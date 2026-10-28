using System.Globalization;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Automatic in-patient charges (IPB-01 "auto bed/nursing/doctor-visit charges"): bed-class and NURS-DAY charges per
/// chargeable day, and a VISIT-DR / VISIT-DR-ICU charge per consultant visit. Everything goes through
/// <see cref="IChargePoster"/> with stable source keys, so retries, restarts and the hourly job never double-charge.
/// </summary>
public sealed class InpatientChargeService(
    HospitalDbContext db,
    IChargePoster chargePoster,
    ISettingsService settings,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<InpatientChargeService> logger)
{
    /// <summary>Facility setting: "MidnightCensus" (default) or "CalendarDay".</summary>
    public const string BedDayPolicySetting = "inpatient.bedDayPolicy";

    public const string NursingServiceCode = "NURS-DAY";

    /// <summary>
    /// Posts bed and nursing charges for every due day not yet in the ledger. <paramref name="closingAt"/> treats the stay
    /// as ending at that instant (discharge being processed) so the final bill includes the last day. Safe to call any
    /// number of times; returns the number of charges posted by this call.
    /// </summary>
    public async Task<int> PostDueBedDaysAsync(Guid admissionId, DateTimeOffset? closingAt, CancellationToken ct)
    {
        var count = await StageDueBedDaysAsync(admissionId, closingAt, ct);
        if (count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return count;
    }

    /// <summary>
    /// Like <see cref="PostDueBedDaysAsync"/>, but only stages the charges and their ledger rows: the caller's save commits
    /// them with its own change. Discharge initiation relies on this so that Billing's DischargeInitiated handler, which
    /// runs inside that save, drafts the final bill with the days.
    /// </summary>
    internal async Task<int> StageDueBedDaysAsync(Guid admissionId, DateTimeOffset? closingAt, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null || admission.Status == AdmissionStatus.Cancelled)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var endedAt = admission.DischargedAt
                      ?? closingAt
                      ?? (admission.Status == AdmissionStatus.DischargeInitiated ? now : null);
        var stays = await db.Set<BedAssignment>().AsNoTracking()
            .Where(a => a.AdmissionId == admissionId)
            .OrderBy(a => a.FromAt)
            .Select(a => new BedStay(a.FromAt, a.ToAt, a.BedServiceItemId, a.WardId))
            .ToListAsync(ct);
        var due = BedDayCalculator.DueDays(admission.AdmittedAt, endedAt, stays, now, await PolicyAsync(admission.FacilityId, ct));
        if (due.Count == 0)
        {
            return 0;
        }

        var posted = (await db.Set<BedDayCharge>().AsNoTracking()
                .Where(c => c.AdmissionId == admissionId)
                .Select(c => new { c.ChargeDate, c.Kind })
                .ToListAsync(ct))
            .Select(c => (c.ChargeDate, c.Kind))
            .ToHashSet();
        var nursingItemId = await ServiceItemIdAsync(NursingServiceCode, ct);

        // A bed's day tells Billing whether it is in critical care, which decides its GST (a CCU or an ICCU is kept as an
        // ICU ward).
        var wardIds = due.Where(d => d.WardId is not null).Select(d => d.WardId!.Value).Distinct().ToList();
        var intensiveCare = (await db.Set<Ward>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(w => wardIds.Contains(w.Id))
                .Select(w => new { w.Id, w.Type })
                .ToListAsync(ct))
            .Where(w => Ward.IsCriticalCareType(w.Type))
            .Select(w => w.Id)
            .ToHashSet();

        var count = 0;
        foreach (var day in due)
        {
            var room = day.WardId is { } wardId && intensiveCare.Contains(wardId) ? RoomCharge.IntensiveCare : RoomCharge.Room;
            if (day.BedServiceItemId is { } bedItemId && !posted.Contains((day.Date, DailyChargeKind.Bed))
                && await PostDayAsync(admission, day.Date, DailyChargeKind.Bed, bedItemId, room, ct))
            {
                count++;
            }

            if (nursingItemId is { } nursingId && !posted.Contains((day.Date, DailyChargeKind.Nursing))
                && await PostDayAsync(admission, day.Date, DailyChargeKind.Nursing, nursingId, null, ct))
            {
                count++;
            }
        }

        return count;
    }

    public async Task<Result<ConsultantVisitDto>> RecordVisitAsync(Guid admissionId, RecordConsultantVisitRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var now = time.GetUtcNow();
        if (!admission.AcceptsCharting(now))
        {
            return Error.Rule("admission.chart_closed", "This admission is closed; consultant visits can no longer be recorded.");
        }

        if ((request.PractitionerId ?? currentUser.PractitionerId) is not { } practitionerId)
        {
            return Error.Field("practitionerId", "Choose the consultant who visited.");
        }

        var practitioner = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.Id == practitionerId && p.IsActive)
            .Select(p => new { p.Id, p.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (practitioner is null)
        {
            return Error.Field("practitionerId", "Choose an active consultant.");
        }

        var visitedAt = request.VisitedAt ?? now;
        if (visitedAt < admission.AdmittedAt || (admission.DischargedAt is { } dischargedAt && visitedAt > dischargedAt))
        {
            return Error.Rule("visit.outside_stay", "The visit time must fall within the patient's stay.");
        }

        var from = visitedAt - ConsultantVisit.DuplicateWindow;
        var to = visitedAt + ConsultantVisit.DuplicateWindow;
        if (await db.Set<ConsultantVisit>().AnyAsync(v => v.AdmissionId == admissionId && v.PractitionerId == practitioner.Id && v.VisitedAt >= from && v.VisitedAt <= to, ct))
        {
            return Error.Conflict("visit.duplicate", $"A visit by {practitioner.DisplayName} is already recorded within 30 minutes of this time.");
        }

        var bed = await db.Set<BedAssignment>().AsNoTracking()
            .Where(a => a.AdmissionId == admissionId && a.FromAt <= visitedAt)
            .OrderByDescending(a => a.FromAt)
            .Select(a => new { a.WardId, a.IsCriticalCare })
            .FirstOrDefaultAsync(ct);

        var visit = new ConsultantVisit
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            PractitionerId = practitioner.Id,
            VisitedAt = visitedAt,
            WardId = bed?.WardId ?? admission.WardId,
            IsCriticalCare = bed?.IsCriticalCare ?? false,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        };
        visit.ServiceItemId = await ServiceItemIdAsync(visit.ServiceCode, ct);
        db.Add(visit);
        await db.SaveChangesAsync(ct);

        await PostVisitChargeAsync(visit, admission.VisitId, ct);
        return ToDto(visit, practitioner.DisplayName);
    }

    /// <summary>Removes a visit recorded in error and reverses its charge (fails if Billing has already billed it).</summary>
    public async Task<Result> CancelVisitAsync(Guid visitId, string reason, CancellationToken ct)
    {
        var visit = await db.Set<ConsultantVisit>().FirstOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit is null)
        {
            return Error.NotFound("Consultant visit", visitId);
        }

        if (visit.ChargeId is not null)
        {
            var reversed = await chargePoster.CancelAsync(DailyChargeKeys.ConsultantVisitSource, visit.Id.ToString(), reason.Trim(), ct);
            if (reversed.IsFailure)
            {
                return reversed;
            }
        }

        visit.ChargeError = Truncate($"Cancelled: {reason.Trim()}", 300);
        db.Remove(visit);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Retries consultant-visit charges Billing did not accept earlier (for example while it was unreachable).</summary>
    public async Task<int> RetryVisitChargesAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-7);
        var pending = await db.Set<ConsultantVisit>()
            .Where(v => v.ChargeId == null && v.VisitedAt >= since)
            .OrderBy(v => v.VisitedAt)
            .Take(200)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var admissionIds = pending.Select(v => v.AdmissionId).Distinct().ToList();
        var admissions = await db.Set<Admission>().AsNoTracking()
            .Where(a => admissionIds.Contains(a.Id))
            .Select(a => new { a.Id, a.VisitId, a.Status })
            .ToDictionaryAsync(a => a.Id, ct);

        var charged = 0;
        foreach (var visit in pending)
        {
            if (admissions.TryGetValue(visit.AdmissionId, out var admission) && admission.Status != AdmissionStatus.Cancelled)
            {
                visit.ServiceItemId ??= await ServiceItemIdAsync(visit.ServiceCode, ct);
                if (await PostVisitChargeAsync(visit, admission.VisitId, ct))
                {
                    charged++;
                }
            }
        }

        return charged;
    }

    /// <summary>
    /// Reverses every charge this module posted for an admission (cancelled admission). Runs inside the caller's
    /// transaction, so if Billing refuses one reversal (already billed) nothing is reversed and the cancellation fails.
    /// </summary>
    public async Task<Result> ReverseAllAsync(Guid admissionId, string reason, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var dayCharges = await db.Set<BedDayCharge>().Where(c => c.AdmissionId == admissionId && c.CancelledAt == null).ToListAsync(ct);
        foreach (var charge in dayCharges)
        {
            var reversed = await chargePoster.CancelAsync(DailyChargeKeys.SourceFor(charge.Kind), charge.SourceReference, reason, ct);
            if (reversed.IsFailure)
            {
                return reversed;
            }

            charge.CancelledAt = now;
        }

        var visits = await db.Set<ConsultantVisit>().Where(v => v.AdmissionId == admissionId && v.ChargeId != null).ToListAsync(ct);
        foreach (var visit in visits)
        {
            var reversed = await chargePoster.CancelAsync(DailyChargeKeys.ConsultantVisitSource, visit.Id.ToString(), reason, ct);
            if (reversed.IsFailure)
            {
                return reversed;
            }

            visit.ChargeError = "Charge reversed: admission cancelled.";
        }

        return Result.Success();
    }

    internal async Task<Guid?> ServiceItemIdAsync(string code, CancellationToken ct) =>
        await db.Set<ServiceItem>().AsNoTracking().Where(s => s.Code == code && s.IsActive).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);

    internal static ConsultantVisitDto ToDto(ConsultantVisit v, string? practitionerName) => new(
        v.Id, v.PractitionerId, practitionerName, v.VisitedAt, v.IsCriticalCare, v.ServiceCode, v.Notes, v.ChargeId is not null, v.ChargeError);

    private async Task<BedDayPolicy> PolicyAsync(Guid facilityId, CancellationToken ct)
    {
        var configured = await settings.GetAsync<string>(BedDayPolicySetting, facilityId, ct);
        return Enum.TryParse<BedDayPolicy>(configured, ignoreCase: true, out var policy) && Enum.IsDefined(policy)
            ? policy
            : BedDayPolicy.MidnightCensus;
    }

    /// <param name="room">For a bed's day, the kind of room (GST on room rent); null for a nursing day.</param>
    private async Task<bool> PostDayAsync(Admission admission, DateOnly date, DailyChargeKind kind, Guid serviceItemId, RoomCharge? room, CancellationToken ct)
    {
        var reference = DailyChargeKeys.For(admission.Id, date);
        var label = kind == DailyChargeKind.Bed ? "Bed charges" : "Nursing charges";
        var result = await chargePoster.PostAsync(new ChargeRequest
        {
            PatientId = admission.PatientId,
            VisitId = admission.VisitId,
            AdmissionId = admission.Id,
            ServiceItemId = serviceItemId,
            Quantity = 1,
            Description = $"{label} for {date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}",
            SourceModule = DailyChargeKeys.SourceFor(kind),
            SourceReference = reference,
            DepartmentId = admission.DepartmentId,
            ServiceDate = IndiaClock.StartOfDayUtc(date),
            Room = room,
        }, ct);

        if (result.IsFailure)
        {
            logger.LogWarning("Day charge {Reference} ({Kind}) was not accepted by billing: {Code}", reference, kind, result.Error!.Code);
            return false;
        }

        db.Add(new BedDayCharge
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            ChargeDate = date,
            Kind = kind,
            ServiceItemId = serviceItemId,
            ChargeId = result.Value,
            SourceReference = reference,
            PostedAt = time.GetUtcNow(),
        });
        return true;
    }

    private async Task<bool> PostVisitChargeAsync(ConsultantVisit visit, Guid admissionVisitId, CancellationToken ct)
    {
        if (visit.ServiceItemId is not { } serviceItemId)
        {
            visit.ChargeError = $"Service {visit.ServiceCode} is not in the service catalogue.";
            await db.SaveChangesAsync(ct);
            return false;
        }

        var result = await chargePoster.PostAsync(new ChargeRequest
        {
            PatientId = visit.PatientId,
            VisitId = admissionVisitId,
            AdmissionId = visit.AdmissionId,
            ServiceItemId = serviceItemId,
            Quantity = 1,
            Description = visit.IsCriticalCare ? "Consultant visit (ICU)" : "Consultant visit",
            SourceModule = DailyChargeKeys.ConsultantVisitSource,
            SourceReference = visit.Id.ToString(),
            PractitionerId = visit.PractitionerId,
            ServiceDate = visit.VisitedAt,
        }, ct);

        if (result.IsSuccess)
        {
            visit.ChargeId = result.Value;
            visit.ChargeError = null;
        }
        else
        {
            visit.ChargeError = Truncate(result.Error!.Message, 300);
            logger.LogWarning("Consultant visit {VisitId} was not charged: {Code}", visit.Id, result.Error.Code);
        }

        await db.SaveChangesAsync(ct);
        return result.IsSuccess;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
