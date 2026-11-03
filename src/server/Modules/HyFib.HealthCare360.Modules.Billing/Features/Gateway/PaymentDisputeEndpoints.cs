using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Features.Gateway;

/// <summary>
/// /api/v1/billing/disputes — chargebacks on card, UPI and gateway payments: the cases HC360 opened from the settlements
/// and those opened from the bank's notice, what the notice says, the evidence sent, a win, and accepting the loss (a
/// receipt reversal a second person approves). Everything is limited to the signed-in facility and needs
/// <see cref="BillingPermissions.GatewayReconcile"/>.
/// </summary>
internal static class PaymentDisputeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/disputes").WithTags("Billing - Chargebacks")
            .RequirePermission(BillingPermissions.GatewayReconcile);
        group.MapGet("/", List);
        group.MapGet("/summary", Summary);
        group.MapGet("/payment-lines", PaymentLines);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Open).WithValidation<OpenPaymentDisputeRequest>();
        group.MapPost("/{id:guid}/details", (Guid id, DescribePaymentDisputeRequest r, PaymentDisputeService disputes, CancellationToken ct) =>
                Ok(disputes.DescribeAsync(id, r, ct)))
            .WithValidation<DescribePaymentDisputeRequest>();
        group.MapPost("/{id:guid}/evidence", (Guid id, PaymentDisputeEvidenceRequest r, PaymentDisputeService disputes, CancellationToken ct) =>
                Ok(disputes.RecordEvidenceAsync(id, r, ct)))
            .WithValidation<PaymentDisputeEvidenceRequest>();
        group.MapPost("/{id:guid}/won", (Guid id, PaymentDisputeNoteRequest r, PaymentDisputeService disputes, CancellationToken ct) =>
                Ok(disputes.WinAsync(id, r, ct)))
            .WithValidation<PaymentDisputeNoteRequest>();
        group.MapPost("/{id:guid}/accept-loss", AcceptLoss).WithValidation<PaymentDisputeNoteRequest>();
    }

    /// <summary><c>open</c>: true for cases not yet won or lost, false for closed ones; <c>terminal</c>: true for card and UPI terminal payments, false for the gateway's.</summary>
    private static async Task<IResult> List(bool? open, bool? terminal, int? page, int? pageSize, ICurrentUser user, PaymentDisputeService disputes, CancellationToken ct) =>
        user.FacilityId is { } facilityId
            ? TypedResults.Ok(await disputes.ListAsync(facilityId, open, terminal, page ?? 1, pageSize ?? 25, ct))
            : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> Summary(ICurrentUser user, PaymentDisputeService disputes, CancellationToken ct) =>
        user.FacilityId is { } facilityId ? TypedResults.Ok(await disputes.SummaryAsync(facilityId, ct)) : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> Get(Guid id, PaymentDisputeService disputes, CancellationToken ct) => (await disputes.GetAsync(id, ct)).ToOk();

    /// <summary>The receipt's card, UPI and online payment lines (<c>?receiptNumber=</c>), to choose the one a notice is about.</summary>
    private static async Task<IResult> PaymentLines(string receiptNumber, ICurrentUser user, PaymentDisputeService disputes, CancellationToken ct) =>
        user.FacilityId is { } facilityId
            ? (await disputes.PaymentLinesAsync(facilityId, receiptNumber, ct)).ToOk()
            : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> Open(OpenPaymentDisputeRequest request, ICurrentUser user, PaymentDisputeService disputes, CancellationToken ct) =>
        user.FacilityId is { } facilityId
            ? (await disputes.OpenFromNoticeAsync(facilityId, request, ct)).ToCreated(d => $"/api/v1/billing/disputes/{d.Id}")
            : BillingDb.FacilityRequired.ToProblem();

    /// <summary>202 with the receipt reversal asked for: the case is lost when a second person who approves reversals approves it.</summary>
    private static async Task<IResult> AcceptLoss(Guid id, PaymentDisputeNoteRequest request, PaymentDisputeService disputes, CancellationToken ct) =>
        (await disputes.AcceptLossAsync(id, request, ct)).ToHttpResult(approval => TypedResults.Accepted($"/api/v1/approvals/{approval.Id}", approval));

    private static async Task<IResult> Ok(Task<Result<PaymentDisputeDto>> change) => (await change).ToOk();
}
