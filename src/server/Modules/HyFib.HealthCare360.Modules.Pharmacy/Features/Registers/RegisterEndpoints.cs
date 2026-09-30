using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Registers;

public sealed record ControlledRegisterRow(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid StoreId,
    string StoreName,
    Guid ItemId,
    string ItemName,
    DrugSchedule Schedule,
    bool IsControlled,
    string BatchNumber,
    DateOnly? ExpiryDate,
    StockMovementType MovementType,
    decimal QuantityIn,
    decimal QuantityOut,
    decimal BalanceAfter,
    string? PatientName,
    string? PatientUhid,
    string? PatientAddress,
    string? PrescriberName,
    string? PrescriberRegistrationNumber,
    string? PrescriptionNumber,
    string? ReferenceNumber,
    string? RecordedBy);

public sealed record ScheduleHRegisterRow(
    DateTimeOffset IssuedAt,
    string DocumentNumber,
    string Source,
    string? PatientName,
    string? PrescriberName,
    string? PrescriptionNumber,
    string ItemName,
    string BatchNumber,
    decimal Quantity);

/// <summary>
/// Minimal RFC 4180 CSV for register exports. Cells that a spreadsheet would treat as formulas are prefixed with
/// an apostrophe (CSV injection), and a UTF-8 BOM lets Excel open Indian names correctly.
/// </summary>
internal static class Csv
{
    public static byte[] Build(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    public static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Date(DateOnly? value) => value?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Time(DateTimeOffset value) => HospitalClock.ToLocalTime(value).ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);

    public static IReadOnlyList<string?> Row(params string?[] cells) => cells;
}

/// <summary>
/// /api/v1/pharmacy/registers — statutory registers (PH-06): the controlled-drug register (Schedule H1/X and NDPS,
/// with running balances) and the Schedule H sales register, viewable and exportable. Exports are audited (PRV-04).
/// </summary>
internal static class RegisterEndpoints
{
    private const int MaxExportDays = 366;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pharmacy/registers").WithTags("Pharmacy");

