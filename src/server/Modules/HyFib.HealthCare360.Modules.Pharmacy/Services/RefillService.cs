using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Refills;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// Prescription refills for chronic patients (PH-05, ERX-05, PP-05). A pharmacist records the prescriber's
/// refill authorisation on the original prescription; patients (portal, family-scoped) or staff request a refill;
/// approval uses one refill, queues a new entry to dispense and tells the patient it is being prepared.
/// </summary>
public sealed class RefillService(
    HospitalDbContext db,
    INumberSeries numbers,
    PortalPatientScope portal,
    PatientAccessGuard guard,
    INotificationService notifications,
    PharmacyRealtime realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string Series = "pharmacy.refill";
    public const string Format = "{FAC}RF{YY}{SEQ:6}";
    public const string ReadyTemplate = "pharmacy.refill_ready";
    public const string ReminderTemplate = "pharmacy.refill_reminder";

    public async Task<Result> AuthorizeAsync(Guid entryId, int refills, DateOnly validUntil, string note, CancellationToken ct)
    {
        var entry = await db.Set<PharmacyQueueEntry>().FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null)
        {
            return Error.NotFound(nameof(PharmacyQueueEntry), entryId);
        }

        var authorised = entry.AuthorizeRefills(refills, validUntil, note.Trim(), time.Today());
        if (authorised.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return authorised;
    }

    /// <summary>Raises a refill request; portal users may only request for patients linked to their mobile number.</summary>
    public async Task<Result<RefillRequestCreated>> RequestAsync(CreateRefillRequest request, RefillChannel channel, CancellationToken ct)
    {
        var entry = await db.Set<PharmacyQueueEntry>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.QueueEntryId, ct);
        if (entry is null)
        {
            return Error.NotFound("Prescription", request.QueueEntryId);
        }

        var access = channel == RefillChannel.Portal
            ? await portal.EnsureAccessAsync(entry.PatientId, ct)
            : await guard.EnsureCanOpenAsync(entry.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var refillable = entry.EnsureRefillable(time.Today());
        if (refillable.IsFailure)
        {
            return refillable.Error!;
        }

        if (await db.Set<RefillRequest>().AnyAsync(r => r.QueueEntryId == entry.Id && r.Status == RefillStatus.Requested, ct))
        {
            return Error.Conflict("refill.already_requested", "A refill for this prescription is already waiting for the pharmacy.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var refill = new RefillRequest
        {
            FacilityId = entry.FacilityId,
            Number = await numbers.NextAsync(Series, entry.FacilityId, Format, NumberResetPolicy.Yearly, ct),
            QueueEntryId = entry.Id,
            PatientId = entry.PatientId,
            Channel = channel,
            RequestedByUserId = currentUser.UserId,
            RequestedAt = time.GetUtcNow(),
            PreferredPickupDate = request.PreferredPickupDate,
            Notes = request.Notes?.Trim(),
        };
        db.Add(refill);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await notifications.NotifyPermissionHoldersAsync(
            PharmacyPermissions.Refills, entry.FacilityId, $"Refill requested: {entry.PrescriptionNumber}",
            channel == RefillChannel.Portal ? "Requested by the patient on the portal." : "Requested at the hospital.",
            $"/pharmacy/refills/{refill.Id}", "pharmacy", "info", ct);
        await realtime.RefillChangedAsync(entry.FacilityId, refill.Id, refill.Status, ct);
        return new RefillRequestCreated(refill.Id, refill.Number);
    }

    public async Task<Result<Guid>> ApproveAsync(Guid requestId, CancellationToken ct)
    {
        var request = await db.Set<RefillRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(RefillRequest), requestId);
        }

        var entry = await db.Set<PharmacyQueueEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == request.QueueEntryId, ct);
        if (entry is null)
        {
            return Error.NotFound(nameof(PharmacyQueueEntry), request.QueueEntryId);
        }

        var now = time.GetUtcNow();
        var refill = entry.CreateRefill(time.Today(), now);
        if (refill.IsFailure)
        {
            return refill.Error!;
        }

        var approved = request.Approve(currentUser.UserId, refill.Value.Id, now);
        if (approved.IsFailure)
        {
            return approved.Error!;
        }

        db.Add(refill.Value);
        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == entry.PatientId)
            .Select(p => new { p.Id, p.FullName, p.Phone, p.Email, p.PreferredLanguage })
            .FirstOrDefaultAsync(ct);
        if (patient?.Phone is not null)
        {
            notifications.Queue(ReadyTemplate,
                new NotificationRecipient(patient.Phone, patient.Email, patient.Id, patient.FullName),
                new Dictionary<string, string>
                {
                    ["reference"] = entry.PrescriptionNumber,
                    ["date"] = (request.PreferredPickupDate ?? time.Today()).ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture),
                },
                [NotificationChannel.Sms, NotificationChannel.WhatsApp],
                TimeSpan.FromDays(2));
        }

        await db.SaveChangesAsync(ct);
        await realtime.RefillChangedAsync(request.FacilityId, request.Id, request.Status, ct);
        await realtime.QueueChangedAsync(refill.Value.FacilityId, refill.Value.Id, refill.Value.Status, ct);
        return refill.Value.Id;
    }

    public async Task<Result> RejectAsync(Guid requestId, string reason, CancellationToken ct)
    {
        var request = await db.Set<RefillRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(RefillRequest), requestId);
        }

        var rejected = request.Reject(currentUser.UserId, reason.Trim(), time.GetUtcNow());
        if (rejected.IsFailure)
        {
            return rejected;
        }

        await db.SaveChangesAsync(ct);
        await realtime.RefillChangedAsync(request.FacilityId, request.Id, request.Status, ct);
        return Result.Success();
    }

    /// <summary>Portal view: refillable prescriptions and recent requests of every patient linked to the signed-in mobile number.</summary>
    public async Task<PortalRefillView> PortalViewAsync(CancellationToken ct)
    {
        var patientIds = await portal.AccessiblePatientIdsAsync(ct);
        if (patientIds.Count == 0)
        {
            return new PortalRefillView([], []);
        }

        var ids = patientIds.ToList();
        var today = time.Today();
        var entries = await db.Set<PharmacyQueueEntry>().AsNoTracking().Include(e => e.Lines)
            .Where(e => ids.Contains(e.PatientId) && e.RefillOfEntryId == null && !e.IsInpatient && !e.HasControlledDrugs
                        && e.Status == QueueEntryStatus.Dispensed && e.RefillsAuthorized > e.RefillsUsed
                        && (e.RefillValidUntil == null || e.RefillValidUntil >= today))
            .OrderByDescending(e => e.SignedAt)
            .Take(50)
            .ToListAsync(ct);
        var requests = await Requests(db.Set<RefillRequest>().AsNoTracking().Where(r => ids.Contains(r.PatientId)))
            .Take(50)
            .ToListAsync(ct);
        var names = await db.Set<Patient>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
        var practitionerIds = entries.Select(e => e.PractitionerId).Distinct().ToList();
        var prescribers = await db.Set<Practitioner>().AsNoTracking().Where(p => practitionerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var open = requests.Where(r => r.Status == RefillStatus.Requested).Select(r => r.QueueEntryId).ToHashSet();

        return new PortalRefillView(
            entries.Select(e => new RefillablePrescription(
                e.Id, e.PrescriptionNumber, e.PatientId, names.GetValueOrDefault(e.PatientId) ?? string.Empty, e.SignedAt,
                prescribers.GetValueOrDefault(e.PractitionerId), e.RefillsRemaining, e.RefillValidUntil,
                e.Lines.Select(l => l.DrugName).ToList(), open.Contains(e.Id))).ToList(),
            requests);
    }

    internal IQueryable<RefillRequestDto> Requests(IQueryable<RefillRequest> requests) =>
        from r in requests
        join e in db.Set<PharmacyQueueEntry>() on r.QueueEntryId equals e.Id
        join p in db.Set<Patient>() on r.PatientId equals p.Id
        orderby r.RequestedAt descending
        select new RefillRequestDto(
            r.Id, r.Number, r.QueueEntryId, e.PrescriptionNumber, r.PatientId, p.FullName, p.Uhid, r.Channel, r.Status, r.RequestedAt,
            r.PreferredPickupDate, r.Notes, r.RejectReason, r.RefillEntryId);
}
