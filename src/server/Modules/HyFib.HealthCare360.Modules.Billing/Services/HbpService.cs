using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>What an import of the HBP master did.</summary>
public sealed record HbpImportResult(string MasterVersion, int Read, int Added, int Updated, int Retired, IReadOnlyList<string> Problems);

/// <summary>A procedure of the HBP master as staff look it up.</summary>
public sealed record HbpProcedureDto(
    string Code,
    string PackageCode,
    string SpecialtyCode,
    string? Specialty,
    string? PackageName,
    string ProcedureName,
    decimal? Rate,
    int? LengthOfStayDays,
    bool ReservedForPublic,
    string? Stratification,
    string MasterVersion,
    bool IsActive);

/// <summary>
/// AB PM-JAY's Health Benefit Package master in HC360 (INS-PMJAY): imported from NHA's file, looked up when a package
/// is mapped to the scheme's procedure, and checked when one is. An import updates the procedures it lists, adds the new
/// ones and retires (does not delete) those it no longer lists, so a package or claim that names a retired procedure
/// still reads; a retired procedure cannot be newly mapped.
/// </summary>
internal sealed class HbpService(HospitalDbContext db, IAuditLogger audit)
{
    public const string Imported = "billing.hbp.imported";

    /// <summary>Problems shown back after an import; the rest are counted.</summary>
    private const int ProblemsShown = 200;

    public async Task<Result<HbpImportResult>> ImportAsync(byte[] content, string masterVersion, CancellationToken ct)
    {
        var read = HbpMasterFile.Read(content);
        if (read.IsFailure)
        {
            return read.Error!;
        }

        var rows = read.Value.Rows;
        if (rows.Count == 0)
        {
            return Error.Field("file", "No procedure in the file could be read: " + (read.Value.Problems.FirstOrDefault() ?? "it has no rows."));
        }

        var version = masterVersion.Trim();
        var existing = await db.Set<HbpProcedure>().ToDictionaryAsync(p => p.Code, StringComparer.Ordinal, ct);
        int added = 0, updated = 0;
        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Code, out var procedure))
            {
                procedure = new HbpProcedure { Code = row.Code };
                db.Add(procedure);
                added++;
            }
            else if (Differs(procedure, row) || !procedure.IsActive || procedure.MasterVersion != version)
            {
                updated++;
            }

            procedure.PackageCode = row.Code[..5];
            procedure.SpecialtyCode = row.SpecialtyCode;
            procedure.Specialty = row.Specialty;
            procedure.PackageName = row.PackageName;
            procedure.ProcedureName = row.ProcedureName;
            procedure.Rate = row.Rate;
            procedure.LengthOfStayDays = row.LengthOfStayDays;
            procedure.ReservedForPublic = row.ReservedForPublic;
            procedure.Stratification = row.Stratification;
            procedure.MasterVersion = version;
            procedure.IsActive = true;
        }

        var listed = rows.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var retired = 0;
        foreach (var procedure in existing.Values.Where(p => p.IsActive && !listed.Contains(p.Code)))
        {
            procedure.IsActive = false;
            retired++;
        }

        var problems = read.Value.Problems;
        audit.Add(new AuditEntry(AuditCategories.Data, Imported, $"PM-JAY HBP master {version} imported: {rows.Count} procedures, {added} new, {retired} retired")
        {
            EntityType = "hbp_master",
            EntityId = version,
            Details = new { version, read = rows.Count, added, updated, retired, problems = problems.Count },
        });
        await db.SaveChangesAsync(ct);

        IReadOnlyList<string> shown = problems.Count <= ProblemsShown
            ? problems
            : [.. problems.Take(ProblemsShown), $"…and {problems.Count - ProblemsShown} more rows not read."];
        return new HbpImportResult(version, rows.Count, added, updated, retired, shown);
    }

    /// <summary>Procedures by code, name, package or specialty (the first 50), active ones unless asked otherwise.</summary>
    public async Task<IReadOnlyList<HbpProcedureDto>> SearchAsync(string? search, string? specialtyCode, bool includeRetired, CancellationToken ct)
    {
        var query = db.Set<HbpProcedure>().AsNoTracking();
        if (!includeRetired)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(specialtyCode))
        {
            var specialty = specialtyCode.Trim().ToUpperInvariant();
            query = query.Where(p => p.SpecialtyCode == specialty);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var code = term.ToUpperInvariant();
            var like = term.ToLikePattern();
            query = query.Where(p => p.Code.StartsWith(code) || EF.Functions.ILike(p.ProcedureName, like, QueryExtensions.LikeEscape)
                || (p.PackageName != null && EF.Functions.ILike(p.PackageName, like, QueryExtensions.LikeEscape))
                || (p.Specialty != null && EF.Functions.ILike(p.Specialty, like, QueryExtensions.LikeEscape)));
        }

        return await query.OrderBy(p => p.Code).Take(50).Select(p => ToDto(p)).ToListAsync(ct);
    }

    public async Task<HbpProcedureDto?> GetAsync(string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var procedure = await db.Set<HbpProcedure>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == normalized, ct);
        return procedure is null ? null : ToDto(procedure);
    }

    /// <summary>The code to keep on a package, or why it cannot be: unknown, or retired by the latest master.</summary>
    public async Task<Result<string?>> CheckForPackageAsync(string? code, string? current, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result<string?>.Ok(null);
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (!HbpMasterFile.IsProcedureCode(normalized))
        {
            return Error.Field("hbpCode", "An HBP procedure code is two letters, three digits and, for a stratified procedure, a letter (for example SG039A).");
        }

        var procedure = await db.Set<HbpProcedure>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == normalized, ct);
        if (procedure is null)
        {
            return Error.Field("hbpCode", $"{normalized} is not in the imported HBP master. Import NHA's latest master first.");
        }

        // A package already mapped to a procedure the latest master retired keeps it until someone changes it.
        return procedure.IsActive || normalized == current
            ? normalized
            : Error.Field("hbpCode", $"{normalized} was retired by HBP master {procedure.MasterVersion}; map the package to its replacement.");
    }

    private static bool Differs(HbpProcedure p, HbpMasterRow row) =>
        p.ProcedureName != row.ProcedureName || p.Rate != row.Rate || p.LengthOfStayDays != row.LengthOfStayDays
        || p.ReservedForPublic != row.ReservedForPublic || p.Specialty != row.Specialty || p.PackageName != row.PackageName
        || p.Stratification != row.Stratification;

    private static HbpProcedureDto ToDto(HbpProcedure p) => new(
        p.Code, p.PackageCode, p.SpecialtyCode, p.Specialty, p.PackageName, p.ProcedureName, p.Rate, p.LengthOfStayDays,
        p.ReservedForPublic, p.Stratification, p.MasterVersion, p.IsActive);
}