        group.MapGet("/controlled", Controlled).RequirePermission(PharmacyPermissions.Registers);
        group.MapGet("/controlled/export", ExportControlled).RequirePermission(PharmacyPermissions.Registers);
        group.MapGet("/schedule-h", ScheduleH).RequirePermission(PharmacyPermissions.Registers);
        group.MapGet("/schedule-h/export", ExportScheduleH).RequirePermission(PharmacyPermissions.Registers);
    }

    private static async Task<IResult> Controlled(
        [AsParameters] PageQuery query, Guid? storeId, Guid? itemId, DrugSchedule? schedule, DateOnly? from, DateOnly? to, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var rows = ControlledQuery(db, storeId, itemId, schedule, Range(from, to, time));
        return TypedResults.Ok(await rows.ToPagedResultAsync(page, ct));
    }

    private static async Task<IResult> ExportControlled(
        Guid? storeId, Guid? itemId, DrugSchedule? schedule, DateOnly? from, DateOnly? to, HospitalDbContext db, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var range = Range(from, to, time);
        if (range.To.DayNumber - range.From.DayNumber > MaxExportDays)
        {
            return Error.Field("from", "Export at most one year at a time.").ToProblem();
        }

        var rows = await ControlledQuery(db, storeId, itemId, schedule, range).Take(50_000).ToListAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "pharmacy.register.exported",
            $"Controlled-drug register exported ({range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd}, {rows.Count} lines)")
        {
            Severity = AuditSeverity.Warning,
        }, ct);

        var lines = rows.Select(r => Csv.Row(
                Csv.Time(r.OccurredAt), r.StoreName, r.ItemName, r.IsControlled ? $"{r.Schedule} / NDPS" : r.Schedule.ToString(), r.BatchNumber,
                Csv.Date(r.ExpiryDate), r.MovementType.ToString(), Csv.Number(r.QuantityIn), Csv.Number(r.QuantityOut), Csv.Number(r.BalanceAfter),
                r.PatientName, r.PatientUhid, r.PatientAddress, r.PrescriberName, r.PrescriberRegistrationNumber, r.PrescriptionNumber,
                r.ReferenceNumber, r.RecordedBy))
            .Prepend(Csv.Row("Date/time", "Store", "Drug", "Schedule", "Batch", "Expiry", "Movement", "In", "Out", "Balance", "Patient", "UHID",
                "Address", "Prescriber", "Reg. no.", "Prescription", "Reference", "Recorded by"));
        return TypedResults.File(Csv.Build(lines), "text/csv; charset=utf-8", string.Create(CultureInfo.InvariantCulture, $"controlled-drug-register-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}.csv"));
    }

    private static async Task<IResult> ScheduleH(
        [AsParameters] PageQuery query, Guid? storeId, DateOnly? from, DateOnly? to, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var rows = await ScheduleHRowsAsync(db, storeId, Range(from, to, time), ct);
        return TypedResults.Ok(new PagedResult<ScheduleHRegisterRow>(rows.Skip(page.Skip).Take(page.Take).ToList(), page.Page, page.Take, rows.Count));
    }

    private static async Task<IResult> ExportScheduleH(
        Guid? storeId, DateOnly? from, DateOnly? to, HospitalDbContext db, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var range = Range(from, to, time);
        if (range.To.DayNumber - range.From.DayNumber > MaxExportDays)
        {
            return Error.Field("from", "Export at most one year at a time.").ToProblem();
        }

        var rows = await ScheduleHRowsAsync(db, storeId, range, ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "pharmacy.register.exported",
            $"Schedule H register exported ({range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd}, {rows.Count} lines)")
        {
            Severity = AuditSeverity.Warning,
        }, ct);

        var lines = rows.Select(r => Csv.Row(
                Csv.Time(r.IssuedAt), r.DocumentNumber, r.Source, r.PatientName, r.PrescriberName, r.PrescriptionNumber, r.ItemName, r.BatchNumber,
                Csv.Number(r.Quantity)))
            .Prepend(Csv.Row("Date/time", "Document", "Source", "Patient", "Prescriber", "Prescription", "Drug", "Batch", "Quantity"));
        return TypedResults.File(Csv.Build(lines), "text/csv; charset=utf-8", string.Create(CultureInfo.InvariantCulture, $"schedule-h-register-{range.From:yyyyMMdd}-{range.To:yyyyMMdd}.csv"));
    }

    private static IQueryable<ControlledRegisterRow> ControlledQuery(
        HospitalDbContext db, Guid? storeId, Guid? itemId, DrugSchedule? schedule, (DateOnly From, DateOnly To) range)
    {
        var (start, end) = HospitalClock.Range(range.From, range.To);
        var entries = db.Set<ControlledDrugRegisterEntry>().AsNoTracking().Where(r => r.OccurredAt >= start && r.OccurredAt < end);
        if (storeId is not null)
        {
            entries = entries.Where(r => r.StoreId == storeId);
        }

        if (itemId is not null)
        {
            entries = entries.Where(r => r.ItemId == itemId);
        }

        if (schedule is not null)
        {
            entries = entries.Where(r => r.Schedule == schedule);
        }

        // Register order: by drug, then chronologically, so each drug's running balance reads top-down.
        return entries.OrderBy(r => r.ItemName).ThenBy(r => r.StoreId).ThenBy(r => r.OccurredAt).ThenBy(r => r.Id)
            .Select(r => new ControlledRegisterRow(
                r.Id, r.OccurredAt, r.StoreId,
                db.Set<Store>().Where(s => s.Id == r.StoreId).Select(s => s.Name).FirstOrDefault() ?? string.Empty,
                r.ItemId, r.ItemName, r.Schedule, r.IsControlled, r.BatchNumber, r.ExpiryDate, r.MovementType, r.QuantityIn, r.QuantityOut,
                r.BalanceAfter, r.PatientName, r.PatientUhid, r.PatientAddress, r.PrescriberName, r.PrescriberRegistrationNumber,
                r.PrescriptionNumber, r.ReferenceNumber,
                db.Set<User>().Where(u => u.Id == r.RecordedByUserId).Select(u => u.DisplayName).FirstOrDefault()));
    }

    /// <summary>Schedule H medicines handed out at the counters and to the wards in the period (Rule 65: record of H sales).</summary>
    private static async Task<List<ScheduleHRegisterRow>> ScheduleHRowsAsync(HospitalDbContext db, Guid? storeId, (DateOnly From, DateOnly To) range, CancellationToken ct)
    {
        var (start, end) = HospitalClock.Range(range.From, range.To);
        var sold = await (
                from i in db.Set<PharmacySaleItem>().AsNoTracking()
                join s in db.Set<PharmacySale>() on i.PharmacySaleId equals s.Id
                where i.Schedule == DrugSchedule.H && s.Status != SaleStatus.Cancelled && s.SoldAt >= start && s.SoldAt < end
                      && (storeId == null || s.StoreId == storeId)
                select new ScheduleHRegisterRow(s.SoldAt, s.InvoiceNumber, "Pharmacy sale", s.CustomerName, s.PrescriberName, s.PrescriptionNumber,
                    i.ItemName, i.BatchNumber, i.Quantity - i.ReturnedQuantity))
            .Take(20_000)
            .ToListAsync(ct);
        var issued = await (
                from l in db.Set<IpIssueLine>().AsNoTracking()
                join i in db.Set<IpIssue>() on l.IpIssueId equals i.Id
                join p in db.Set<Patient>() on i.PatientId equals p.Id
                where l.Schedule == DrugSchedule.H && i.IssuedAt >= start && i.IssuedAt < end && (storeId == null || i.StoreId == storeId)
                select new ScheduleHRegisterRow(i.IssuedAt, i.Number, "IP issue", p.FullName, null, null, l.ItemName, l.BatchNumber, l.Quantity - l.ReturnedQuantity))
            .Take(20_000)
            .ToListAsync(ct);
        return sold.Concat(issued).Where(r => r.Quantity > 0).OrderBy(r => r.IssuedAt).ToList();
    }

    private static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, TimeProvider time)
    {
        var today = HospitalClock.Today(time);
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        return start > end ? (end, end) : (start, end);
    }
}
