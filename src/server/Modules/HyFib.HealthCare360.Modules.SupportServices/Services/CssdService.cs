using System.Text.Json;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// CSSD counter work: department requests (CSSD-02), soiled receipts with count checks (CSSD-03), issue of sterile packs
/// with expiry and use on patients (CSSD-04), recall acknowledgement (CSSD-05) and the CSSD summary report.
/// </summary>
public sealed class CssdService(
    HospitalDbContext db,
    INumberSeries numbers,
    INotificationService notifications,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string RequestSeries = "cssd-request";
    public const string RequestFormat = "{FAC}CQ{YY}{SEQ:6}";

    /// <summary>Packs expiring within this many days are shown as "expiring soon".</summary>
    public const int ExpiringSoonDays = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<CssdRequestDto>> CreateRequestAsync(CreateCssdRequestRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Department>().AnyAsync(d => d.Id == request.DepartmentId && d.IsActive, ct))
        {
            return Error.Field("departmentId", "Choose an active department.");
        }

        var setIds = request.Lines.Select(l => l.InstrumentSetId).Distinct().ToList();
        if (await db.Set<InstrumentSet>().CountAsync(s => setIds.Contains(s.Id) && s.IsActive, ct) != setIds.Count)
        {
            return Error.Field("lines", "Every requested set must be an active instrument set.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var cssdRequest = new CssdRequest
        {
            FacilityId = facilityId,
            RequestNumber = await numbers.NextAsync(RequestSeries, facilityId, RequestFormat, NumberResetPolicy.Yearly, ct),
            DepartmentId = request.DepartmentId,
            RequestedByName = currentUser.DisplayName,
            RequiredBy = request.RequiredBy,
            Priority = request.Priority,
            Status = CssdRequestStatus.Open,
            Notes = Clean(request.Notes),
        };
        foreach (var line in request.Lines.GroupBy(l => l.InstrumentSetId))
        {
            cssdRequest.Lines.Add(new CssdRequestLine { CssdRequestId = cssdRequest.Id, InstrumentSetId = line.Key, Quantity = line.Sum(l => l.Quantity) });
        }

        db.Add(cssdRequest);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (request.Priority == CssdPriority.Urgent)
        {
            await notifications.NotifyPermissionHoldersAsync(SupportServicesPermissions.CssdProcess, facilityId,
                $"Urgent CSSD request {cssdRequest.RequestNumber}", "A department needs sterile sets urgently.", "/cssd/requests", "cssd", "warning", ct);
        }

        return (await RequestDtosAsync(db.Set<CssdRequest>().AsNoTracking().Where(r => r.Id == cssdRequest.Id), ct)).Single();
    }

    public async Task<Result> CancelRequestAsync(Guid requestId, string reason, CancellationToken ct)
    {
        var cssdRequest = await db.Set<CssdRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (cssdRequest is null)
        {
            return Error.NotFound("CSSD request", requestId);
        }

        var cancelled = cssdRequest.Cancel(reason);
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Receives used trays, checks each against its count sheet and closes the returned packs' issues.</summary>
    public async Task<Result<SoiledReceiptDto>> ReceiveSoiledAsync(ReceiveSoiledRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Department>().AnyAsync(d => d.Id == request.DepartmentId, ct))
        {
            return Error.Field("departmentId", "Choose a department.");
        }

        var setIds = request.Lines.Select(l => l.InstrumentSetId).Distinct().ToList();
        var sets = await db.Set<InstrumentSet>().AsNoTracking().Include(s => s.Items).Where(s => setIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        if (sets.Count != setIds.Count)
        {
            return Error.Field("lines", "Every tray must be a known instrument set.");
        }

        var issueIds = request.Lines.Where(l => l.SetIssueId != null).Select(l => l.SetIssueId!.Value).ToList();
        var issues = await db.Set<SetIssue>().Where(i => issueIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var now = time.GetUtcNow();
        var receipt = new SoiledReceipt
        {
            FacilityId = facilityId,
            DepartmentId = request.DepartmentId,
            ReceivedAt = now,
            ReceivedByName = currentUser.DisplayName,
            HandedOverByName = Clean(request.HandedOverByName),
            Notes = Clean(request.Notes),
        };

        foreach (var line in request.Lines)
        {
            var set = sets[line.InstrumentSetId];
            var check = InstrumentCountCheck.Compare(set.CountSheet, line.Counts.Select(c => new CountedItem(c.ItemName, c.Received, c.Damaged)).ToList());
            receipt.Lines.Add(new SoiledReceiptLine
            {
                SoiledReceiptId = receipt.Id,
                InstrumentSetId = set.Id,
                SetIssueId = line.SetIssueId,
                ExpectedCount = check.Expected,
                ReceivedCount = check.Received,
                MissingCount = check.Missing,
                ExtraCount = check.Extra,
                DamagedCount = check.Damaged,
                Discrepancies = check.HasDiscrepancy ? Fit(string.Join("; ", check.Discrepancies), 1000) : null,
                CountDetails = JsonSerializer.Serialize(check.Items, Json),
            });

            if (line.SetIssueId is { } issueId && issues.TryGetValue(issueId, out var issue))
            {
                if (issue.InstrumentSetId != set.Id)
                {
                    return Error.Field("lines", "A returned pack does not match the tray it was received with.");
                }

                issue.MarkReturned(receipt.Id, now);
                var pack = await db.Set<SterilePack>().FirstOrDefaultAsync(p => p.Id == issue.PackId, ct);
                if (pack is not null && pack.Status is PackStatus.Issued or PackStatus.Recalled)
                {
                    pack.Status = PackStatus.Returned;
                }
            }
        }

        receipt.HasDiscrepancy = receipt.Lines.Any(l => l.Discrepancies is not null);
        db.Add(receipt);
        await db.SaveChangesAsync(ct);

        if (receipt.HasDiscrepancy)
        {
            // A missing instrument after a procedure is a patient-safety signal (possible retained item), not just stock loss.
            await notifications.NotifyPermissionHoldersAsync(SupportServicesPermissions.CssdRecalls, facilityId,
                "Instrument count discrepancy",
                string.Join(" | ", receipt.Lines.Where(l => l.Discrepancies is not null).Select(l => $"{sets[l.InstrumentSetId].Name}: {l.Discrepancies}")),
                "/cssd/receipts", "cssd", "warning", ct);
        }

        return (await ReceiptDtosAsync(db.Set<SoiledReceipt>().AsNoTracking().Where(r => r.Id == receipt.Id), ct)).Single();
    }

    /// <summary>Issues sterile packs to a department; expired, unreleased or recalled packs are refused.</summary>
    public async Task<Result<IReadOnlyList<SetIssueDto>>> IssueAsync(IssuePacksRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Department>().AnyAsync(d => d.Id == request.DepartmentId && d.IsActive, ct))
        {
            return Error.Field("departmentId", "Choose an active department.");
        }

        CssdRequest? cssdRequest = null;
        if (request.CssdRequestId is { } requestId)
        {
            cssdRequest = await db.Set<CssdRequest>().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == requestId, ct);
            if (cssdRequest is null || !cssdRequest.IsOpen || cssdRequest.DepartmentId != request.DepartmentId)
            {
                return Error.Field("cssdRequestId", "Choose an open request from the same department.");
            }
        }

        var packs = await db.Set<SterilePack>().Where(p => request.PackIds.Contains(p.Id)).ToListAsync(ct);
        if (packs.Count != request.PackIds.Count)
        {
            return Error.Field("packIds", "One or more scanned packs were not found.");
        }

        var today = time.Today();
        var now = time.GetUtcNow();
        var issues = new List<SetIssue>();
        foreach (var pack in packs)
        {
            var issued = pack.Issue(request.DepartmentId, today);
            if (issued.IsFailure)
            {
                return issued.Error!;
            }

            var issue = new SetIssue
            {
                FacilityId = facilityId,
                PackId = pack.Id,
                LoadId = pack.LoadId,
                InstrumentSetId = pack.InstrumentSetId,
                DepartmentId = request.DepartmentId,
                CssdRequestId = cssdRequest?.Id,
                IssuedAt = now,
                IssuedToName = Clean(request.IssuedToName),
                SterileUntil = pack.SterileUntil,
                Status = SetIssueStatus.Issued,
            };
            issues.Add(issue);
            db.Add(issue);
            cssdRequest?.RecordIssue(pack.InstrumentSetId);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("cssd.pack_taken", "One of these packs was issued from another counter a moment ago. Scan again.");
        }

        var ids = issues.Select(i => i.Id).ToList();
        return Result<IReadOnlyList<SetIssueDto>>.Ok(await IssueDtosAsync(db.Set<SetIssue>().AsNoTracking().Where(i => ids.Contains(i.Id)), ct));
    }

    /// <summary>Links a pack to the patient it was used on (implant and instrument traceability for recalls).</summary>
    public async Task<Result> RecordUseAsync(Guid issueId, RecordPackUseRequest request, CancellationToken ct)
    {
        var issue = await db.Set<SetIssue>().FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null)
        {
            return Error.NotFound("Pack issue", issueId);
        }

        if (!await db.Set<Patient>().AnyAsync(p => p.Id == request.PatientId, ct))
        {
            return Error.Field("patientId", "Choose the patient the pack was used on.");
        }

        var used = issue.RecordUse(request.PatientId, request.UsedAt ?? time.GetUtcNow(), time.Today());
        if (used.IsFailure)
        {
            return used;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> AcknowledgeRecallAsync(Guid issueId, string action, CancellationToken ct)
    {
        var issue = await db.Set<SetIssue>().FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null)
        {
            return Error.NotFound("Pack issue", issueId);
        }

        var acknowledged = issue.AcknowledgeRecall(currentUser.UserId, currentUser.DisplayName, action, time.GetUtcNow());
        if (acknowledged.IsFailure)
        {
            return acknowledged;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<CssdSummary> SummaryAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var start = IndiaClock.StartOfDayUtc(from);
        var end = IndiaClock.StartOfDayUtc(to.AddDays(1));
        var loads = await db.Set<SterilizationLoad>().AsNoTracking()
            .Where(l => l.LoadedAt >= start && l.LoadedAt < end)
            .Select(l => new { l.Method, l.Status, l.BiologicalIndicator, l.ReleasedAt })
            .ToListAsync(ct);
        var failed = loads.Count(l => l.Status == LoadStatus.Failed);
        var byMethod = loads.GroupBy(l => l.Method)
            .Select(g => new MethodLoadCount(g.Key, g.Count(), g.Count(l => l.Status == LoadStatus.Failed)))
            .OrderBy(m => m.Method)
            .ToList();

        var today = time.Today();
        var soon = today.AddDays(ExpiringSoonDays);
        var stock = await (
                from p in db.Set<SterilePack>().AsNoTracking()
                join s in db.Set<InstrumentSet>() on p.InstrumentSetId equals s.Id
                where p.Status == PackStatus.Available && (p.SterileUntil == null || p.SterileUntil >= today)
                group p by new { s.Id, s.Code, s.Name } into g
                select new SetStock(g.Key.Id, g.Key.Code, g.Key.Name, g.Count(), g.Count(p => p.SterileUntil != null && p.SterileUntil <= soon)))
            .ToListAsync(ct);

        return new CssdSummary(
            from,
            to,
            loads.Count,
            loads.Count(l => l.ReleasedAt != null),
            failed,
            loads.Count == 0 ? 0 : Math.Round(failed * 100m / loads.Count, 1, MidpointRounding.AwayFromZero),
            loads.Count(l => l.BiologicalIndicator == IndicatorResult.Pending && l.Status is LoadStatus.Completed or LoadStatus.Released),
            byMethod,
            await db.Set<SetIssue>().CountAsync(i => i.IssuedAt >= start && i.IssuedAt < end, ct),
            await db.Set<CssdRequest>().CountAsync(r => r.Status == CssdRequestStatus.Open || r.Status == CssdRequestStatus.PartiallyIssued, ct),
            await db.Set<SoiledReceipt>().CountAsync(r => r.HasDiscrepancy && r.ReceivedAt >= start && r.ReceivedAt < end, ct),
            await db.Set<SetIssue>().CountAsync(i => i.RecallFlaggedAt != null && i.RecallAcknowledgedAt == null, ct),
            stock.Sum(s => s.ExpiringSoon),
            stock.OrderBy(s => s.Name).ToList());
    }

    // ------------------------------------------------------------------ projections shared with the endpoints

    internal async Task<IReadOnlyList<CssdRequestDto>> RequestDtosAsync(IQueryable<CssdRequest> requests, CancellationToken ct)
    {
        var rows = await requests.Include(r => r.Lines).ToListAsync(ct);
        var setIds = rows.SelectMany(r => r.Lines).Select(l => l.InstrumentSetId).Distinct().ToList();
        var departmentIds = rows.Select(r => r.DepartmentId).Distinct().ToList();
        var setNames = await db.Set<InstrumentSet>().AsNoTracking().Where(s => setIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var departments = await db.Set<Department>().AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return rows.Select(r => new CssdRequestDto(r.Id, r.RequestNumber, r.DepartmentId, departments.GetValueOrDefault(r.DepartmentId), r.RequestedByName,
                r.CreatedAt, r.RequiredBy, r.Priority, r.Status, r.Notes, r.CancelReason,
                r.Lines.Select(l => new CssdRequestLineDto(l.InstrumentSetId, setNames.GetValueOrDefault(l.InstrumentSetId), l.Quantity, l.IssuedQuantity)).ToList()))
            .ToList();
    }

    internal async Task<IReadOnlyList<SoiledReceiptDto>> ReceiptDtosAsync(IQueryable<SoiledReceipt> receipts, CancellationToken ct)
    {
        var rows = await receipts.Include(r => r.Lines).ToListAsync(ct);
        var setIds = rows.SelectMany(r => r.Lines).Select(l => l.InstrumentSetId).Distinct().ToList();
        var departmentIds = rows.Select(r => r.DepartmentId).Distinct().ToList();
        var setNames = await db.Set<InstrumentSet>().AsNoTracking().Where(s => setIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var departments = await db.Set<Department>().AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return rows.Select(r => new SoiledReceiptDto(r.Id, r.DepartmentId, departments.GetValueOrDefault(r.DepartmentId), r.ReceivedAt, r.ReceivedByName,
                r.HandedOverByName, r.HasDiscrepancy, r.Notes,
                r.Lines.Select(l => new SoiledReceiptLineDto(l.InstrumentSetId, setNames.GetValueOrDefault(l.InstrumentSetId), l.SetIssueId, l.ExpectedCount,
                    l.ReceivedCount, l.MissingCount, l.ExtraCount, l.DamagedCount, l.Discrepancies)).ToList()))
            .ToList();
    }

    internal static Task<List<SetIssueDto>> IssueDtosAsync(HospitalDbContext db, IQueryable<SetIssue> issues, CancellationToken ct) =>
        (from i in issues
         join pk in db.Set<SterilePack>() on i.PackId equals pk.Id
         join l in db.Set<SterilizationLoad>() on i.LoadId equals l.Id
         join s in db.Set<InstrumentSet>() on i.InstrumentSetId equals s.Id
         join d in db.Set<Department>() on i.DepartmentId equals d.Id into departments
         from d in departments.DefaultIfEmpty()
         orderby i.IssuedAt descending
         select new SetIssueDto(i.Id, i.PackId, pk.PackNumber, i.LoadId, l.CycleNumber, i.InstrumentSetId, s.Name, i.DepartmentId, d != null ? d.Name : null,
             i.IssuedAt, i.IssuedToName, i.SterileUntil, i.Status, i.PatientId, i.UsedAt, i.RecallFlaggedAt, i.RecallReason, i.RecallAcknowledgedAt, i.RecallAction))
        .ToListAsync(ct);

    private async Task<IReadOnlyList<SetIssueDto>> IssueDtosAsync(IQueryable<SetIssue> issues, CancellationToken ct) => await IssueDtosAsync(db, issues, ct);

    private static string? Fit(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
