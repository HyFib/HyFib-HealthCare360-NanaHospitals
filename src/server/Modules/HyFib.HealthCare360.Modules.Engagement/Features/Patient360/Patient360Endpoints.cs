using HyFib.HealthCare360.Modules.Engagement.Features.Campaigns;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Patient360;

/// <summary>
/// /api/v1/crm/patients/{patientId}: the CRM 360° view (CRM-04), its communications history and a staff-recorded
/// marketing opt-out. The view and the history apply the restricted-record (VIP) rules and are audited as
/// patient-record access. Any staff member who works with patients can record an opt-out, because a withdrawal request
/// must never wait for the CRM team.
/// </summary>
internal static class Patient360Endpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/crm/patients/{patientId:guid}").WithTags("CRM");

        group.MapGet("/360", Get).RequirePermission(EngagementPermissions.Patient360Read);
        group.MapGet("/communications", Communications).RequirePermission(EngagementPermissions.Patient360Read);
        group.MapPost("/marketing-opt-out", async (Guid patientId, OptOutRequest r, CommunicationConsentService consents, CancellationToken ct) =>
                (await consents.OptOutAsync(patientId, $"Requested through staff: {r.Reason.Trim()}", ct)).ToNoContent())
            .RequireAnyPermission(
                EngagementPermissions.CampaignsManage,
                EngagementPermissions.FollowUpsManage,
                EngagementPermissions.LeadsManage,
                EngagementPermissions.Patient360Read,
                PatientsPermissions.Consents)
            .WithValidation<OptOutRequest>();
    }

    private static async Task<IResult> Get(Guid patientId, Patient360Service view, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var result = await view.GetAsync(patientId, ct);
        if (result.IsSuccess)
        {
            await audit.PatientRecordAccessedAsync(patientId, "Viewed the CRM 360° profile", ct);
        }

        return result.ToOk();
    }

    /// <summary>The communications history (CRM-04), under the same restricted-record rules and audit as the view.</summary>
    private static async Task<IResult> Communications(
        Guid patientId, [AsParameters] PageQuery query, CommunicationKind? kind, Patient360Service view, PatientAccessGuard guard, IAuditLogger audit,
        CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var page = await view.CommunicationsAsync(patientId, kind, query.ToRequest(), ct);
        await audit.PatientRecordAccessedAsync(patientId, "Viewed the CRM communications history", ct);
        return TypedResults.Ok(page);
    }
}
