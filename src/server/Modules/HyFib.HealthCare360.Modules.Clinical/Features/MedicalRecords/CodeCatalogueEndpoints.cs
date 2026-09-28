using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

/// <summary>
/// /api/v1/mrd — local ICD-10 and procedure catalogues with ranked search (MRD-01, INT-ICD), the optional WHO ICD-API
/// lookup through the platform integration, and maintenance of the local catalogue.
/// </summary>
internal static class CodeCatalogueEndpoints
{
    private const int CandidateLimit = 300;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/mrd").WithTags("Medical Records");

        group.MapGet("/icd-codes", SearchIcd).RequireAnyPermission(ClinicalPermissions.EmrRead, ClinicalPermissions.MrdCoding);
        group.MapGet("/icd-codes/online", SearchOnline).RequireAnyPermission(ClinicalPermissions.EmrRead, ClinicalPermissions.MrdCoding);
        group.MapGet("/icd-classification", Classification).RequireAnyPermission(ClinicalPermissions.EmrRead, ClinicalPermissions.MrdCoding);
        group.MapPost("/icd-codes", (SaveIcdCodeRequest r, HospitalDbContext db, CancellationToken ct) => SaveIcd(null, r, db, ct))
            .RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<SaveIcdCodeRequest>();
        group.MapPut("/icd-codes/{id:guid}", (Guid id, SaveIcdCodeRequest r, HospitalDbContext db, CancellationToken ct) => SaveIcd(id, r, db, ct))
            .RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<SaveIcdCodeRequest>();

