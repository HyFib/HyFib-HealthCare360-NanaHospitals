using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The history of a bill, for the billing staff who may read bills: the bill (its discounts, finalization, settlement and
/// cancellation), the discount requests, receipts, refunds, credit notes and write-offs on it, who made each change and
/// what it changed.
/// </summary>
internal sealed class BillRecordHistory(HospitalDbContext db, PatientAccessGuard guard) : IRecordHistorySource
{
    private static readonly string[] Readers =
        [BillingPermissions.ChargesRead, BillingPermissions.OpCreate, BillingPermissions.IpManage, BillingPermissions.PaymentsCollect];

    public IReadOnlyCollection<string> EntityTypes { get; } = [nameof(Bill)];

    public IReadOnlyCollection<string> ReadPermissions(string entityType) => Readers;

    public async Task<Result<HistoryRecord>> OpenAsync(string entityType, Guid id, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().Where(b => b.Id == id).Select(b => new { b.PatientId, b.BillNumber }).FirstOrDefaultAsync(ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), id);
        }

        var open = await guard.EnsureCanOpenAsync(bill.PatientId, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        var discounts = await Rows<DiscountApproval>().Where(d => d.BillId == id)
            .Select(d => new HistoryPart(nameof(DiscountApproval), d.Id, "Discount request")).ToListAsync(ct);
        var receipts = await Rows<Receipt>().Where(r => r.BillId == id)
            .Select(r => new { r.Id, r.ReceiptNumber }).ToListAsync(ct);
        var refunds = await Rows<Refund>().Where(r => r.BillId == id)
            .Select(r => new { r.Id, r.RefundNumber }).ToListAsync(ct);
        var credits = await Rows<CreditNote>().Where(c => c.BillId == id)
            .Select(c => new { c.Id, c.Number, c.Kind }).ToListAsync(ct);
        return new HistoryRecord(bill.BillNumber is { } number ? $"bill {number}" : "draft bill", bill.PatientId,
        [
            .. discounts,
            .. receipts.Select(r => new HistoryPart(nameof(Receipt), r.Id, $"Receipt {r.ReceiptNumber}")),
            .. refunds.Select(r => new HistoryPart(nameof(Refund), r.Id, $"Refund {r.RefundNumber}")),
            .. credits.Select(c => new HistoryPart(nameof(CreditNote), c.Id, $"{(c.Kind == CreditNoteKind.WriteOff ? "Write-off" : "Credit note")} {c.Number}")),
        ]);
    }

    // Deleted rows too: their deletion is the bill's history. The facility's only, as the bill is.
    private IQueryable<T> Rows<T>()
        where T : class => db.Set<T>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking();
}
