using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Patients' part of the pre-downtime pack: the downtime numbers each desk and counter holds (UHIDs, visits, bills,
/// receipts), with the next one to use, and the back-entry windows the paper will be entered within.
/// </summary>
internal sealed class DowntimePackNumbers(HospitalDbContext db, IEnumerable<DowntimeNumberSeries> series, BackEntryService backEntry) : IDowntimePackSource
{
    public async Task<IReadOnlyList<DowntimePackSection>> BuildAsync(DowntimePackContext context, CancellationToken ct)
    {
        var rows = new List<IReadOnlyList<string>>();

        // Blocks are not facility-scoped entities: the facility is filtered here, so the nightly pack needs no sign-in.
        var uhidBlocks = await db.Set<DowntimeUhidBlock>().AsNoTracking()
            .Where(b => b.FacilityId == context.FacilityId)
            .OrderBy(b => b.CreatedAt)
            .Select(b => new
            {
                b.Label,
                b.FirstUhid,
                b.LastUhid,
                Next = db.Set<DowntimeUhid>().Where(u => u.BlockId == b.Id && u.PatientId == null && u.VoidedAt == null).OrderBy(u => u.Position).Select(u => u.Uhid).FirstOrDefault(),
                Open = db.Set<DowntimeUhid>().Count(u => u.BlockId == b.Id && u.PatientId == null && u.VoidedAt == null),
            })
            .ToListAsync(ct);
        rows.AddRange(uhidBlocks.Where(b => b.Open > 0).Select(b => (IReadOnlyList<string>)[b.Label, "UHID", $"{b.FirstUhid} – {b.LastUhid}", b.Next ?? "", Count(b.Open)]));

        var numberBlocks = await db.Set<DowntimeNumberBlock>().AsNoTracking()
            .Where(b => b.FacilityId == context.FacilityId && b.ReturnedAt == null)
            .OrderBy(b => b.Counter).ThenBy(b => b.CreatedAt)
            .Select(b => new
            {
                b.Counter,
                b.Kind,
                b.FirstNumber,
                b.LastNumber,
                Next = db.Set<DowntimeNumber>().Where(n => n.BlockId == b.Id && n.EntityId == null && n.VoidedAt == null).OrderBy(n => n.Position).Select(n => n.Number).FirstOrDefault(),
                Open = db.Set<DowntimeNumber>().Count(n => n.BlockId == b.Id && n.EntityId == null && n.VoidedAt == null),
            })
            .ToListAsync(ct);
        var names = series.ToDictionary(s => s.Kind, s => s.Name);
        rows.AddRange(numberBlocks.Where(b => b.Open > 0).Select(b => (IReadOnlyList<string>)[
            b.Counter, Capital(names.GetValueOrDefault(b.Kind, b.Kind.ToString())), $"{b.FirstNumber} – {b.LastNumber}", b.Next ?? "", Count(b.Open)]));

        var numbers = new DowntimePackSection("numbers", "Downtime numbers issued",
            "Use each desk's numbers in order, one per paper form, and write the date and time on every form. A spoilt number is crossed out and marked void. Every number is entered when HC360 is back, or voided.",
            false,
            [new DowntimePackTable("Desks and counters", null, rows.Count == 0 ? "No downtime numbers are issued: ask medical records to issue blocks before a planned downtime." : null,
                ["Desk or counter", "Numbers for", "Block", "Next to use", "Left"], rows)]);

        var windows = new List<IReadOnlyList<string>>();
        foreach (var window in BackEntryWindows.All)
        {
            var span = await backEntry.WindowAsync(window.Record, context.FacilityId, ct);
            windows.Add([window.Name, BackEntryService.Words(span)]);
        }

        var windowSection = new DowntimePackSection("windows", "Back-entry windows",
            "When HC360 is back, each paper record is entered with the time on the paper. Paper older than its window is entered only after a second person approves a late back-entry.",
            false,
            [new DowntimePackTable("Enter within", null, null, ["Record", "Window"], windows)]);
        return [numbers, windowSection];
    }

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