        group.MapGet("/procedure-codes", SearchProcedures).RequireAnyPermission(ClinicalPermissions.EmrRead, ClinicalPermissions.MrdCoding);
        group.MapPost("/procedure-codes", (SaveProcedureCodeRequest r, HospitalDbContext db, CancellationToken ct) => SaveProcedure(null, r, db, ct))
            .RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<SaveProcedureCodeRequest>();
        group.MapPut("/procedure-codes/{id:guid}", (Guid id, SaveProcedureCodeRequest r, HospitalDbContext db, CancellationToken ct) => SaveProcedure(id, r, db, ct))
            .RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<SaveProcedureCodeRequest>();
    }

    /// <summary>
    /// Code prefix ("E11", "e119") or title/keyword words ("type 2 diabetes", "T2DM"); candidates come from ILIKE on the
    /// trigram-indexed search text and are ranked in memory (exact code, prefix, title start, whole words, keywords).
    /// </summary>
    private static async Task<IResult> SearchIcd(string? search, string? chapter, bool? billableOnly, string? release, int? limit, HospitalDbContext db,
        CancellationToken ct)
    {
        var codes = db.Set<IcdCode>().AsNoTracking().Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(chapter))
        {
            var ch = chapter.Trim().ToUpperInvariant();
            codes = codes.Where(c => c.Chapter == ch);
        }

        if (billableOnly == true)
        {
            codes = codes.Where(c => c.IsBillable);
        }

        if (!string.IsNullOrWhiteSpace(release))
        {
            var rel = release.Trim();
            codes = codes.Where(c => c.Release == rel);
        }

        var take = Math.Clamp(limit ?? 25, 1, 100);
        if (string.IsNullOrWhiteSpace(search))
        {
            return TypedResults.Ok(await codes.OrderBy(c => c.Code).Take(take).Select(c => ToDto(c)).ToListAsync(ct));
        }

        var candidates = await Candidates(codes, search).Take(CandidateLimit).ToListAsync(ct);
        var ranked = IcdSearchRanker.Rank(candidates, c => new CodeCandidate(c.Code, c.Title, c.Keywords, c.IsBillable), search, take);
        return TypedResults.Ok(ranked.Select(ToDto).ToList());
    }

    private static async Task<IResult> SearchProcedures(string? search, string? category, int? limit, HospitalDbContext db, CancellationToken ct)
    {
        var codes = db.Set<ProcedureCode>().AsNoTracking().Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = category.Trim();
            codes = codes.Where(c => c.Category == cat);
        }

        var take = Math.Clamp(limit ?? 25, 1, 100);
        if (string.IsNullOrWhiteSpace(search))
        {
            return TypedResults.Ok(await codes.OrderBy(c => c.Category).ThenBy(c => c.Title).Take(take)
                .Select(c => new ProcedureCodeDto(c.Id, c.Code, c.Title, c.System, c.Category, c.Keywords, c.Source, c.IsActive)).ToListAsync(ct));
        }

        var q = search.Trim();
        var code = q.ToUpperInvariant();
        var words = IcdSearchRanker.Words(q);
        var candidates = codes.Where(c => c.Code.StartsWith(code));
        var byWords = codes;
        foreach (var word in words)
        {
            var like = word.ToLikePattern();
            byWords = byWords.Where(c => EF.Functions.ILike(c.SearchText, like));
        }

        var rows = await candidates.Union(byWords).Take(CandidateLimit).ToListAsync(ct);
        var ranked = IcdSearchRanker.Rank(rows, c => new CodeCandidate(c.Code, c.Title, c.Keywords, true), q, take);
        return TypedResults.Ok(ranked.Select(c => new ProcedureCodeDto(c.Id, c.Code, c.Title, c.System, c.Category, c.Keywords, c.Source, c.IsActive)).ToList());
    }

    /// <summary>
    /// WHO ICD-API search (ICD-11 MMS by default) through the configured integration. Internet problems never break the
    /// local workflow: the caller gets a clear message and keeps using the local catalogue.
    /// </summary>
    private static async Task<IResult> SearchOnline(string search, string? release, int? limit, HospitalDbContext db, IServiceProvider services,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2)
        {
            return Error.Field("search", "Type at least two characters.").ToProblem();
        }

        if (services.GetService<IIcdLookup>() is not { } lookup)
        {
            return Error.Rule("icd.online_unavailable", "Online ICD lookup is not configured. Use the local catalogue.").ToProblem();
        }

        IReadOnlyList<IcdSearchResult> results;
        try
        {
            results = await lookup.SearchAsync(search.Trim(), string.IsNullOrWhiteSpace(release) ? CodeReleases.Icd11 : release.Trim(), Math.Clamp(limit ?? 20, 1, 50), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            loggers.CreateLogger("Clinical.IcdLookup").LogWarning(ex, "Online ICD lookup failed");
            return Error.Rule("icd.online_failed", "The online ICD service could not be reached. Use the local catalogue or try again later.").ToProblem();
        }

        var codes = results.Select(r => r.Code).ToList();
        var cached = await db.Set<IcdCode>().Where(c => codes.Contains(c.Code)).Select(c => c.Code).ToListAsync(ct);
        return TypedResults.Ok(results.Select(r => new OnlineIcdResult(r.Code, r.Title, r.Release, r.Chapter, r.IsLeaf, cached.Contains(r.Code))).ToList());
    }

    private static IResult Classification() =>
        TypedResults.Ok(IcdClassification.Chapters.Select(c => new
        {
            c.Number,
            c.Title,
            Range = $"{c.Start}-{c.End}",
            Blocks = IcdClassification.Blocks.Where(b => string.CompareOrdinal(b.Start, c.Start) >= 0 && string.CompareOrdinal(b.End, c.End) <= 0)
                .Select(b => new { b.Range, b.Title }).ToList(),
        }).ToList());

    private static async Task<IResult> SaveIcd(Guid? id, SaveIcdCodeRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var release = string.IsNullOrWhiteSpace(request.Release) ? CodeReleases.Icd10 : request.Release.Trim();
        var code = release == CodeReleases.Icd10 ? IcdSearchRanker.NormalizeCode(request.Code) : request.Code.Trim().ToUpperInvariant();
        if (await db.Set<IcdCode>().AnyAsync(c => c.Code == code && c.Release == release && c.Id != id, ct))
        {
            return Error.Conflict("icd.exists", $"{code} ({release}) is already in the catalogue.").ToProblem();
        }

        IcdCode entry;
        if (id is { } existingId)
        {
            var found = await db.Set<IcdCode>().FirstOrDefaultAsync(c => c.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound("ICD code", existingId).ToProblem();
            }

            entry = found;
        }
        else
        {
            entry = new IcdCode { Source = request.FromOnlineLookup ? CodeSource.Online : CodeSource.Manual };
            db.Add(entry);
        }

        entry.Code = code;
        entry.Title = request.Title.Trim();
        entry.Release = release;
        entry.Chapter = release == CodeReleases.Icd10 ? IcdClassification.ChapterOf(code)?.Number : entry.Chapter;
        entry.Block = release == CodeReleases.Icd10 ? IcdClassification.BlockOf(code)?.Range : entry.Block;
        entry.IsBillable = request.IsBillable;
        entry.Keywords = string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords.Trim();
        entry.IsActive = request.IsActive;
        entry.RefreshSearchText();
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/mrd/icd-codes/{entry.Id}", ToDto(entry)) : TypedResults.Ok(ToDto(entry));
    }

    private static async Task<IResult> SaveProcedure(Guid? id, SaveProcedureCodeRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var system = string.IsNullOrWhiteSpace(request.System) ? CodeReleases.Icd10Pcs : request.System.Trim();
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<ProcedureCode>().AnyAsync(c => c.Code == code && c.System == system && c.Id != id, ct))
        {
            return Error.Conflict("procedure.exists", $"{code} ({system}) is already in the catalogue.").ToProblem();
        }

        ProcedureCode entry;
        if (id is { } existingId)
        {
            var found = await db.Set<ProcedureCode>().FirstOrDefaultAsync(c => c.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound("Procedure code", existingId).ToProblem();
            }

            entry = found;
        }
        else
        {
            entry = new ProcedureCode { Source = CodeSource.Manual };
            db.Add(entry);
        }

        entry.Code = code;
        entry.Title = request.Title.Trim();
        entry.System = system;
        entry.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        entry.Keywords = string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords.Trim();
        entry.IsActive = request.IsActive;
        entry.RefreshSearchText();
        await db.SaveChangesAsync(ct);
        var dto = new ProcedureCodeDto(entry.Id, entry.Code, entry.Title, entry.System, entry.Category, entry.Keywords, entry.Source, entry.IsActive);
        return id is null ? TypedResults.Created($"/api/v1/mrd/procedure-codes/{entry.Id}", dto) : TypedResults.Ok(dto);
    }

    /// <summary>Candidates: code prefix (normalised, e.g. "e119" → "E11.9") or every query word in the search text.</summary>
    private static IQueryable<IcdCode> Candidates(IQueryable<IcdCode> codes, string search)
    {
        var q = search.Trim();
        var byWords = codes;
        foreach (var word in IcdSearchRanker.Words(q))
        {
            var like = word.ToLikePattern();
            byWords = byWords.Where(c => EF.Functions.ILike(c.SearchText, like));
        }

        if (!IcdSearchRanker.LooksLikeCode(q))
        {
            return byWords;
        }

        var prefix = IcdSearchRanker.NormalizeCode(q);
        var compact = prefix.Replace(".", string.Empty, StringComparison.Ordinal);
        return codes.Where(c => c.Code.StartsWith(prefix) || c.Code.StartsWith(compact)).Union(byWords);
    }

    private static IcdCodeDto ToDto(IcdCode c) => new(
        c.Id, c.Code, c.Title, c.Release, c.Chapter, c.Block, IcdClassification.BlockOf(c.Code)?.Title, c.IsBillable, c.Keywords, c.Source, c.IsActive);
}
