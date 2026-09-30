using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Portal;

/// <summary>
/// /api/v1/portal/privacy: the patient's own privacy centre (DPDP Act s11 to s14). The data summary, requests, the
/// nominee and breach notices, for the patients the signed-in mobile number may act for (<see cref="PortalPatientScope"/>).
/// Anyone else's id gets a 403, and every read of patient data is audited.
/// </summary>
internal static class PortalPrivacyEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/portal/privacy").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal);

        group.MapGet("/status", Status);
        group.MapGet("/summary", Summary);
        group.MapGet("/requests", List);
        group.MapGet("/requests/{id:guid}", Get);
        group.MapPost("/requests", Create).WithValidation<PortalDataRequestCreate>();
        group.MapPost("/requests/{id:guid}/withdraw", Withdraw);
        group.MapGet("/nominee", GetNominee);
        group.MapPut("/nominee", SaveNominee).WithValidation<PortalNomineeRequest>();
        group.MapDelete("/nominee", RemoveNominee);
        group.MapGet("/notices", Notices);
        group.MapGet("/notices/{id:guid}", Notice);
    }

    private static async Task<IResult> Status(Guid patientId, PortalPatientScope scope, HospitalDbContext db, BreachService breaches, CancellationToken ct)
    {
        var access = await scope.EnsureAccessAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var nominee = await db.Set<Nominee>().AsNoTracking().Where(n => n.PatientId == patientId).Select(n => n.Name).FirstOrDefaultAsync(ct);
        var open = await db.Set<DataRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .CountAsync(r => r.PatientId == patientId && (r.Status == DataRequestStatus.Submitted || r.Status == DataRequestStatus.InProgress), ct);
        var notices = (await breaches.NoticesForAsync([patientId], null, ct)).Count;
        return TypedResults.Ok(new PortalPrivacyStatus(nominee is not null, nominee, open, DataRequestService.MaxOpenPortalRequests, notices));
    }

    private static async Task<IResult> Summary(Guid patientId, PortalPatientScope scope, PersonalDataInventory inventory, IAuditLogger audit, CancellationToken ct)
    {
        var access = await scope.EnsureAccessAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, "Portal: viewed the summary of personal data", ct);
        return await inventory.SummariseAsync(patientId, ct) is { } summary ? TypedResults.Ok(summary) : Error.NotFound("Patient", patientId).ToProblem();
    }

    private static async Task<IResult> List(Guid? patientId, PortalPatientScope scope, HospitalDbContext db, CancellationToken ct)
    {
        var accessible = await scope.AccessiblePatientIdsAsync(ct);
        if (patientId is { } requested && !accessible.Contains(requested))
        {
            return Error.Forbidden("portal.patient_denied", "This record is not linked to your mobile number.").ToProblem();
        }

        var ids = patientId is { } one ? [one] : accessible;
        var rows = await db.Set<DataRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.PatientId != null && ids.Contains(r.PatientId.Value))
            .OrderByDescending(r => r.ReceivedAt)
            .Take(100)
            .Select(r => new PortalDataRequestItem(r.Id, r.RequestNumber, r.PatientId!.Value, r.Type, r.Status, r.ReceivedAt, r.DueAt, r.ClosedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> Get(Guid id, PortalPatientScope scope, HospitalDbContext db, PrivacySettings privacy, IAuditLogger audit, CancellationToken ct)
    {
        var request = await db.Set<DataRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(r => r.Events)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request?.PatientId is not { } patientId)
        {
            return Error.NotFound(nameof(DataRequest), id).ToProblem();
        }

        var access = await scope.EnsureAccessAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, $"Portal: viewed privacy request {request.RequestNumber}", ct);
        return TypedResults.Ok(new PortalDataRequestDetail(
            request.Id, request.RequestNumber, patientId, request.Type, request.Status, request.RequestedBy, request.RequesterName,
            request.IsIdentityVerified, request.Details,
            [.. request.Corrections.Select(c => new PortalCorrectionItem(c.Field, c.CurrentValue, c.RequestedValue, c.Decision, c.Note))],
            request.ReceivedAt, request.DueAt, request.ClosedAt, request.Response, request.ErasedItems, request.RetainedUntil, request.RetainedPermanently,
            [.. request.Events.Where(e => e.IsPublic).OrderBy(e => e.At).Select(e => new PortalRequestEvent(e.At, e.Kind, e.Text))],
            request.IsOpen, await privacy.PublishedContactAsync(ct)));
    }

    private static async Task<IResult> Create(PortalDataRequestCreate r, PortalPatientScope scope, HospitalDbContext db, DataRequestService requests, CancellationToken ct)
    {
        var access = await scope.EnsureAccessAsync(r.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var facilityId = await db.Set<Patient>().Where(p => p.Id == r.PatientId).Select(p => p.RegisteredFacilityId).FirstAsync(ct);

        // Signing in with a code sent to the patient's registered number confirms a request the patient makes for
        // themselves; one made for someone else waits for staff to confirm the requester's authority.
        var created = await requests.CreateAsync(new NewDataRequest(facilityId, r.Type, DataRequestChannel.Portal, r.PatientId, null, null, null,
            r.RequestedBy, r.RequesterName, r.Details, r.Corrections,
            r.RequestedBy == RequesterRelation.Self ? IdentityCheck.PortalSignIn : null, null), ct);
        return created.IsFailure
            ? created.Error!.ToProblem()
            : TypedResults.Created($"/api/v1/portal/privacy/requests/{created.Value.Id}",
                new PortalRequestCreated(created.Value.Id, created.Value.RequestNumber, created.Value.DueAt));
    }

    private static async Task<IResult> Withdraw(Guid id, PortalPatientScope scope, HospitalDbContext db, DataRequestService requests, CancellationToken ct)
    {
        var patientId = await db.Set<DataRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.Id == id).Select(r => r.PatientId).FirstOrDefaultAsync(ct);
        if (patientId is null)
        {
            return Error.NotFound(nameof(DataRequest), id).ToProblem();
        }

        var access = await scope.EnsureAccessAsync(patientId.Value, ct);
        return access.IsFailure ? access.Error!.ToProblem() : (await requests.WithdrawAsync(id, ct)).ToNoContent();
    }

    private static async Task<IResult> GetNominee(Guid patientId, PortalPatientScope scope, NomineeService nominees, IAuditLogger audit, CancellationToken ct)
    {
        var access = await scope.EnsureAccessAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, "Portal: viewed the nominee", ct);
        return TypedResults.Ok(new { nominee = await nominees.GetAsync(patientId, ct) });
    }

    private static async Task<IResult> SaveNominee(PortalNomineeRequest r, PortalPatientScope scope, NomineeService nominees, CancellationToken ct)
    {
        var access = await scope.EnsureAccessAsync(r.PatientId, ct);
        return access.IsFailure
            ? access.Error!.ToProblem()
            : (await nominees.SaveAsync(r.PatientId, new SaveNomineeRequest(r.Name, r.Relationship, r.Phone, r.Email), DataRequestChannel.Portal, ct)).ToNoContent();
    }

    private static async Task<IResult> RemoveNominee(Guid patientId, PortalPatientScope scope, NomineeService nominees, CancellationToken ct)
    {
        var access = await scope.EnsureAccessAsync(patientId, ct);
        return access.IsFailure ? access.Error!.ToProblem() : (await nominees.RemoveAsync(patientId, ct)).ToNoContent();
    }

    private static async Task<IResult> Notices(Guid? patientId, PortalPatientScope scope, BreachService breaches, CancellationToken ct)
    {
        var accessible = await scope.AccessiblePatientIdsAsync(ct);
        if (patientId is { } requested && !accessible.Contains(requested))
        {
            return Error.Forbidden("portal.patient_denied", "This record is not linked to your mobile number.").ToProblem();
        }

        return TypedResults.Ok(await breaches.NoticesForAsync(patientId is { } one ? [one] : [.. accessible], null, ct));
    }

    private static async Task<IResult> Notice(Guid id, PortalPatientScope scope, BreachService breaches, CancellationToken ct)
    {
        var notice = (await breaches.NoticesForAsync([.. await scope.AccessiblePatientIdsAsync(ct)], id, ct)).FirstOrDefault();
        return notice is null ? Error.NotFound("Notice", id).ToProblem() : TypedResults.Ok(notice);
    }
}
