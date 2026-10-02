using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>Waitlist requests for a doctor (OA-05).</summary>
public sealed record WaitlistCommand(
    Guid PatientId,
    Guid PractitionerId,
    DateOnly PreferredFrom,
    DateOnly PreferredTo,
    PreferredSession PreferredSession,
    bool IsTelehealth,
    AppointmentChannel Channel,
    string? Notes);

/// <summary>
/// Waitlist handling (OA-05): joining, closing, and offering freed slots. Offers go first to patients who have not
/// been offered anything yet, then by age of the request, so nobody is repeatedly skipped.
/// </summary>
public sealed class WaitlistService(
    HospitalDbContext db,
    AppointmentNotifier notifier,
    SchedulingSettings settings,
    IFeatureFlags features,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<Result<WaitlistEntry>> AddAsync(WaitlistCommand command, CancellationToken ct)
    {
        var window = WaitlistEntry.ValidateWindow(command.PreferredFrom, command.PreferredTo, time.Today());
        if (window.IsFailure)
        {
            return window.Error!;
        }

        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), command.PatientId);
        }

        if (!patient.IsActive || patient.IsDeceased || patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.inactive", "This patient record is not active.");
        }

        var practitioner = await db.Set<Practitioner>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.PractitionerId && p.IsActive, ct);
        if (practitioner is null)
        {
            return Error.Field("practitionerId", "Choose an active doctor.");
        }

        // A request for a video consultation could never be booked while they are switched off.
        if (command.IsTelehealth && !await features.IsEnabledAsync(FeatureKeys.Telehealth, currentUser.FacilityId, ct))
        {
            return FeatureProblems.DisabledError(FeatureCatalog.Telehealth, FeatureProblems.IsForPatients(currentUser));
        }

        if (command.IsTelehealth && !practitioner.AvailableForTelehealth)
        {
            return Error.Rule("appointment.telehealth_unavailable", $"{practitioner.DisplayName} does not offer video consultations.");
        }

        var duplicate = await db.Set<WaitlistEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(w => w.PatientId == command.PatientId && w.PractitionerId == command.PractitionerId
                           && (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered), ct);
        if (duplicate)
        {
            return Error.Conflict("waitlist.duplicate", "This patient is already on the waitlist for this doctor.");
        }

        var facilityId = currentUser.FacilityId ?? await ScheduledFacilityAsync(command.PractitionerId, ct);
        if (facilityId is null)
        {
            return Error.Rule("waitlist.no_schedule", $"{practitioner.DisplayName} has no OPD sessions to wait for.");
        }

        var entry = new WaitlistEntry
        {
            FacilityId = facilityId.Value,
            PatientId = command.PatientId,
            PractitionerId = command.PractitionerId,
            DepartmentId = practitioner.DepartmentId,
            PreferredFrom = command.PreferredFrom,
            PreferredTo = command.PreferredTo,
            PreferredSession = command.PreferredSession,
            IsTelehealth = command.IsTelehealth,
            Channel = command.Channel,
            Notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
            Status = WaitlistStatus.Waiting,
        };
        db.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<Result> CancelAsync(WaitlistEntry entry, CancellationToken ct)
    {
        var result = entry.Cancel(time.GetUtcNow());
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>Closes the patient's open waitlist requests with this doctor once they hold an appointment (same unit of work).</summary>
    internal async Task CloseForBookingAsync(Appointment appointment, Guid? entryId, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.Set<WaitlistEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(w => w.PatientId == appointment.PatientId && (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered)
                        && (w.Id == entryId || w.PractitionerId == appointment.PractitionerId))
            .ToListAsync(ct);

        foreach (var entry in open)
        {
            entry.MarkBooked(appointment.Id, now);
        }

        if (entryId is not null && open.Any(e => e.Id == entryId))
        {
            appointment.WaitlistEntryId = entryId;
        }
    }

    /// <summary>
    /// Offers a slot freed by a cancellation to the first matching waitlisted patient (same unit of work, so the offer
    /// is sent only if the cancellation commits). Slots inside the online cut-off are not offered: the patient could
    /// not book them online anyway.
    /// </summary>
    internal async Task OfferFreedSlotAsync(Appointment freed, DateTimeOffset now, CancellationToken ct)
    {
        var policy = await settings.OnlinePolicyAsync(freed.FacilityId, ct);
        if (freed.StartsAt - now < policy.Cutoff)
        {
            return;
        }

        var calendar = HospitalCalendar.India;
        var date = calendar.DateOf(freed.StartsAt);
        var localTime = calendar.TimeOf(freed.StartsAt);
        var candidates = await db.Set<WaitlistEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(w => w.PractitionerId == freed.PractitionerId && w.PatientId != freed.PatientId
                        && (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered)
                        && w.PreferredFrom <= date && w.PreferredTo >= date && w.IsTelehealth == freed.IsTelehealth)
            .OrderBy(w => w.OfferCount)
            .ThenBy(w => w.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        var next = candidates.FirstOrDefault(w => w.Wants(date, localTime, freed.IsTelehealth));
        if (next is not null && next.Offer(freed.StartsAt, now).IsSuccess)
        {
            await notifier.WaitlistOfferAsync(next, freed.StartsAt, ct);
        }
    }

    /// <summary>Expires requests whose preferred dates have all passed (run by the reminder job).</summary>
    internal async Task<int> ExpirePastAsync(CancellationToken ct)
    {
        var today = time.Today();
        var now = time.GetUtcNow();
        var stale = await db.Set<WaitlistEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(w => (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered) && w.PreferredTo < today)
            .Take(500)
            .ToListAsync(ct);
        return stale.Count(entry => entry.ExpireIfPast(today, now));
    }

    private async Task<Guid?> ScheduledFacilityAsync(Guid practitionerId, CancellationToken ct) =>
        await db.Set<DoctorSchedule>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(s => s.PractitionerId == practitionerId && s.IsActive)
            .OrderBy(s => s.CreatedAt)
            .Select(s => (Guid?)s.FacilityId)
            .FirstOrDefaultAsync(ct);
}
