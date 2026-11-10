using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Features.VisitPayer;

/// <summary>
/// /api/v1/billing/visits/{visitId}/payer — who pays for an out-patient visit, changed after registration while its bill
/// is not final (BIL-OP). Billing owns it, since the change prices the visit's unbilled services again; the visit itself
/// is registered in Patients (<c>/api/v1/visits</c>).
/// </summary>
internal static class VisitPayerEndpoints
{
    private static readonly string[] Readers = [BillingPermissions.ChargesRead, BillingPermissions.OpCreate];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/visits/{visitId:guid}/payer").WithTags("Billing - Visit payer");
        group.MapGet("/", Get).RequireAnyPermission(Readers);
        group.MapPost("/", Change).RequirePermission(BillingPermissions.OpCreate).WithValidation<ChangeVisitPayerRequest>();
    }

    private static async Task<IResult> Get(Guid visitId, VisitPayerChanges visits, PatientAccessGuard guard, HospitalDbContext db, CancellationToken ct)
    {
        var view = await visits.ViewAsync(visitId, ct);
        if (view is null)
        {
            return Error.NotFound("Visit", visitId).ToProblem();
        }

        var patientId = await db.Set<HyFib.HealthCare360.Modules.Patients.Domain.Visit>().Where(v => v.Id == visitId).Select(v => v.PatientId).FirstAsync(ct);
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        return access.IsFailure ? access.Error!.ToProblem() : TypedResults.Ok(view);
    }

    /// <summary>
    /// 200 with what changed when the payer stays of the same kind and no service gets cheaper; 202 with the approval it
    /// waits for otherwise (MAKER-CHECKER.md); 422 when the visit's bill is final or drafted, or the payer does not stand
    /// behind the patient; 409 when the visit changed, or a change already waits.
    /// </summary>
    private static async Task<IResult> Change(Guid visitId, ChangeVisitPayerRequest request, VisitPayerChanges visits, CancellationToken ct)
    {
        var outcome = await visits.ChangeAsync(visitId, request, ct);
        if (outcome.IsFailure)
        {
            return outcome.Error!.ToProblem();
        }

        return outcome.Value.Approval is { } approval
            ? TypedResults.Accepted($"/api/v1/approvals/{approval.Id}", approval)
            : TypedResults.Ok(new { changed = outcome.Value.Changed });
    }
}
