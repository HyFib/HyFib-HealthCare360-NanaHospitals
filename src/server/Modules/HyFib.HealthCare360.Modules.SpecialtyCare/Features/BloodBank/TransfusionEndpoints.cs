using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;

/// <summary>/api/v1/blood-bank/requests, /issues and /reactions — requests, grouping, cross-match, issue/return and reactions.</summary>
internal static class TransfusionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/blood-bank").WithTags("Blood Bank");

        group.MapGet("/requests", ListRequests).RequirePermission(SpecialtyCarePermissions.BloodBankRead);
        group.MapGet("/requests/{id:guid}", GetRequest).RequirePermission(SpecialtyCarePermissions.BloodBankRead);
        group.MapPost("/requests", async (CreateBloodRequestRequest r, TransfusionService s, CancellationToken ct) =>
                (await s.CreateRequestAsync(r, ct)).ToHttpResult(x => TypedResults.Created($"/api/v1/blood-bank/requests/{x.Id}", new { x.Id, x.RequestNumber })))
            .RequirePermission(SpecialtyCarePermissions.BloodBankRequests).WithValidation<CreateBloodRequestRequest>();
        group.MapPost("/requests/{id:guid}/cancel", async (Guid id, BloodReasonRequest r, TransfusionService s, CancellationToken ct) =>
                (await s.CancelRequestAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.BloodBankRequests).WithValidation<BloodReasonRequest>();
        group.MapPost("/requests/{id:guid}/grouping", async (Guid id, GroupPatientRequest r, TransfusionService s, CancellationToken ct) =>
                (await s.GroupPatientAsync(id, r.BloodGroup, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.BloodBankCrossmatch).WithValidation<GroupPatientRequest>();
        group.MapPost("/requests/{id:guid}/emergency-release", async (Guid id, EmergencyReleaseRequest r, TransfusionService s, CancellationToken ct) =>
                (await s.AuthoriseEmergencyReleaseAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.BloodBankIssue).WithValidation<EmergencyReleaseRequest>();
        group.MapGet("/requests/{id:guid}/compatible-units", CompatibleUnits).RequirePermission(SpecialtyCarePermissions.BloodBankCrossmatch);
        group.MapPost("/requests/{id:guid}/crossmatches", async (Guid id, RecordCrossMatchRequest r, TransfusionService s, TimeProvider t, CancellationToken ct) =>
                (await s.CrossMatchAsync(id, r, ct)).ToHttpResult(c => TypedResults.Ok(ToDto(c, t.GetUtcNow()))))
            .RequirePermission(SpecialtyCarePermissions.BloodBankCrossmatch).WithValidation<RecordCrossMatchRequest>();
        group.MapPost("/requests/{id:guid}/issues", async (Guid id, IssueBloodRequest r, TransfusionService s, CancellationToken ct) =>
                (await s.IssueAsync(id, r, ct)).ToHttpResult(i => TypedResults.Ok(ToDto(i))))
            .RequirePermission(SpecialtyCarePermissions.BloodBankIssue).WithValidation<IssueBloodRequest>();

        group.MapPost("/issues/{id:guid}/return", async (Guid id, ReturnBloodRequest r, TransfusionService s, CancellationToken ct) =>
                (await s.ReturnAsync(id, r, ct)).ToHttpResult(i => TypedResults.Ok(ToDto(i))))
            .RequirePermission(SpecialtyCarePermissions.BloodBankIssue).WithValidation<ReturnBloodRequest>();
        group.MapPost("/issues/{id:guid}/reactions", async (Guid id, ReportReactionRequest r, TransfusionService s, CancellationToken ct) =>
                (await s.ReportReactionAsync(id, r, ct)).ToHttpResult(x => TypedResults.Created($"/api/v1/blood-bank/reactions/{x.Id}", new { x.Id })))
            .RequirePermission(SpecialtyCarePermissions.BloodBankReactions).WithValidation<ReportReactionRequest>();

        group.MapGet("/reactions", ListReactions).RequirePermission(SpecialtyCarePermissions.BloodBankRead);
        group.MapPost("/reactions/{id:guid}/investigate", async (Guid id, TransfusionService s, CancellationToken ct) =>
                (await s.InvestigateReactionAsync(id, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.BloodBankReactionsInvestigate);
        group.MapPost("/reactions/{id:guid}/close", async (Guid id, CloseReactionRequest r, TransfusionService s, CancellationToken ct) =>
                (await s.CloseReactionAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.BloodBankReactionsInvestigate).WithValidation<CloseReactionRequest>();
    }

    private static async Task<IResult> ListRequests(
        [AsParameters] PageQuery query, BloodRequestStatus? status, bool? openOnly, Guid? patientId, HospitalDbContext db, IFieldProtector protector,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var requests = db.Set<BloodRequest>().AsNoTracking();
        if (status is not null)
        {
            requests = requests.Where(r => r.Status == status);
        }
        else if (openOnly != false)
        {
            requests = requests.Where(r => r.Status != BloodRequestStatus.Issued && r.Status != BloodRequestStatus.Cancelled);
        }

        if (patientId is not null)
        {
            requests = requests.Where(r => r.PatientId == patientId);
        }

        if (page.SearchTerm is { } term)
        {
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            var number = term.ToUpperInvariant();
            requests = requests.Where(r => r.RequestNumber == number || matching.Contains(r.PatientId));
        }

        var result = await (
                from r in requests
                join p in db.Set<Patient>() on r.PatientId equals p.Id
                // Enums are stored as text, so rank urgency explicitly (emergency first) instead of sorting alphabetically.
                orderby r.Urgency == BloodRequestUrgency.Emergency ? 0 : r.Urgency == BloodRequestUrgency.Urgent ? 1 : 2, r.CreatedAt
                select new BloodRequestListItem(
                    r.Id, r.RequestNumber, r.PatientId, p.Uhid, p.FullName, r.ComponentType, r.UnitsRequested, r.UnitsReserved, r.UnitsIssued, r.Urgency,
                    r.Status, r.PatientBloodGroup, r.CreatedAt, r.RequiredBy))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetRequest(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var row = await (
                from x in db.Set<BloodRequest>().AsNoTracking()
                join p in db.Set<Patient>() on x.PatientId equals p.Id
                where x.Id == id
                select new { r = x, p.Uhid, p.FullName })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Error.NotFound(nameof(BloodRequest), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenChartAsync(row.r.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(row.r.PatientId, $"Viewed blood request {row.r.RequestNumber}", ct);
        var now = time.GetUtcNow();
        var crossMatches = await db.Set<CrossMatch>().AsNoTracking().Where(c => c.RequestId == id).OrderByDescending(c => c.PerformedAt).ToListAsync(ct);
        var issues = await db.Set<BloodIssue>().AsNoTracking().Where(i => i.RequestId == id).OrderBy(i => i.IssuedAt).ToListAsync(ct);
        var r = row.r;
        return TypedResults.Ok(new BloodRequestDetail(
            r.Id, r.RequestNumber, r.PatientId, row.Uhid, row.FullName, r.AdmissionId, r.VisitId, r.OrderItemId, r.ComponentType, r.UnitsRequested,
            r.UnitsReserved, r.UnitsIssued, r.Urgency, r.Indication, r.RequiredBy, r.RequestedByName, r.HaemoglobinGdl, r.PlateletCount,
            r.PatientBloodGroup, r.GroupedAt, r.GroupedByName, r.EmergencyReleaseAuthorisedBy, r.EmergencyReleaseReason, r.Status, r.CancelReason,
            crossMatches.Select(c => ToDto(c, now)).ToList(), issues.Select(ToDto).ToList(), r.Version));
    }

    /// <summary>Units the blood bank can offer: ABO/Rh compatible with the grouped patient, in date, first-expiry-first-out.</summary>
    private static async Task<IResult> CompatibleUnits(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var request = await db.Set<BloodRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(BloodRequest), id).ToProblem();
        }

        if (!request.IsGrouped)
        {
            return Error.Rule("bloodbank.patient_not_grouped", "Group the patient's sample first.").ToProblem();
        }

        var now = time.GetUtcNow();
        var candidates = await db.Set<BloodComponent>().AsNoTracking()
            .Where(c => c.ComponentType == request.ComponentType && c.ExpiresAt > now
                        && (c.Status == BloodUnitStatus.Available || (c.Status == BloodUnitStatus.Reserved && c.ReservedForRequestId == id)))
            .OrderBy(c => c.ExpiresAt)
            .Take(500)
            .ToListAsync(ct);

        return TypedResults.Ok(candidates
            .Where(c => BloodCompatibility.IsCompatible(c.ComponentType, c.BloodGroup, request.PatientBloodGroup))
            .OrderByDescending(c => c.ReservedForRequestId == id)
            .ThenByDescending(c => c.BloodGroup == request.PatientBloodGroup)
            .ThenBy(c => c.ExpiresAt)
            .Take(50)
            .Select(c => new CompatibleUnitDto(c.Id, c.UnitNumber, c.BloodGroup, c.VolumeMl, c.ExpiresAt, c.ReservedForRequestId == id))
            .ToList());
    }

    private static async Task<IResult> ListReactions([AsParameters] PageQuery query, ReactionStatus? status, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var reactions = db.Set<TransfusionReaction>().AsNoTracking();
        if (status is not null)
        {
            reactions = reactions.Where(r => r.Status == status);
        }

        var result = await (
                from r in reactions
                join p in db.Set<Patient>() on r.PatientId equals p.Id
                orderby r.ReportedAt descending
                select new { r, p.Uhid, p.FullName })
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(x => new TransfusionReactionDto(
            x.r.Id, x.r.IssueId, x.r.RequestId, x.r.PatientId, x.Uhid, x.FullName, x.r.UnitNumber, x.r.ReportedAt, x.r.ReportedByName, x.r.OnsetAt,
            x.r.Symptoms, x.r.Vitals, x.r.TransfusionStopped, x.r.VolumeTransfusedMl, x.r.ActionsTaken, x.r.Severity, x.r.Type, x.r.Status,
            x.r.InvestigationFindings, x.r.ReportedToHaemovigilance, x.r.ClosedAt, x.r.ClosedByName)));
    }

    private static CrossMatchDto ToDto(CrossMatch c, DateTimeOffset now) => new(
        c.Id, c.ComponentId, c.UnitNumber, c.Method, c.Result, c.PerformedAt, c.PerformedByName, c.Remarks, c.IsValidCompatibleAt(now), c.ChargeStatus);

    private static BloodIssueDto ToDto(BloodIssue i) => new(
        i.Id, i.RequestId, i.ComponentId, i.UnitNumber, i.ComponentType, i.UnitBloodGroup, i.IsEmergencyRelease, i.IssuedAt, i.IssuedByName, i.IssuedToName,
        i.ReturnedAt, i.Restocked, i.ReturnNotes, i.ChargeStatus, i.ChargeError);
}
