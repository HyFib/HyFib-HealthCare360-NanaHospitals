namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>
/// One table of the pre-downtime pack, ready to print: a heading (a ward, a patient's medication sheet, a theatre), its
/// columns and its rows, all as text. Every section of the pack is made of these, so the staff app prints them, and
/// shows them offline, the same way whichever module wrote them.
/// </summary>
/// <param name="Note">A line under the heading: the patient's allergies on a medication sheet, a ward's bed counts.</param>
/// <param name="Alert">Something the reader must not miss: "ALLERGIES NOT RECORDED", "Isolation: airborne".</param>
public sealed record DowntimePackTable(string Heading, string? Note, string? Alert, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>A section of the pack: the census, the medication sheets, the pending orders, the OT list, the downtime numbers.</summary>
/// <param name="Key">Stable, for the staff app: "census", "mar", "orders", "ot", "numbers", "windows".</param>
/// <param name="Note">What the section holds and from when to when, printed under its title.</param>
/// <param name="PagePerTable">Print each table on its own page (a medication sheet goes to the patient's bedside).</param>
public sealed record DowntimePackSection(string Key, string Title, string? Note, bool PagePerTable, IReadOnlyList<DowntimePackTable> Tables);

/// <summary>What a module adding to the pack is told: whose pack, its time, and whether restricted names may be printed.</summary>
/// <param name="WindowEnd">The pack covers what is due until then (24 hours from <paramref name="Now"/>).</param>
/// <param name="CanSeeRestricted">
/// The one printing may open restricted (VIP) records; the nightly pack never may, so it prints "Restricted patient"
/// with the UHID, as the bed board does.
/// </param>
public sealed record DowntimePackContext(Guid FacilityId, DateTimeOffset Now, DateTimeOffset WindowEnd, bool CanSeeRestricted)
{
    public const string RestrictedName = "Restricted patient";

    /// <summary>"Name (UHID)", or "Restricted patient (UHID)" for a restricted patient the reader may not see.</summary>
    public string Patient(string name, string uhid, bool isVip) => $"{(isVip && !CanSeeRestricted ? RestrictedName : name)} ({uhid})";
}

/// <summary>
/// A module's part of the pre-downtime pack. Inpatient writes the census and the medication sheets, Diagnostics the
/// pending orders, SpecialtyCare the OT list, Patients the downtime numbers and back-entry windows. Each reads only its
/// own records, filtered to the facility it is given (the nightly pack is made with no one signed in).
/// </summary>
public interface IDowntimePackSource
{
    Task<IReadOnlyList<DowntimePackSection>> BuildAsync(DowntimePackContext context, CancellationToken ct);
}

/// <summary>The order of the sections in the pack.</summary>
public static class DowntimePackOrder
{
    private static readonly string[] KeyOrder = ["census", "mar", "orders", "ot", "numbers", "windows"];

    public static IReadOnlyList<string> Keys => KeyOrder;

    public static int Of(string key) => Array.IndexOf(KeyOrder, key) is var i and >= 0 ? i : KeyOrder.Length;
}

public enum DowntimePackKind
{
    /// <summary>Made each night by HC360, so a pack less than a day old is always there when it goes down unannounced.</summary>
    Nightly = 0,

    /// <summary>Printed by someone, before a planned downtime or when it is declared.</summary>
    OnDemand,

    /// <summary>
    /// Made by HC360 at any hour (<c>downtime.pack.refreshMinutes</c>), so the ward sheets and medicines in the latest pack
    /// are no older than that. Only the last few hours of them are kept (<see cref="DowntimePackSnapshot.RefreshedKeptFor"/>).
    /// </summary>
    Refreshed,
}

/// <summary>
/// A pack as it was made, kept so the staff app can take the latest one for reading offline and so the hospital can show
/// what the wards had on paper. The nightly ones are kept a week; the printed ones a week too.
/// </summary>
public sealed class DowntimePackSnapshot : AuditableEntity
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    /// <summary>The refreshed packs of the day are kept only this long: each replaces the one before, so they would pile up.</summary>
    public static readonly TimeSpan RefreshedKeptFor = TimeSpan.FromHours(6);

    public Guid FacilityId { get; set; }
    public DowntimePackKind Kind { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public string? GeneratedByName { get; set; }
    public int Patients { get; set; }

    /// <summary>The pack as JSON (<c>DowntimePack</c>).</summary>
    public string Content { get; set; } = "{}";
}
