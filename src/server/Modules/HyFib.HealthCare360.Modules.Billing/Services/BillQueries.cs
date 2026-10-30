using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Bill look-ups and DTO mapping shared by the staff screens, the portal and other billing services.</summary>
internal static class BillQueries
{
    /// <summary>Tracked bill with its lines (for changes).</summary>
    public static Task<Bill?> LoadAsync(HospitalDbContext db, Guid billId, CancellationToken ct) =>
        db.Set<Bill>().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == billId, ct);

    public static IQueryable<BillRow> Rows(IQueryable<Bill> bills) => bills.Select(b => new BillRow(
        b.Id, b.BillNumber, b.Type, b.IsFinalBill, b.Status, b.PatientId, b.VisitId, b.AdmissionId, b.PayerType, b.PayerId,
        b.NetAmount, b.PatientAmount, b.PayerAmount, b.PaidAmount, b.DueAmount, b.CreatedAt, b.FinalizedAt));

    public static async Task<PagedResult<BillListItem>> ToListAsync(HospitalDbContext db, PagedResult<BillRow> page, TimeProvider time, CancellationToken ct)
    {
        var items = await ToListItemsAsync(db, page.Items, time, ct);
        return new PagedResult<BillListItem>(items, page.Page, page.PageSize, page.TotalCount);
    }

    public static async Task<IReadOnlyList<BillListItem>> ToListItemsAsync(HospitalDbContext db, IReadOnlyList<BillRow> rows, TimeProvider time, CancellationToken ct)
    {
        var patients = await PatientQueries.SummariesAsync(db, rows.Select(r => r.PatientId), BillingTime.Today(time), fullContacts: false, ct);
        var payerIds = rows.Where(r => r.PayerId is not null).Select(r => r.PayerId!.Value).Distinct().ToList();
        var payers = await db.Set<Payer>().Where(p => payerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(r =>
        {
            var patient = patients.GetValueOrDefault(r.PatientId);
            return new BillListItem(
                r.Id, r.BillNumber, r.Type, r.IsFinalBill, r.Status, r.PatientId, patient?.Uhid, patient?.FullName, r.VisitId, r.AdmissionId,
                r.PayerType, r.PayerId, r.PayerId is { } id ? payers.GetValueOrDefault(id) : null, r.NetAmount, r.PatientAmount, r.PayerAmount,
                r.PaidAmount, r.DueAmount, r.CreatedAt, r.FinalizedAt);
        }).ToList();
    }

    public static async Task<BillDetail?> DetailAsync(HospitalDbContext db, Guid billId, TimeProvider time, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == billId, ct);
        if (bill is null)
        {
            return null;
        }

        var patient = (await PatientQueries.SummariesAsync(db, [bill.PatientId], BillingTime.Today(time), fullContacts: false, ct)).GetValueOrDefault(bill.PatientId);
        var payerName = bill.PayerId is { } payerId ? await db.Set<Payer>().Where(p => p.Id == payerId).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        var planName = bill.TariffPlanId is { } planId ? await db.Set<TariffPlan>().Where(p => p.Id == planId).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        var practitionerIds = bill.Lines.Where(l => l.PractitionerId is not null).Select(l => l.PractitionerId!.Value).Distinct().ToList();
        var practitioners = await db.Set<Practitioner>().Where(p => practitionerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);

        var receipts = await db.Set<Receipt>().AsNoTracking().Include(r => r.Payments).Where(r => r.BillId == billId).OrderBy(r => r.ReceivedAt).ToListAsync(ct);
        var refunds = await db.Set<Refund>().AsNoTracking().Where(r => r.BillId == billId).OrderBy(r => r.RequestedAt).ToListAsync(ct);
        var credits = await db.Set<CreditNote>().AsNoTracking().Where(c => c.BillId == billId).OrderBy(c => c.IssuedAt).ToListAsync(ct);
        var pending = await db.Set<DiscountApproval>().AsNoTracking()
            .Where(a => a.BillId == billId && a.Status == DiscountApprovalStatus.Pending)
            .OrderByDescending(a => a.RequestedAt)
            .FirstOrDefaultAsync(ct);

        var package = await PackageSummaryAsync(db, bill, ct);
        var lines = bill.Lines.OrderBy(l => l.SortOrder).Select(l => new BillLineDto(
            l.Id, l.ChargeId, l.ServiceItemId, l.ServiceCode, l.Description, l.Category, l.SacCode, l.Quantity, l.UnitPrice, l.GrossAmount,
            l.PackageCoveredAmount, l.TariffDiscountAmount, l.LineDiscountAmount, l.BillDiscountShare, l.TaxRate, l.TaxableAmount, l.CgstAmount,
            l.SgstAmount, l.IgstAmount, l.NetAmount, l.IsDiscountAllowed, l.IsPayerPayable, l.PractitionerId,
            l.PractitionerId is { } pid ? practitioners.GetValueOrDefault(pid) : null, l.DepartmentId, l.ServiceDate, IsReturn: l.GrossAmount < 0,
            l.Device)).ToList();

        var einvoice = await db.Set<EInvoice>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Source == "bill" && e.SourceId == bill.Id && e.Status == EInvoiceStatus.Registered, ct);
        return new BillDetail(
            bill.Id, bill.BillNumber, bill.Type, bill.IsFinalBill, bill.Status, bill.FacilityId, bill.PatientId, patient?.Uhid, patient?.FullName,
            bill.VisitId, bill.AdmissionId, bill.IpAccountId, bill.PayerType, bill.PayerId, payerName, bill.TariffPlanId, planName, bill.PolicyId,
            bill.PreAuthorizationId, bill.CoPayPercent, bill.PayerCoverageLimit, bill.BillDiscountPercent, bill.BillDiscountValue, bill.DiscountReason,
            bill.ApprovedDiscountAmount, bill.GrossAmount, bill.PackageCoveredAmount, bill.TariffDiscountAmount, bill.LineDiscountAmount,
            bill.BillDiscountAmount, bill.DiscountAmount, bill.TaxableAmount, bill.CgstAmount, bill.SgstAmount, bill.IgstAmount,
            bill.PlaceOfSupply, bill.IsInterState, bill.TaxAmount, bill.RoundOffAmount,
            bill.NetAmount, bill.PayerAmount, bill.PatientAmount, bill.CoPayAmount, bill.NonPayableAmount, bill.PaidAmount, bill.RefundedAmount,
            bill.CreditAmount, bill.DueAmount, bill.RefundableAmount, bill.PayerSettledAmount, bill.PayerDisallowedAmount,
            IsTaxInvoice: bill.Lines.Any(l => l.TaxAmount > 0), bill.CreatedAt, bill.FinalizedAt, bill.CancelledAt, bill.CancelReason, bill.Notes, lines,
            receipts.Select(ToSummary).ToList(),
            refunds.Select(r => new RefundSummary(r.Id, r.RefundNumber, r.Amount, r.Status, r.Mode, r.RequestedAt, r.PaidAt)).ToList(),
            credits.Select(c => new CreditNoteDto(c.Id, c.Number, c.Kind, c.Amount, c.Reason, c.IssuedAt, c.IssuedByName)).ToList(),
            pending is null ? null : ToDto(pending, bill.BillNumber, patient?.Uhid, patient?.FullName),
            package,
            bill.Version,
            einvoice is null ? null : EInvoiceService.Stamp(einvoice),
            bill.FreeTreatmentReason,
            bill.FreeTreatmentAmount);
    }

    /// <summary>The package pricing a bill: the OP bill's own, or the IP account's on a final bill.</summary>
    private static async Task<BillPackageSummary?> PackageSummaryAsync(HospitalDbContext db, Bill bill, CancellationToken ct)
    {
        var packageId = bill.PackageId;
        if (packageId is null && bill.IsFinalBill && bill.IpAccountId is { } accountId)
        {
            packageId = await db.Set<IpAccount>().Where(a => a.Id == accountId).Select(a => a.PackageId).FirstOrDefaultAsync(ct);
        }

        if (packageId is not { } id)
        {
            return null;
        }

        var package = await db.Set<Package>().Where(p => p.Id == id).Select(p => new { p.Code, p.Name, p.ServiceItemId }).FirstOrDefaultAsync(ct);
        if (package is null)
        {
            return null;
        }

        var services = bill.Lines.Where(l => l.Category != ServiceCategory.Package).ToList();
        var actuals = services.Sum(l => l.GrossAmount);
        var covered = services.Sum(l => l.PackageCoveredAmount);
        var price = bill.Lines.Where(l => l.ServiceItemId == package.ServiceItemId).Sum(l => l.GrossAmount);
        return new BillPackageSummary(id, package.Code, package.Name, price, actuals, covered, actuals - covered);
    }

    public static ReceiptSummary ToSummary(Receipt r) => new(r.Id, r.ReceiptNumber, r.Purpose, r.Amount, r.ModeSummary, r.ReceivedAt);

    public static DiscountApprovalDto ToDto(DiscountApproval a, string? billNumber, string? uhid, string? patientName) => new(
        a.Id, a.BillId, billNumber, a.PatientId, uhid, patientName, a.RequestedAmount, a.DiscountBase, a.BillDiscountPercent, a.BillDiscountAmount,
        a.Reason, a.Status, a.RequestedByName, a.RequestedAt, a.DecidedBy, a.DecidedAt, a.Remarks);
}

internal sealed record BillRow(
    Guid Id,
    string? BillNumber,
    BillType Type,
    bool IsFinalBill,
    BillStatus Status,
    Guid PatientId,
    Guid? VisitId,
    Guid? AdmissionId,
    PayerType PayerType,
    Guid? PayerId,
    decimal NetAmount,
    decimal PatientAmount,
    decimal PayerAmount,
    decimal PaidAmount,
    decimal DueAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinalizedAt);
