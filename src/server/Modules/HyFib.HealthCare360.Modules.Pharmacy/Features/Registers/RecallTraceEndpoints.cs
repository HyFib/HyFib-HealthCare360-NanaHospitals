using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Registers;

/// <summary>How a recalled batch reached a patient.</summary>
public enum RecallTraceSource
{
    /// <summary>Dispensed at the OP pharmacy against an e-prescription (or a refill).</summary>
    OpDispense = 0,

    /// <summary>Sold over the counter, to a registered patient or a walk-in customer.</summary>
    CounterSale,

    /// <summary>Issued to the ward for an admitted patient.</summary>
    WardIssue,

    /// <summary>Implanted in an operation: the patient carries it.</summary>
    Implant,

    /// <summary>Used on the patient in an operation (a consumable recorded against the surgery).</summary>
    OtConsumable,
}

/// <summary>
/// One hand-over of the recalled batch to a patient. Kept is what the patient was given less what came back; a row with
/// nothing kept is still listed, so the list shows the whole history. For an operation, the document is the surgery's
/// booking number, the store the theatre, and <paramref name="Procedure"/>, <paramref name="SerialNumber"/> and
/// <paramref name="Clinician"/> the surgery, the implant's serial and the surgeon.
/// </summary>
public sealed record RecallTraceRow(
    RecallTraceSource Source,
    DateTimeOffset At,
    string Document,
    string StoreName,
    Guid? PatientId,
    string? Uhid,
    string PatientName,
    string? Phone,
    decimal Quantity,
    decimal Returned,
    decimal Kept,
    string? Procedure = null,
    string? SerialNumber = null,
    string? Clinician = null);

/// <summary>Where the batch is still on hand.</summary>
public sealed record RecallStockRow(string StoreName, decimal OnHand, bool IsBlocked, bool IsRecalled, string? BlockReason);

public sealed record RecallTrace(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    string BatchNumber,
    DateTimeOffset GeneratedAt,
    string GeneratedBy,
    int PatientCount,
    decimal QuantityWithPatients,
    IReadOnlyList<RecallTraceRow> Patients,
    IReadOnlyList<RecallStockRow> Stock);

