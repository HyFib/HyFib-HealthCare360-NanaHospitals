using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;
using Microsoft.AspNetCore.Mvc;

namespace HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

/// <summary>One catalogue's state: how many codes are in use, where they came from, and its last import.</summary>
public sealed record CatalogueSummaryDto(
    string Release,
    int ActiveCodes,
    int CodableCodes,
    int WithdrawnCodes,
    int SwitchedOffCodes,
    int FromFile,
    int FromStarterList,
    int AddedByHand,
    int SavedFromOnline,
    bool? ClassificationFromFile,
    CatalogueImportResult? LastImport);

/// <summary>The ICD-10, ICD-10-PCS and cached ICD-11 catalogues, and the most recent imports (newest first).</summary>
public sealed record CatalogueStatusDto(IReadOnlyList<CatalogueSummaryDto> Catalogues, IReadOnlyList<CatalogueImportResult> RecentImports);

/// <summary>
/// /api/v1/mrd/catalogue — the state of the local code catalogues and imports of the hospital's own WHO ICD-10 file or
/// the CMS ICD-10-PCS order file (MRD-01, INT-ICD, MB-03).
/// </summary>
internal static class CatalogueEndpoints
{
    private const int RecentImports = 10;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/mrd/catalogue").WithTags("Medical Records");

        group.MapGet("/", Status)
            .RequireAnyPermission(ClinicalPermissions.EmrRead, ClinicalPermissions.MrdCoding, ClinicalPermissions.CatalogueImport);

        group.MapPost("/imports", Import)
            .RequirePermission(ClinicalPermissions.CatalogueImport)
            // Bearer-token API called by the staff app and HC360 Desktop (no cookie authentication), so antiforgery does not apply.
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(CatalogueFileLimits.DefaultMaxUploadBytes + (1024 * 1024)));
    }

    private static async Task<IResult> Status(HospitalDbContext db, IcdClassificationProvider classification, CancellationToken ct)
    {
        var icd = await db.Set<IcdCode>().AsNoTracking()
            .GroupBy(c => new { c.Release, c.Source, c.IsActive, c.IsBillable, Withdrawn = c.WithdrawnAt != null })
            .Select(g => new CountRow(g.Key.Release, g.Key.Source, g.Key.IsActive, g.Key.IsBillable, g.Key.Withdrawn, g.Count()))
            .ToListAsync(ct);
        var procedures = await db.Set<ProcedureCode>().AsNoTracking()
            .Where(c => c.System == CodeReleases.Icd10Pcs)
            .GroupBy(c => new { c.Source, c.IsActive, Withdrawn = c.WithdrawnAt != null })
            .Select(g => new CountRow(CodeReleases.Icd10Pcs, g.Key.Source, g.Key.IsActive, true, g.Key.Withdrawn, g.Count()))
            .ToListAsync(ct);
        var imports = await db.Set<CatalogueImport>().AsNoTracking()
            .OrderByDescending(i => i.ImportedAt)
            .Take(RecentImports)
            .ToListAsync(ct);
        var recent = imports.Select(ToResult).ToList();
        var fromFile = (await classification.GetAsync(ct)).FromFile;

        CatalogueSummaryDto Summary(string release, IEnumerable<CountRow> rows, bool? classifiedFromFile)
        {
            var list = rows.ToList();
            return new CatalogueSummaryDto(
                release,
                list.Where(r => r.IsActive).Sum(r => r.Count),
                list.Where(r => r.IsActive && r.IsBillable).Sum(r => r.Count),
                list.Where(r => r.Withdrawn).Sum(r => r.Count),
                list.Where(r => !r.IsActive && !r.Withdrawn).Sum(r => r.Count),
                list.Where(r => r.Source == CodeSource.Import).Sum(r => r.Count),
                list.Where(r => r.Source == CodeSource.Seed).Sum(r => r.Count),
                list.Where(r => r.Source == CodeSource.Manual).Sum(r => r.Count),
                list.Where(r => r.Source == CodeSource.Online).Sum(r => r.Count),
                classifiedFromFile,
                recent.FirstOrDefault(i => i.Release == release));
        }

        string ReleaseOf(CountRow row) => CodeReleases.Normalize(row.Release) ?? row.Release;
        return TypedResults.Ok(new CatalogueStatusDto(
        [
            Summary(CodeReleases.Icd10, icd.Where(r => ReleaseOf(r) == CodeReleases.Icd10), fromFile),
            Summary(CodeReleases.Icd10Pcs, procedures, null),
            Summary(CodeReleases.Icd11, icd.Where(r => ReleaseOf(r) == CodeReleases.Icd11), null),
        ], recent));
    }

    /// <summary>
    /// Multipart <c>file</c> (the WHO ClaML file or the CMS order file, plain or zipped, at most 100 MB) and optional
    /// <c>dryRun</c>: true reads the file and says what would change without changing anything. 200 for a dry run, 201
    /// for an import; 400 <c>file</c> when the file cannot be imported (the message says why); 409
    /// <c>catalogue.import_running</c> while another import runs.
    /// </summary>
    private static async Task<IResult> Import(IFormFile? file, [FromForm] bool? dryRun, CatalogueImporter importer, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return Error.Field("file", $"Choose the file to import. {CatalogueFile.AcceptedFiles}").ToProblem();
        }

        if (file.Length > importer.Limits.MaxUploadBytes)
        {
            return Error.Field("file", $"The file is larger than {importer.Limits.MaxUploadBytes / (1024 * 1024)} MB. {CatalogueFile.AcceptedFiles}").ToProblem();
        }

        await using var stream = file.OpenReadStream();
        var result = await importer.ImportAsync(new CatalogueImportRequest(stream, file.FileName, dryRun == true), ct);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        return result.Value.DryRun ? TypedResults.Ok(result.Value) : TypedResults.Created("/api/v1/mrd/catalogue", result.Value);
    }

    internal static CatalogueImportResult ToResult(CatalogueImport i) => new(
        i.Id, i.Catalogue, i.Release, i.FileName, i.FileSha256, i.FileSizeBytes, i.SourceTitle, i.SourceVersion, i.CodesInFile, i.Added, i.Updated,
        i.Unchanged, i.Reinstated, i.Withdrawn, i.LeftAlone, i.Chapters, i.Blocks, DryRun: false, i.ImportedAt, i.ImportedByName, i.Channel, i.DurationMs, []);

    private sealed record CountRow(string Release, CodeSource Source, bool IsActive, bool IsBillable, bool Withdrawn, int Count);
}
