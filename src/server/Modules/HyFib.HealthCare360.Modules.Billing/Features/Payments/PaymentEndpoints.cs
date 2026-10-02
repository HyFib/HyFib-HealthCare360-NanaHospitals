using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Payments;

/// <summary>
/// Money in and out (MB-04/05, OPB-05/06, IPB-01/08, OA-03): /api/v1/billing/receipts (split payments), IP advances,
/// patient advances (online appointment prepayments), patient dues and /api/v1/billing/refunds (request → approve → pay).
/// </summary>
internal static class PaymentEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var receipts = app.MapGroup("/api/v1/billing/receipts").WithTags("Billing - Payments");
        receipts.MapGet("/", ListReceipts).RequireAnyPermission(BillingPermissions.ChargesRead, BillingPermissions.PaymentsCollect);
        receipts.MapGet("/{id:guid}", GetReceipt).RequireAnyPermission(BillingPermissions.ChargesRead, BillingPermissions.PaymentsCollect);
        receipts.MapPost("/", Collect).RequirePermission(BillingPermissions.PaymentsCollect).WithValidation<CollectPaymentRequest>();

        app.MapPost("/api/v1/billing/ip-accounts/{id:guid}/deposits", CollectDeposit)
            .WithTags("Billing - Payments")
            .RequirePermission(BillingPermissions.Deposits)
            .WithValidation<CollectDepositRequest>();
        app.MapGet("/api/v1/billing/patients/{patientId:guid}/dues", Dues)
            .WithTags("Billing - Payments")
            .RequireAnyPermission(BillingPermissions.PaymentsCollect, BillingPermissions.ChargesRead);
        app.MapGet("/api/v1/billing/patients/{patientId:guid}/advances", ListAdvances)
            .WithTags("Billing - Payments")
            .RequireAnyPermission(BillingPermissions.PaymentsCollect, BillingPermissions.ChargesRead);
        app.MapPost("/api/v1/billing/bills/{id:guid}/adjust-advance", AdjustAdvance)
            .WithTags("Billing - Payments")
            .RequirePermission(BillingPermissions.PaymentsCollect)
            .WithValidation<AdjustAdvanceRequest>();

        var refunds = app.MapGroup("/api/v1/billing/refunds").WithTags("Billing - Refunds");
        refunds.MapGet("/", ListRefunds).RequireAnyPermission(BillingPermissions.RefundsRequest, BillingPermissions.RefundsApprove, BillingPermissions.ChargesRead);
        refunds.MapGet("/{id:guid}", GetRefund).RequireAnyPermission(BillingPermissions.RefundsRequest, BillingPermissions.RefundsApprove, BillingPermissions.ChargesRead);
        refunds.MapPost("/", RequestRefund).RequirePermission(BillingPermissions.RefundsRequest).WithValidation<RequestRefundRequest>();
        refunds.MapPost("/{id:guid}/approve", ApproveRefund).RequirePermission(BillingPermissions.RefundsApprove).WithValidation<ApproveRefundRequest>();
        refunds.MapPost("/{id:guid}/reject", RejectRefund).RequirePermission(BillingPermissions.RefundsApprove).WithValidation<RejectRefundRequest>();
        refunds.MapPost("/{id:guid}/pay", PayRefund).RequirePermission(BillingPermissions.PaymentsCollect).WithValidation<PayRefundRequest>();
    }

    private static async Task<IResult> ListReceipts(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        Guid? patientId,
        Guid? billId,
        Guid? ipAccountId,
        ReceiptPurpose? purpose,
        string? counter,
        HospitalDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = BillingTime.Today(time);
        var (start, end) = BillingTime.Range(from ?? (patientId is null && billId is null && ipAccountId is null ? today : DateOnly.MinValue.AddDays(1)), to ?? today);
        var rows = db.Set<Receipt>().AsNoTracking().Where(r => r.ReceivedAt >= start && r.ReceivedAt < end);
        if (patientId is not null)
        {
            rows = rows.Where(r => r.PatientId == patientId);
        }

        if (billId is not null)
        {
            rows = rows.Where(r => r.BillId == billId);
        }

        if (ipAccountId is not null)
        {
            rows = rows.Where(r => r.IpAccountId == ipAccountId);
        }

        if (purpose is not null)
        {
            rows = rows.Where(r => r.Purpose == purpose);
        }

        if (!string.IsNullOrWhiteSpace(counter))
        {
            rows = rows.Where(r => r.Counter == counter);
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            rows = rows.Where(r => r.ReceiptNumber == number);
        }

        var result = await rows.Include(r => r.Payments).OrderByDescending(r => r.ReceivedAt).ToPagedResultAsync(page, ct);
        var patients = await PatientQueries.SummariesAsync(db, result.Items.Select(r => r.PatientId), today, ct);
        return TypedResults.Ok(result.Map(r =>
        {
            var patient = patients.GetValueOrDefault(r.PatientId);
            return new ReceiptListItem(
                r.Id, r.ReceiptNumber, r.Purpose, r.PatientId, patient?.Uhid, patient?.FullName, r.BillId, r.Amount, r.ModeSummary, r.Counter,
                r.ReceivedByName, r.ReceivedAt);
        }));
    }

    private static async Task<IResult> GetReceipt(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var receipt = await db.Set<Receipt>().AsNoTracking().Include(r => r.Payments).FirstOrDefaultAsync(r => r.Id == id, ct);
        return receipt is null ? Error.NotFound(nameof(Receipt), id).ToProblem() : TypedResults.Ok(await ToDtoAsync(db, receipt, time, ct));
    }

    private static async Task<IResult> Collect(CollectPaymentRequest request, ReceiptService receipts, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await CreatedReceiptAsync(await receipts.CollectForBillAsync(request, ct), db, time, ct);

    private static async Task<IResult> CollectDeposit(Guid id, CollectDepositRequest request, ReceiptService receipts, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await CreatedReceiptAsync(await receipts.CollectDepositAsync(id, request, ct), db, time, ct);

    private static async Task<IResult> Dues(Guid patientId, HospitalDbContext db, AdvanceService advances, TimeProvider time, CancellationToken ct)
    {
        var rows = await BillQueries.Rows(db.Set<Bill>().AsNoTracking()
                .Where(b => b.PatientId == patientId && b.DueAmount > 0 && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid))
                .OrderBy(b => b.FinalizedAt))
            .ToListAsync(ct);
        var deposits = await db.Set<IpAccount>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Status != IpAccountStatus.Closed)
            .SumAsync(a => a.DepositReceived - a.DepositAdjusted - a.DepositRefunded, ct);
        var open = await advances.ListAsync(patientId, openOnly: true, ct);
        var bills = await BillQueries.ToListItemsAsync(db, rows, time, ct);
        return TypedResults.Ok(new PatientDues(patientId, bills.Sum(b => b.DueAmount), deposits + open.Sum(a => a.Balance), bills, open));
    }

    private static async Task<IResult> ListAdvances(Guid patientId, bool? openOnly, AdvanceService advances, CancellationToken ct) =>
        TypedResults.Ok(await advances.ListAsync(patientId, openOnly ?? false, ct));

    private static async Task<IResult> AdjustAdvance(Guid id, AdjustAdvanceRequest request, AdvanceService advances, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await CreatedReceiptAsync(await advances.AdjustAsync(id, request, ct), db, time, ct);

    private static async Task<IResult> ListRefunds(
        [AsParameters] PageQuery query, RefundStatus? status, Guid? patientId, Guid? billId, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var rows = db.Set<Refund>().AsNoTracking();
        if (status is not null)
        {
            rows = rows.Where(r => r.Status == status);
        }

        if (patientId is not null)
        {
            rows = rows.Where(r => r.PatientId == patientId);
        }

        if (billId is not null)
        {
            rows = rows.Where(r => r.BillId == billId);
        }

        var result = await rows.OrderByDescending(r => r.RequestedAt).ToPagedResultAsync(page, ct);
        var patients = await PatientQueries.SummariesAsync(db, result.Items.Select(r => r.PatientId), BillingTime.Today(time), ct);
        return TypedResults.Ok(result.Map(r => ToDto(r, patients.GetValueOrDefault(r.PatientId))));
    }

    private static async Task<IResult> GetRefund(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var refund = await db.Set<Refund>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (refund is null)
        {
            return Error.NotFound(nameof(Refund), id).ToProblem();
        }

        var patients = await PatientQueries.SummariesAsync(db, [refund.PatientId], BillingTime.Today(time), ct);
        return TypedResults.Ok(ToDto(refund, patients.GetValueOrDefault(refund.PatientId)));
    }

    private static async Task<IResult> RequestRefund(RequestRefundRequest request, RefundService refunds, CancellationToken ct)
    {
        var requested = await refunds.RequestAsync(request, ct);
        return requested.IsSuccess ? TypedResults.Created($"/api/v1/billing/refunds/{requested.Value}", new { id = requested.Value }) : requested.Error!.ToProblem();
    }

    private static async Task<IResult> ApproveRefund(Guid id, ApproveRefundRequest request, RefundService refunds, CancellationToken ct) =>
        (await refunds.ApproveAsync(id, request.Remarks, ct)).ToNoContent();

    private static async Task<IResult> RejectRefund(Guid id, RejectRefundRequest request, RefundService refunds, CancellationToken ct) =>
        (await refunds.RejectAsync(id, request.Reason, ct)).ToNoContent();

    private static async Task<IResult> PayRefund(Guid id, PayRefundRequest request, RefundService refunds, CancellationToken ct) =>
        (await refunds.PayAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> CreatedReceiptAsync(Result<Guid> created, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (created.IsFailure)
        {
            return created.Error!.ToProblem();
        }

        var receipt = await db.Set<Receipt>().AsNoTracking().Include(r => r.Payments).FirstAsync(r => r.Id == created.Value, ct);
        return TypedResults.Created($"/api/v1/billing/receipts/{receipt.Id}", await ToDtoAsync(db, receipt, time, ct));
    }

    private static async Task<ReceiptDto> ToDtoAsync(HospitalDbContext db, Receipt r, TimeProvider time, CancellationToken ct)
    {
        var patient = (await PatientQueries.SummariesAsync(db, [r.PatientId], BillingTime.Today(time), ct)).GetValueOrDefault(r.PatientId);
        var billNumber = r.BillId is { } billId ? await db.Set<Bill>().Where(b => b.Id == billId).Select(b => b.BillNumber).FirstOrDefaultAsync(ct) : null;
        return new ReceiptDto(
            r.Id, r.ReceiptNumber, r.Purpose, r.PatientId, patient?.Uhid, patient?.FullName, r.BillId, billNumber, r.IpAccountId, r.Amount, r.Source,
            r.Counter, r.ReceivedByName, r.ReceivedAt, r.Notes,
            r.Payments.Select(p => new ReceiptPaymentDto(p.Mode, p.Amount, p.Reference, p.CardLast4, p.CardNetwork, p.ChequeNumber, p.ChequeBank, p.ChequeDate)).ToList());
    }

    private static RefundDto ToDto(Refund r, PatientSummary? patient) => new(
        r.Id, r.RefundNumber, r.Purpose, r.PatientId, patient?.Uhid, patient?.FullName, r.BillId, r.IpAccountId, r.Amount, r.Reason, r.Status,
        r.RequestedByName, r.RequestedAt, r.DecidedBy, r.DecidedAt, r.DecisionRemarks, r.Mode, r.Reference, r.PaidAt, r.AdvanceId,
        r.GatewayStatus, r.GatewayReference, r.GatewayError);
}
