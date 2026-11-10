using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// An import of NHA's HBP master, staged for a second person's approval (maker-checker, INS-PMJAY): the procedures read
/// from the file, kept exactly as they will be applied, the file's SHA-256 (so the approver can match it to the copy NHA
/// or the State Health Agency sent), what applying them would change, and the master they were read against. Nothing in
/// the master changes until another person approves the import, and it is applied only over that master. Kept once
/// decided, as the master's history: which master was put in force when, from which file, asked for and approved by whom.
/// </summary>
public sealed class HbpMasterImport : AuditableEntity
{
    /// <summary>The longest list of unread rows kept with an import; the rest are counted.</summary>
    public const int ProblemsKept = 200;

    public string MasterVersion { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA-256 of the file as it was uploaded, in lower-case hex.</summary>
    public string FileSha256 { get; set; } = string.Empty;

    /// <summary>The procedures read from the file (<see cref="HbpMasterImportRows"/>), applied as they are.</summary>
    public string Rows { get; set; } = "[]";

    /// <summary>SHA-256 of <see cref="Rows"/>: the approval names it, and the rows are applied only while they match it.</summary>
    public string RowsSha256 { get; set; } = string.Empty;

    /// <summary>The master the rows were read against (<see cref="HbpMasterDiff.Stamp"/>); they are applied only over it.</summary>
    public string MasterStamp { get; set; } = string.Empty;

    public int Read { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Retired { get; set; }

    /// <summary>How many rows of the file could not be read; the first <see cref="ProblemsKept"/> are in <see cref="Problems"/>.</summary>
    public int ProblemCount { get; set; }

    public List<string> Problems { get; set; } = [];

    public Guid? ApprovalId { get; set; }
    public string AskedByName { get; set; } = string.Empty;
    public DateTimeOffset AskedAt { get; set; }

    /// <summary>When the approved import was applied: the master in force from then.</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    public string? ApprovedByName { get; set; }
}

/// <summary>The stored form of an import's rows: JSON written once, and its SHA-256.</summary>
public static class HbpMasterImportRows
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write(IReadOnlyList<HbpMasterRow> rows) => JsonSerializer.Serialize(rows, Options);

    public static IReadOnlyList<HbpMasterRow> Read(string rows) =>
        JsonSerializer.Deserialize<List<HbpMasterRow>>(rows, Options) ?? throw new InvalidOperationException("The import has no rows.");

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>A procedure whose scheme rate a new master changes.</summary>
public sealed record HbpRateChange(string Code, string ProcedureName, decimal? From, decimal? To);

/// <summary>
/// What putting a master's procedures in force changes in the master in use, procedure by procedure: those it adds, those
/// it changes (or brings back, or names under its own version), and those it retires because it no longer lists them;
/// with the scheme rates it changes and the procedures it newly reserves for public hospitals. Its counts are the ones
/// an import is applied with.
/// </summary>
public sealed record HbpMasterDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Retired,
    IReadOnlyList<HbpRateChange> Rates,
    IReadOnlyList<string> NewlyReserved)
{
    /// <summary>Whether putting the master in force would change nothing.</summary>
    public bool IsEmpty => Added.Count == 0 && Updated.Count == 0 && Retired.Count == 0;

    public static HbpMasterDiff Of(IEnumerable<HbpProcedure> master, IReadOnlyList<HbpMasterRow> rows, string version)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(rows);
        var existing = master.ToDictionary(p => p.Code, StringComparer.Ordinal);
        List<string> added = [], updated = [], reserved = [];
        List<HbpRateChange> rates = [];
        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Code, out var procedure))
            {
                added.Add(row.Code);
                continue;
            }

            if (Differs(procedure, row) || !procedure.IsActive || procedure.MasterVersion != version)
            {
                updated.Add(row.Code);
            }

            if (procedure.Rate != row.Rate)
            {
                rates.Add(new HbpRateChange(row.Code, row.ProcedureName, procedure.Rate, row.Rate));
            }

            if (row.ReservedForPublic && !procedure.ReservedForPublic)
            {
                reserved.Add(row.Code);
            }
        }

        var listed = rows.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        List<string> retired = [.. existing.Values.Where(p => p.IsActive && !listed.Contains(p.Code)).Select(p => p.Code).Order(StringComparer.Ordinal)];
        return new HbpMasterDiff(added, updated, retired, rates, reserved);
    }

    /// <summary>
    /// The master as one value: SHA-256 over every procedure's code and what an import sets, in code order. An approved
    /// import is applied only over the master with the stamp it was read against, so what the approver was shown is
    /// what changes.
    /// </summary>
    public static string Stamp(IEnumerable<HbpProcedure> master)
    {
        ArgumentNullException.ThrowIfNull(master);
        var text = new StringBuilder();
        foreach (var p in master.OrderBy(p => p.Code, StringComparer.Ordinal))
        {
            string?[] fields =
            [
                p.Code, p.SpecialtyCode, p.Specialty, p.PackageName, p.ProcedureName, p.Rate?.ToString("0.00", CultureInfo.InvariantCulture),
                p.LengthOfStayDays?.ToString(CultureInfo.InvariantCulture), p.ReservedForPublic ? "1" : "0", p.Stratification, p.MasterVersion, p.IsActive ? "1" : "0",
            ];
            text.AppendLine(JsonSerializer.Serialize(fields));
        }

        return HbpMasterImportRows.Hash(text.ToString());
    }

    /// <summary>Whether a procedure in the master reads otherwise than the master file's row for it.</summary>
    public static bool Differs(HbpProcedure procedure, HbpMasterRow row)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        ArgumentNullException.ThrowIfNull(row);
        return procedure.ProcedureName != row.ProcedureName || procedure.Rate != row.Rate || procedure.LengthOfStayDays != row.LengthOfStayDays
            || procedure.ReservedForPublic != row.ReservedForPublic || procedure.Specialty != row.Specialty || procedure.PackageName != row.PackageName
            || procedure.Stratification != row.Stratification;
    }
}
