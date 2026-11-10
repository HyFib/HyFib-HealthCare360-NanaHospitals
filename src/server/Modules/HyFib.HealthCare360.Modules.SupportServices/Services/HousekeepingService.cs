using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Housekeeping;
using HyFib.HealthCare360.Modules.SupportServices.Features.Housekeeping;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Housekeeping beyond the bed turnaround (Inpatient's terminal cleaning of a bed): areas cleaned on a schedule against
/// their checklists, spill, terminal and routine cleaning asked for by the wards with the response time measured against
/// each kind's target, and every cleaning verified by a supervisor who did not do it.
/// </summary>
internal sealed class HousekeepingService(HospitalDbContext db, ICurrentUser currentUser, INotificationService notifications, TimeProvider time)
{
    public async Task<IReadOnlyList<CleaningAreaDto>> AreasAsync(bool includeInactive, CancellationToken ct)
    {
        var areas = await db.Set<CleaningArea>().AsNoTracking().Where(a => includeInactive || a.IsActive).OrderBy(a => a.RiskZone).ThenBy(a => a.Code).ToListAsync(ct);
        var wards = await WardNamesAsync(areas.Select(a => a.WardId), ct);
        return areas.Select(a => AreaDto(a, wards)).ToList();
    }

    public async Task<Result<CleaningAreaDto>> SaveAreaAsync(Guid? id, SaveCleaningAreaRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<CleaningArea>().AnyAsync(a => a.Code == code && a.Id != id, ct))
        {
            return Error.Conflict("housekeeping.code_in_use", $"Area code {code} is already in use.");
        }

        if (request.WardId is { } wardId && !await db.Set<Ward>().AnyAsync(w => w.Id == wardId, ct))
        {
            return Error.Field("wardId", "Choose a ward of this hospital.");
        }

        CleaningArea area;
        if (id is { } existing)
        {
            var found = await db.Set<CleaningArea>().FirstOrDefaultAsync(a => a.Id == existing, ct);
            if (found is null)
            {
                return Error.NotFound("Cleaning area", existing);
            }

            area = found;
            db.SetExpectedVersion(area, request.Version ?? 0);
        }
        else
        {
            area = new CleaningArea { FacilityId = facility };
            db.Add(area);
        }

        area.Code = code;
        area.Name = request.Name.Trim();
        area.WardId = request.WardId;
        area.RiskZone = request.RiskZone;
        area.Frequency = request.Frequency;
        area.TimesPerDay = request.Frequency == CleaningFrequency.Daily ? request.TimesPerDay : 1;
        area.WeekDays = request.Frequency == CleaningFrequency.Weekly ? [.. (request.WeekDays ?? []).Distinct().Order()] : [];
        area.Checklist = [.. request.Checklist.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct()];
        area.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return AreaDto(area, await WardNamesAsync([area.WardId], ct));
    }

    /// <summary>The day's cleanings due, each with what was done about it (the latest record of its slot).</summary>
    public async Task<CleaningBoard> BoardAsync(DateOnly date, CancellationToken ct)
    {
        var areas = await db.Set<CleaningArea>().AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.RiskZone).ThenBy(a => a.Code).ToListAsync(ct);
        var records = await db.Set<CleaningRecord>().AsNoTracking().Where(r => r.Date == date).ToListAsync(ct);
        var wards = await WardNamesAsync(areas.Select(a => a.WardId), ct);
        var slots = areas.SelectMany(a => a.SlotsOn(date).Select(slot => new CleaningSlot(
                a.Id, a.Code, a.Name, a.WardId is { } w ? wards.GetValueOrDefault(w) : null, a.RiskZone, slot, a.Checklist,
                records.Where(r => r.AreaId == a.Id && r.Slot == slot).OrderByDescending(r => r.CleanedAt).Select(RecordDto).FirstOrDefault())))
            .ToList();
        return new CleaningBoard(date, slots, slots.Count, slots.Count(s => s.Record is null || s.Record.Status == CleaningStatus.Redo),
            slots.Count(s => s.Record?.Status == CleaningStatus.AwaitingVerification), slots.Count(s => s.Record?.Status == CleaningStatus.Redo));
    }

    public async Task<Result<CleaningRecordDto>> RecordAsync(Guid areaId, RecordCleaningRequest request, CancellationToken ct)
    {
        var area = await db.Set<CleaningArea>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == areaId, ct);
        if (area is null)
        {
            return Error.NotFound("Cleaning area", areaId);
        }

        var today = time.Today();
        if (request.Date > today || request.Date < today.AddDays(-1))
        {
            return Error.Field("date", "Record a cleaning on the day it is done (or, after midnight, the day before).");
        }

        if (!area.SlotsOn(request.Date).Contains(request.Slot))
        {
            return Error.Field("slot", $"{area.Name} has no cleaning {request.Slot} due on {request.Date:dd MMM yyyy}.");
        }

        var answered = CleaningRules.CheckList(area.Checklist, request.Checklist);
        if (answered.IsFailure)
        {
            return answered.Error!;
        }

        if (await db.Set<CleaningRecord>().AnyAsync(r => r.AreaId == areaId && r.Date == request.Date && r.Slot == request.Slot && r.Status != CleaningStatus.Redo, ct))
        {
            return Error.Conflict("housekeeping.slot_done", $"Cleaning {request.Slot} of {area.Name} on {request.Date:dd MMM yyyy} is already recorded.");
        }

        var record = new CleaningRecord
        {
            FacilityId = area.FacilityId,
            AreaId = area.Id,
            Date = request.Date,
            Slot = request.Slot,
            CleanedAt = time.GetUtcNow(),
            CleanedBy = currentUser.UserId,
            CleanedByName = Name(),
            Checklist = [.. request.Checklist.Where(c => area.Checklist.Contains(c.Item))],
            Notes = Clean(request.Notes),
        };
        db.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Error.Conflict("housekeeping.slot_done", $"Cleaning {request.Slot} of {area.Name} was recorded by someone else just now.");
        }

        return RecordDto(record);
    }

    public async Task<Result<CleaningRecordDto>> VerifyRecordAsync(Guid id, VerifyCleaningRequest request, CancellationToken ct)
    {
        var record = await db.Set<CleaningRecord>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null)
        {
            return Error.NotFound("Cleaning", id);
        }

        db.SetExpectedVersion(record, request.Version);
        var verified = record.Verify(currentUser.UserId, Name(), request.Passed, request.Remarks, time.GetUtcNow());
        if (verified.IsFailure)
        {
            return verified.Error!;
        }

        await db.SaveChangesAsync(ct);
        if (!request.Passed && record.CleanedBy is { } cleaner)
        {
            await notifications.NotifyUsersAsync([cleaner], "Cleaning sent back", $"Clean again: {record.VerificationRemarks}", "/housekeeping", "housekeeping", "warning", ct);
        }

        return RecordDto(record);
    }

    public async Task<IReadOnlyList<CleaningRequestDto>> RequestsAsync(bool openOnly, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (start, end) = (Start(from), Start(to.AddDays(1)));
        var requests = db.Set<CleaningRequest>().AsNoTracking();
        requests = openOnly
            ? requests.Where(r => r.Status == CleaningRequestStatus.Open || r.Status == CleaningRequestStatus.Acknowledged || r.Status == CleaningRequestStatus.Redo
                                  || r.Status == CleaningRequestStatus.AwaitingVerification)
            : requests.Where(r => r.RequestedAt >= start && r.RequestedAt < end);
        var rows = await requests.OrderBy(r => r.Kind).ThenBy(r => r.RequestedAt).Take(500).ToListAsync(ct);
        var wards = await WardNamesAsync(rows.Select(r => r.WardId), ct);
        var now = time.GetUtcNow();
        return rows.Select(r => RequestDto(r, wards, now)).ToList();
    }

    public async Task<Result<CleaningRequestDto>> RaiseAsync(RaiseCleaningRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        if (request.WardId is { } wardId && !await db.Set<Ward>().AnyAsync(w => w.Id == wardId, ct))
        {
            return Error.Field("wardId", "Choose a ward of this hospital.");
        }

        var raised = new CleaningRequest
        {
            FacilityId = facility,
            Kind = request.Kind,
            WardId = request.WardId,
            Location = request.Location.Trim(),
            Details = Clean(request.Details),
            RequestedAt = time.GetUtcNow(),
            RequestedBy = currentUser.UserId,
            RequestedByName = Name(),
        };
        db.Add(raised);
        await db.SaveChangesAsync(ct);
        var target = (int)CleaningRequest.Target(raised.Kind).TotalMinutes;
        await notifications.NotifyPermissionHoldersAsync(SupportServicesPermissions.HousekeepingWork, facility,
            raised.Kind == CleaningRequestKind.Spill ? "Spill to clean" : $"{raised.Kind} cleaning asked for",
            $"{raised.Location}: someone on the way within {target} minutes.{(raised.Details is null ? string.Empty : $" {raised.Details}")}", "/housekeeping",
            "housekeeping", raised.Kind == CleaningRequestKind.Spill ? "critical" : "info", ct);
        return RequestDto(raised, await WardNamesAsync([raised.WardId], ct), raised.RequestedAt);
    }

    public Task<Result<CleaningRequestDto>> AcknowledgeAsync(Guid id, uint version, CancellationToken ct) =>
        StepAsync(id, version, (r, now) => r.Acknowledge(currentUser.UserId, Name(), now), ct);

    public Task<Result<CleaningRequestDto>> CompleteAsync(Guid id, CompleteCleaningRequest request, CancellationToken ct) =>
        StepAsync(id, request.Version, (r, now) => r.Complete(request.Checklist, request.Notes, currentUser.UserId, Name(), now), ct);

    public Task<Result<CleaningRequestDto>> VerifyRequestAsync(Guid id, VerifyCleaningRequest request, CancellationToken ct) =>
        StepAsync(id, request.Version, (r, now) => r.Verify(currentUser.UserId, Name(), request.Passed, request.Remarks, now), ct);

    public Task<Result<CleaningRequestDto>> CancelAsync(Guid id, CancelCleaningRequest request, CancellationToken ct) =>
        StepAsync(id, request.Version, (r, _) => r.Cancel(request.Reason), ct);

    public async Task<HousekeepingReport> ReportAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (start, end) = (Start(from), Start(to.AddDays(1)));
        var requests = await db.Set<CleaningRequest>().AsNoTracking()
            .Where(r => r.RequestedAt >= start && r.RequestedAt < end && r.Status != CleaningRequestStatus.Cancelled).ToListAsync(ct);
        var now = time.GetUtcNow();
        var kinds = Enum.GetValues<CleaningRequestKind>().Select(k =>
        {
            var of = requests.Where(r => r.Kind == k).ToList();
            return new RequestKindSummary(k, of.Count, (int)CleaningRequest.Target(k).TotalMinutes,
                HousekeepingStats.Median(of.Where(r => r.ResponseMinutes is not null).Select(r => (double)r.ResponseMinutes!.Value)),
                of.Count(r => r.ResponseLate(now)), of.Count(r => r.Status == CleaningRequestStatus.Redo || r.VerificationRemarks is not null && r.Status != CleaningRequestStatus.Verified));
        }).ToList();

        var areas = await db.Set<CleaningArea>().AsNoTracking().Where(a => a.IsActive).ToListAsync(ct);
        var records = await db.Set<CleaningRecord>().AsNoTracking().Where(r => r.Date >= from && r.Date <= to).ToListAsync(ct);
        var due = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            due += areas.Sum(a => a.SlotsOn(day).Count);
        }

        var standing = records.Where(r => r.Status != CleaningStatus.Redo).ToList();
        return new HousekeepingReport(from, to, kinds, due, standing.Count, standing.Count(r => r.Status == CleaningStatus.Verified),
            records.Count(r => r.Status == CleaningStatus.Redo));
    }

    private async Task<Result<CleaningRequestDto>> StepAsync(Guid id, uint version, Func<CleaningRequest, DateTimeOffset, Result> step, CancellationToken ct)
    {
        var request = await db.Set<CleaningRequest>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return Error.NotFound("Cleaning request", id);
        }

        db.SetExpectedVersion(request, version);
        var now = time.GetUtcNow();
        var done = step(request, now);
        if (done.IsFailure)
        {
            return done.Error!;
        }

        await db.SaveChangesAsync(ct);
        if (request.Status == CleaningRequestStatus.Redo && request.CompletedBy is { } cleaner)
        {
            await notifications.NotifyUsersAsync([cleaner], "Cleaning sent back", $"{request.Location}: {request.VerificationRemarks}", "/housekeeping", "housekeeping", "warning", ct);
        }

        return RequestDto(request, await WardNamesAsync([request.WardId], ct), now);
    }

    private async Task<Dictionary<Guid, string>> WardNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return wanted.Count == 0 ? [] : await db.Set<Ward>().AsNoTracking().Where(w => wanted.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
    }

    private static CleaningAreaDto AreaDto(CleaningArea a, IReadOnlyDictionary<Guid, string> wards) => new(
        a.Id, a.Code, a.Name, a.WardId, a.WardId is { } w ? wards.GetValueOrDefault(w) : null, a.RiskZone, a.Frequency, a.TimesPerDay, a.WeekDays, a.Checklist,
        a.IsActive, a.Version);

    private CleaningRecordDto RecordDto(CleaningRecord r) => new(
        r.Id, r.AreaId, r.Date, r.Slot, r.CleanedAt, r.CleanedByName, r.Checklist, r.Notes, r.Status, r.VerifiedAt, r.VerifiedByName, r.VerificationRemarks,
        r.CleanedBy is { } by && by == currentUser.UserId, r.Version);

    private CleaningRequestDto RequestDto(CleaningRequest r, IReadOnlyDictionary<Guid, string> wards, DateTimeOffset now) => new(
        r.Id, r.Kind, r.WardId, r.WardId is { } w ? wards.GetValueOrDefault(w) : null, r.Location, r.Details, r.RequestedAt, r.RequestedByName, r.Status,
        (int)CleaningRequest.Target(r.Kind).TotalMinutes, r.AcknowledgedAt, r.AcknowledgedByName, r.ResponseMinutes, r.ResponseLate(now), r.CompletedAt,
        r.CompletedByName, r.CompletionMinutes, CleaningRequest.ChecklistFor(r.Kind), r.Checklist, r.Notes, r.VerifiedAt, r.VerifiedByName,
        r.VerificationRemarks, r.CompletedBy is { } by && by == currentUser.UserId, r.Version);

    private string Name() => currentUser.DisplayName ?? currentUser.UserName ?? "Staff";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
}

/// <summary>Medians for the housekeeping report.</summary>
public static class HousekeepingStats
{
    public static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? null
            : sorted.Count % 2 == 1 ? Math.Round(sorted[sorted.Count / 2], 1)
            : Math.Round((sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2, 1);
    }
}
