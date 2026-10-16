using HyFib.HealthCare360.Modules.Billing.Documents;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Localization;

namespace HyFib.HealthCare360.Modules.Billing.Features.Portal;

/// <summary>
/// /api/v1/portal/bills — the patient portal's bills (PP-02), their PDFs, and "pay now" (PP-03). Every call is limited to
/// the patients linked to the signed-in mobile number (<see cref="PortalPatientScope"/>); drafts and cancelled bills are
/// never shown.
/// </summary>
internal static class PortalBillEndpoints
{
    private static readonly BillStatus[] Visible = [BillStatus.Finalized, BillStatus.PartiallyPaid, BillStatus.Paid, BillStatus.Refunded];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/portal/bills").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal)
            .RequireFeature(FeatureKeys.PatientPortal);
        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapGet("/{id:guid}/pdf", Pdf);
        group.MapPost("/{id:guid}/pay", Pay);
        group.MapPost("/{id:guid}/payments/{orderId:guid}/verify", Verify);
    }

    private static async Task<IResult> List([AsParameters] PageQuery query, Guid? patientId, PortalPatientScope scope, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var accessible = await scope.AccessiblePatientIdsAsync(ct);
        if (patientId is { } requested && !accessible.Contains(requested))
        {
            return Error.Forbidden("portal.patient_denied", "This record is not linked to your mobile number.").ToProblem();
        }

        var ids = patientId is { } one ? [one] : accessible.ToList();
        var page = query.ToRequest();
        var result = await db.Set<Bill>().AsNoTracking()
            .Where(b => ids.Contains(b.PatientId) && Visible.Contains(b.Status))
            .OrderByDescending(b => b.FinalizedAt)
            .Select(b => new { b.Id, b.BillNumber, b.Type, b.Status, b.PatientId, b.FinalizedAt, b.NetAmount, b.PatientAmount, b.PaidAmount, b.RefundedAmount, b.DueAmount })
            .ToPagedResultAsync(page, ct);
        var patients = await PatientQueries.SummariesAsync(db, result.Items.Select(b => b.PatientId), BillingTime.Today(time), ct);
        return TypedResults.Ok(result.Map(b => new PortalBillListItem(
            b.Id, b.BillNumber, b.Type, b.Status, b.PatientId, patients.GetValueOrDefault(b.PatientId)?.FullName, b.FinalizedAt, b.NetAmount, b.PatientAmount,
            b.PaidAmount - b.RefundedAmount, b.DueAmount)));
    }

    private static async Task<IResult> Get(Guid id, PortalPatientScope scope, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var bill = await FindVisibleAsync(db, scope, id, ct);
        if (bill.IsFailure)
        {
            return bill.Error!.ToProblem();
        }

        var b = bill.Value;
        var patient = (await PatientQueries.SummariesAsync(db, [b.PatientId], BillingTime.Today(time), ct)).GetValueOrDefault(b.PatientId);
        var receipts = await db.Set<Receipt>().AsNoTracking().Include(r => r.Payments)
            .Where(r => r.BillId == id && r.Purpose != ReceiptPurpose.PayerSettlement)
            .OrderBy(r => r.ReceivedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(new PortalBillDetail(
            b.Id, b.BillNumber, b.Type, b.Status, b.PatientId, patient?.FullName, b.FinalizedAt, b.GrossAmount, b.DiscountAmount, b.TaxAmount, b.RoundOffAmount,
            b.NetAmount, b.PayerAmount, b.PatientAmount, b.PaidAmount - b.RefundedAmount, b.DueAmount,
            b.Lines.OrderBy(l => l.SortOrder)
                .Select(l => new PortalBillLine(
                    l.Description, l.Quantity, l.UnitPrice, l.TariffDiscountAmount + l.LineDiscountAmount + l.BillDiscountShare + l.PackageCoveredAmount, l.TaxAmount,
                    l.NetAmount, l.ServiceDate, IsReturn: l.GrossAmount < 0))
                .ToList(),
            receipts.Select(BillQueries.ToSummary).ToList()));
    }

    /// <summary>The bill as a PDF: the same A4 document the counter prints, for the bills the patient can see above.</summary>
    private static async Task<IResult> Pdf(
        Guid id, PortalPatientScope scope, HospitalDbContext db, DocumentAssets assets, IPdfRenderer renderer, IAuditLogger audit, TimeProvider time, HttpContext http,
        CancellationToken ct)
    {
        var bill = await FindVisibleAsync(db, scope, id, ct);
        if (bill.IsFailure)
        {
            return bill.Error!.ToProblem();
        }

        var detail = await BillQueries.DetailAsync(db, id, time, ct);
        if (detail is null)
        {
            return Error.NotFound(nameof(Bill), id).ToProblem();
        }

        await audit.PatientRecordAccessedAsync(detail.PatientId, $"Downloaded bill {detail.BillNumber} as a PDF (patient portal)", ct);
        return await (await BillDocument.BuildAsync(detail, assets, DocumentText.For(http.PatientLanguage()), ct)).ToPdfAsync(renderer, ct);
    }

    /// <summary>Creates a hosted-checkout order for the bill's balance.</summary>
    private static async Task<IResult> Pay(Guid id, PortalPatientScope scope, HospitalDbContext db, PaymentGatewayService gateway, CancellationToken ct)
    {
        var bill = await FindVisibleAsync(db, scope, id, ct);
        if (bill.IsFailure)
        {
            return bill.Error!.ToProblem();
        }

        var order = await gateway.CreatePortalOrderAsync(id, ct);
        if (order.IsFailure)
        {
            return order.Error!.ToProblem();
        }

        var o = order.Value;
        return TypedResults.Ok(new PortalPaymentOrder(o.Id, o.Provider, o.OrderId, o.Amount, o.Currency, o.CheckoutKey, o.LinkUrl));
    }

    /// <summary>After checkout, confirms the payment with the gateway (server to server) and records it if captured.</summary>
    private static async Task<IResult> Verify(Guid id, Guid orderId, PortalPatientScope scope, HospitalDbContext db, PaymentGatewayService gateway, CancellationToken ct)
    {
        var bill = await FindVisibleAsync(db, scope, id, ct);
        if (bill.IsFailure)
        {
            return bill.Error!.ToProblem();
        }

        var order = await db.Set<GatewayOrder>().FirstOrDefaultAsync(o => o.Id == orderId && o.BillId == id, ct);
        if (order is null)
        {
            return Error.NotFound(nameof(GatewayOrder), orderId).ToProblem();
        }

        var synced = await gateway.SyncAsync(order, ct);
        if (synced.IsFailure)
        {
            return synced.Error!.ToProblem();
        }

        var due = await db.Set<Bill>().AsNoTracking().Where(b => b.Id == id).Select(b => b.DueAmount).FirstAsync(ct);
        return TypedResults.Ok(new PortalPaymentStatus(synced.Value.Status, synced.Value.ReceiptId, due));
    }

    private static async Task<Result<Bill>> FindVisibleAsync(HospitalDbContext db, PortalPatientScope scope, Guid id, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == id && Visible.Contains(b.Status), ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), id);
        }

        var access = await scope.EnsureAccessAsync(bill.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        return bill;
    }
}
