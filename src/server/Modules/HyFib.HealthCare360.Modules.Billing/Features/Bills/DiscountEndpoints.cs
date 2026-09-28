using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Bills;

/// <summary>/api/v1/billing/discount-approvals and /discount-limits — discounts above a user's limit (MB-05, OPB-04).</summary>
internal static class DiscountEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var approvals = app.MapGroup("/api/v1/billing/discount-approvals").WithTags("Billing - Discounts");
        approvals.MapGet("/", List).RequirePermission(BillingPermissions.DiscountsApprove);
        approvals.MapPost("/{id:guid}/approve", Approve).RequirePermission(BillingPermissions.DiscountsApprove).WithValidation<DecideDiscountRequest>();
        approvals.MapPost("/{id:guid}/reject", Reject).RequirePermission(BillingPermissions.DiscountsApprove).WithValidation<DecideDiscountRequest>();

        var limits = app.MapGroup("/api/v1/billing/discount-limits").WithTags("Billing - Discounts");
        limits.MapGet("/", ListLimits).RequirePermission(BillingPermissions.DiscountsApprove);
        limits.MapPut("/", SaveLimit).RequirePermission(BillingPermissions.DiscountsApprove).WithValidation<SaveDiscountLimitRequest>();
        limits.MapDelete("/{id:guid}", DeleteLimit).RequirePermission(BillingPermissions.DiscountsApprove);
        limits.MapGet("/me", MyAuthority).RequireAnyPermission(BillingPermissions.OpCreate, BillingPermissions.IpManage);
    }

    private static async Task<IResult> List([AsParameters] PageQuery query, DiscountApprovalStatus? status, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var wanted = status ?? DiscountApprovalStatus.Pending;
        var result = await (
                from a in db.Set<DiscountApproval>().AsNoTracking().Where(a => a.Status == wanted).OrderByDescending(a => a.RequestedAt)
                join b in db.Set<Bill>() on a.BillId equals b.Id
                select new { Approval = a, b.BillNumber })
            .ToPagedResultAsync(page, ct);
        var patients = await PatientQueries.SummariesAsync(db, result.Items.Select(r => r.Approval.PatientId), BillingTime.Today(time), ct);
        return TypedResults.Ok(result.Map(r =>
        {
            var patient = patients.GetValueOrDefault(r.Approval.PatientId);
            return BillQueries.ToDto(r.Approval, r.BillNumber, patient?.Uhid, patient?.FullName);
        }));
    }

    private static async Task<IResult> Approve(Guid id, DecideDiscountRequest request, DiscountService discounts, CancellationToken ct) =>
        (await discounts.ApproveAsync(id, request.Remarks, ct)).ToNoContent();

    private static async Task<IResult> Reject(Guid id, DecideDiscountRequest request, DiscountService discounts, CancellationToken ct) =>
        (await discounts.RejectAsync(id, request.Remarks, ct)).ToNoContent();

    private static async Task<IResult> ListLimits(DiscountService discounts, CancellationToken ct) => TypedResults.Ok(await discounts.ListLimitsAsync(ct));

    private static async Task<IResult> SaveLimit(SaveDiscountLimitRequest request, DiscountService discounts, CancellationToken ct) =>
        (await discounts.SaveLimitAsync(request, ct)).ToNoContent();

    private static async Task<IResult> DeleteLimit(Guid id, DiscountService discounts, CancellationToken ct) =>
        (await discounts.DeleteLimitAsync(id, ct)).ToNoContent();

    /// <summary>The signed-in user's own discount authority (lets the bill screen warn before a request needs approval).</summary>
    private static async Task<IResult> MyAuthority(DiscountService discounts, CancellationToken ct) => TypedResults.Ok(await discounts.AuthorityAsync(ct));
}
