using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Requests;

/// <summary>
/// /api/v1/privacy: the grievance officer's queue of data-principal requests (DPDP Act s11 to s13), requests recorded at
/// the desk, and nominees. Opening a request, its data summary or its erasure assessment is patient-record access: it
/// is audited, and a restricted record needs the usual permission or break-the-glass.
/// </summary>
internal static class RequestEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/privacy").WithTags("Privacy");

        group.MapGet("/overview", async (DataRequestService requests, CancellationToken ct) => TypedResults.Ok(await requests.OverviewAsync(ct)))
            .RequirePermission(PrivacyPermissions.RequestsRead);
        group.MapGet("/requests", List).RequirePermission(PrivacyPermissions.RequestsRead);
        group.MapGet("/officers", async (DataRequestService requests, CancellationToken ct) => TypedResults.Ok(await requests.OfficersAsync(ct)))
            .RequirePermission(PrivacyPermissions.RequestsManage);
        group.MapGet("/requests/{id:guid}", Get).RequirePermission(PrivacyPermissions.RequestsRead);
        group.MapPost("/requests", Create).RequirePermission(PrivacyPermissions.RequestsLog).WithValidation<CreateDataRequestRequest>();
        group.MapPost("/requests/{id:guid}/start", async (Guid id, DataRequestService requests, CancellationToken ct) =>
                (await requests.StartAsync(id, ct)).ToNoContent())
            .RequirePermission(PrivacyPermissions.RequestsManage);
        group.MapPost("/requests/{id:guid}/verify", async (Guid id, VerifyIdentityRequest r, DataRequestService requests, CancellationToken ct) =>
                (await requests.VerifyIdentityAsync(id, r, ct)).ToNoContent())
            .RequirePermission(PrivacyPermissions.RequestsManage)
            .WithValidation<VerifyIdentityRequest>();
        group.MapPost("/requests/{id:guid}/assign", async (Guid id, AssignDataRequestRequest r, DataRequestService requests, CancellationToken ct) =>
                (await requests.AssignAsync(id, r.UserId, ct)).ToNoContent())
            .RequirePermission(PrivacyPermissions.RequestsManage);
        group.MapPost("/requests/{id:guid}/notes", async (Guid id, DataRequestNoteRequest r, DataRequestService requests, CancellationToken ct) =>
                (await requests.AddNoteAsync(id, r.Text, ct)).ToNoContent())
            .RequirePermission(PrivacyPermissions.RequestsManage)
            .WithValidation<DataRequestNoteRequest>();
        group.MapPost("/requests/{id:guid}/corrections", async (Guid id, DecideCorrectionsRequest r, DataRequestService requests, CancellationToken ct) =>
                (await requests.DecideCorrectionsAsync(id, r, ct)).ToNoContent())
            .RequirePermission(PrivacyPermissions.RequestsManage)
            .WithValidation<DecideCorrectionsRequest>();
        group.MapGet("/requests/{id:guid}/summary", Summary).RequirePermission(PrivacyPermissions.RequestsRead);
        group.MapGet("/requests/{id:guid}/erasure", Assess).RequirePermission(PrivacyPermissions.RequestsRead);
        group.MapPost("/requests/{id:guid}/erasure", Erase).RequirePermission(PrivacyPermissions.RequestsManage).WithValidation<EraseDataRequest>();
        group.MapPost("/requests/{id:guid}/close", async (Guid id, CloseDataRequestRequest r, DataRequestService requests, CancellationToken ct) =>
                (await requests.CloseAsync(id, r, ct)).ToNoContent())
            .RequirePermission(PrivacyPermissions.RequestsManage)
            .WithValidation<CloseDataRequestRequest>();

        group.MapGet("/patients/{patientId:guid}/nominee", GetNominee)
            .RequireAnyPermission(PrivacyPermissions.RequestsLog, PrivacyPermissions.RequestsRead);
        group.MapPut("/patients/{patientId:guid}/nominee", SaveNominee).RequirePermission(PrivacyPermissions.RequestsLog).WithValidation<SaveNomineeRequest>();
        group.MapDelete("/patients/{patientId:guid}/nominee", RemoveNominee).RequirePermission(PrivacyPermissions.RequestsLog);
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DataRequestStatus? status,
        DataRequestType? type,
        Guid? patientId,
        Guid? assignedToUserId,
        bool? mine,
        bool? openOnly,
        bool? overdueOnly,
        DataRequestService requests,
        CancellationToken ct) =>
        TypedResults.Ok(await requests.ListAsync(
            new DataRequestFilter(status, type, patientId, assignedToUserId, mine == true, openOnly == true, overdueOnly == true), query.ToRequest(), ct));

    private static async Task<IResult> Get(Guid id, DataRequestService requests, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var detail = await requests.DetailAsync(id, ct);
        if (detail is null)
        {
            return Error.NotFound(nameof(DataRequest), id).ToProblem();
        }

        if (detail.PatientId is { } patientId)
        {
            var access = await guard.EnsureCanOpenAsync(patientId, ct);
            if (access.IsFailure)
            {
                return access.Error!.ToProblem();
            }

            await audit.PatientRecordAccessedAsync(patientId, $"Viewed privacy request {detail.RequestNumber}", ct);
        }

        return TypedResults.Ok(detail);
    }

    private static async Task<IResult> Create(CreateDataRequestRequest r, DataRequestService requests, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var created = await requests.CreateAsync(new NewDataRequest(facilityId, r.Type, r.Channel, r.PatientId, r.PrincipalName, r.ContactPhone,
            r.ContactEmail, r.RequestedBy, r.RequesterName, r.Details, r.Corrections, r.IdentityCheck, r.IdentityNote), ct);
        return created.IsFailure
            ? created.Error!.ToProblem()
            : TypedResults.Created($"/api/v1/privacy/requests/{created.Value.Id}", new { created.Value.Id, created.Value.RequestNumber, created.Value.DueAt });
    }

    /// <summary>
    /// The data summary for the request's principal: the one given, for a completed access request, or else a fresh one
    /// to check before answering.
    /// </summary>
    private static async Task<IResult> Summary(
        Guid id, HospitalDbContext db, PersonalDataInventory inventory, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var request = await db.Set<DataRequest>().AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.PatientId, r.RequestNumber, r.SummarySnapshot })
            .FirstOrDefaultAsync(ct);
        if (request is null)
        {
            return Error.NotFound(nameof(DataRequest), id).ToProblem();
        }

        if (request.PatientId is not { } patientId)
        {
            return Error.Rule("privacy.no_patient", "This request is not linked to a patient record.").ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, $"Viewed the personal data summary for privacy request {request.RequestNumber}", ct);
        if (request.SummarySnapshot is { } snapshot)
        {
            return TypedResults.Text(snapshot, "application/json");
        }

        return await inventory.SummariseAsync(patientId, ct) is { } summary
            ? TypedResults.Ok(summary)
            : Error.NotFound("Patient", patientId).ToProblem();
    }

    private static async Task<IResult> Assess(
        Guid id, HospitalDbContext db, ErasureService erasure, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var request = await db.Set<DataRequest>().AsNoTracking().Where(r => r.Id == id).Select(r => new { r.PatientId, r.RequestNumber }).FirstOrDefaultAsync(ct);
        if (request is null)
        {
            return Error.NotFound(nameof(DataRequest), id).ToProblem();
        }

        if (request.PatientId is not { } patientId)
        {
            return Error.Rule("privacy.no_patient", "This request is not linked to a patient record.").ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, $"Viewed what can be erased for privacy request {request.RequestNumber}", ct);
        return TypedResults.Ok(await erasure.AssessAsync(patientId, ct));
    }

    private static async Task<IResult> Erase(
        Guid id, EraseDataRequest r, HospitalDbContext db, ErasureService erasure, PatientAccessGuard guard, CancellationToken ct)
    {
        var request = await db.Set<DataRequest>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(DataRequest), id).ToProblem();
        }

        if (request.PatientId is { } patientId && (await guard.EnsureCanOpenAsync(patientId, ct)) is { IsFailure: true } denied)
        {
            return denied.Error!.ToProblem();
        }

        return (await erasure.EraseAsync(request, r.Items, ct)).ToOk();
    }

    private static async Task<IResult> GetNominee(Guid patientId, NomineeService nominees, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, "Viewed the nominee", ct);
        return TypedResults.Ok(new { nominee = await nominees.GetAsync(patientId, ct) });
    }

    private static async Task<IResult> SaveNominee(Guid patientId, SaveNomineeRequest r, NomineeService nominees, PatientAccessGuard guard, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        return access.IsFailure ? access.Error!.ToProblem() : (await nominees.SaveAsync(patientId, r, DataRequestChannel.FrontDesk, ct)).ToNoContent();
    }

    private static async Task<IResult> RemoveNominee(Guid patientId, NomineeService nominees, PatientAccessGuard guard, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        return access.IsFailure ? access.Error!.ToProblem() : (await nominees.RemoveAsync(patientId, ct)).ToNoContent();
    }
}