/// <summary>
/// /api/v1/pharmacy/recalls/trace — from a recalled batch to every patient it was dispensed, sold or issued to (OP dispense,
/// counter sale, ward issue), or used on in an operation (an implant or a consumable, from the modules that record them:
/// <see cref="IRecallTraceSource"/>), with what came back, and the stores still holding it: the list the pharmacy works
/// through to call patients back. It shows patients' names and phone numbers, so every run is audited with who ran it and
/// what for.
/// </summary>
internal static class RecallTraceEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/pharmacy/recalls/trace", Trace).WithTags("Pharmacy").RequirePermission(PharmacyPermissions.Registers);

    private static async Task<IResult> Trace(
        Guid itemId, string batchNumber, HospitalDbContext db, IEnumerable<IRecallTraceSource> sources, IAuditLogger audit, ICurrentUser user,
        TimeProvider time, CancellationToken ct)
    {
        var batch = batchNumber?.Trim().ToUpperInvariant() ?? string.Empty;
        if (batch.Length is 0 or > 40)
        {
            return Error.Field("batchNumber", "Enter the batch number on the recall notice.").ToProblem();
        }

        var item = await db.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => new { i.Code, i.Name }).FirstOrDefaultAsync(ct);
        if (item is null)
        {
            return Error.NotFound(nameof(Item), itemId).ToProblem();
        }

        var stores = db.Set<Store>().AsNoTracking();
        var patients = db.Set<Patient>().AsNoTracking();

        // Every sale that was not cancelled: a walk-in customer has no patient record, only the name and phone given at the counter.
        var sold = await (
                from line in db.Set<PharmacySaleItem>().AsNoTracking()
                join sale in db.Set<PharmacySale>().AsNoTracking() on line.PharmacySaleId equals sale.Id
                where line.ItemId == itemId && line.BatchNumber.ToUpper() == batch && sale.Status != SaleStatus.Cancelled
                select new
                {
                    Source = sale.Type == SaleType.Prescription ? RecallTraceSource.OpDispense : RecallTraceSource.CounterSale,
                    At = sale.SoldAt,
                    Document = sale.InvoiceNumber,
                    StoreName = stores.Where(s => s.Id == sale.StoreId).Select(s => s.Name).FirstOrDefault(),
                    sale.PatientId,
                    Uhid = patients.Where(p => p.Id == sale.PatientId).Select(p => p.Uhid).FirstOrDefault(),
                    PatientName = patients.Where(p => p.Id == sale.PatientId).Select(p => p.FullName).FirstOrDefault() ?? sale.CustomerName,
                    Phone = patients.Where(p => p.Id == sale.PatientId).Select(p => p.Phone).FirstOrDefault() ?? sale.CustomerPhone,
                    line.Quantity,
                    Returned = line.ReturnedQuantity,
                })
            .ToListAsync(ct);

        var issued = await (
                from line in db.Set<IpIssueLine>().AsNoTracking()
                join issue in db.Set<IpIssue>().AsNoTracking() on line.IpIssueId equals issue.Id
                where line.ItemId == itemId && line.BatchNumber.ToUpper() == batch
                select new
                {
                    Source = RecallTraceSource.WardIssue,
                    At = issue.IssuedAt,
                    Document = issue.Number,
                    StoreName = stores.Where(s => s.Id == issue.StoreId).Select(s => s.Name).FirstOrDefault(),
                    PatientId = (Guid?)issue.PatientId,
                    Uhid = patients.Where(p => p.Id == issue.PatientId).Select(p => p.Uhid).FirstOrDefault(),
                    PatientName = patients.Where(p => p.Id == issue.PatientId).Select(p => p.FullName).FirstOrDefault(),
                    Phone = patients.Where(p => p.Id == issue.PatientId).Select(p => p.Phone).FirstOrDefault(),
                    line.Quantity,
                    Returned = line.ReturnedQuantity,
                })
            .ToListAsync(ct);

        // Implants and consumables recorded against an operation, and whatever else another module used on a patient.
        List<RecalledUse> used = [];
        foreach (var source in sources)
        {
            used.AddRange(await source.UsesAsync(itemId, batch, ct));
        }

        var usedOn = used.Select(u => u.PatientId).Distinct().ToList();
        var people = await patients.Where(p => usedOn.Contains(p.Id)).Select(p => new { p.Id, p.Uhid, p.FullName, p.Phone }).ToDictionaryAsync(p => p.Id, ct);
        var operated = used.Select(u =>
        {
            var person = people.GetValueOrDefault(u.PatientId);
            return new RecallTraceRow(u.Kind == RecalledUseKind.Implant ? RecallTraceSource.Implant : RecallTraceSource.OtConsumable, u.At, u.Document,
                u.Place, u.PatientId, person?.Uhid, person?.FullName ?? "Unknown patient", person?.Phone, u.Quantity, 0, u.Quantity, u.Procedure,
                u.SerialNumber, u.Clinician);
        });

        var rows = sold.Concat(issued)
            .Select(r => new RecallTraceRow(r.Source, r.At, r.Document, r.StoreName ?? string.Empty, r.PatientId, r.Uhid, r.PatientName ?? "Walk-in customer",
                r.Phone, r.Quantity, r.Returned, r.Quantity - r.Returned))
            .Concat(operated)
            .OrderBy(r => r.At)
            .ToList();

        var stock = await (
                from b in db.Set<StockBatch>().AsNoTracking()
                where b.ItemId == itemId && b.BatchNumber.ToUpper() == batch && b.QuantityOnHand > 0
                orderby b.QuantityOnHand descending
                select new RecallStockRow(stores.Where(s => s.Id == b.StoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty, b.QuantityOnHand,
                    b.IsBlocked, b.IsRecalled, b.BlockReason))
            .ToListAsync(ct);

        var withPatients = rows.Where(r => r.Kept > 0).ToList();
        var patientCount = withPatients.Select(r => r.PatientId?.ToString() ?? $"{r.PatientName}|{r.Phone}").Distinct().Count();
        var by = user.DisplayName ?? user.UserName ?? "Unknown";

        // Names and phone numbers of every patient who had the batch: who looked, and for which batch, is kept.
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "pharmacy.recall_trace",
            $"Recall trace run for {item.Code} {item.Name} batch {batch} by {by}: {patientCount} patient(s), {rows.Count} hand-over(s)")
        {
            EntityType = nameof(Item),
            EntityId = itemId.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { batch, patients = patientCount, handovers = rows.Count, operations = rows.Count(r => r.Source is RecallTraceSource.Implant or RecallTraceSource.OtConsumable) },
        }, ct);

        return TypedResults.Ok(new RecallTrace(itemId, item.Code, item.Name, batch, time.GetUtcNow(), by, patientCount, withPatients.Sum(r => r.Kept), rows, stock));
    }
}
