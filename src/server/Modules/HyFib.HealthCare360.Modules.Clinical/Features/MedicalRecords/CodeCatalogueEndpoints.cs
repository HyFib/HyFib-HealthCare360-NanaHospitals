using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

/// <summary>
/// /api/v1/mrd — local ICD-10 and procedure catalogues with ranked search (MRD-01, INT-ICD), the optional WHO ICD-API
/// lookup through the platform integration, and maintenance of the local catalogue.
/// </summary>
internal static class CodeCatalogueEndpoints
{
    /// <summary>Candidates the database orders and returns for the ranker; enough for the best matches of a 100 000-code catalogue.</summary>
    private const int CandidateLimit = 300;

    private const int MaxQueryLength = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/mrd").WithTags("Medical Records");

        // The code lists hold no patient data: whoever imports them may also look them up, to check an import.
        group.MapGet("/icd-codes", SearchIcd).RequireAnyPermission(ClinicalPermissions.EmrRead, ClinicalPermissions.MrdCoding, ClinicalPermissions.CatalogueImport);
        group.MapGet("/icd-codes/online", SearchOnline).RequireAnyPermission(ClinicalPermissions.EmrRead, ClinicalPermissions.MrdCoding);
        group.MapGet("/icd-classification", Classification)
            .RequireAnyPermission(ClinicalPermissions.EmrRead, ClinicalPermissions.MrdCoding, ClinicalPermissions.CatalogueImport);
        group.MapPost("/icd-codes", (SaveIcdCodeRequest r, HospitalDbContext db, IcdClassificationProvider c, IAuditLogger a, CancellationToken ct) => SaveIcd(null, r, db, c, a, ct))
            .RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<SaveIcdCodeRequest>();
        group.MapPut("/icd-codes/{id:guid}", (Guid id, SaveIcdCodeRequest r, HospitalDbContext db, IcdClassificationProvider c, IAuditLogger a, CancellationToken ct) =>
                SaveIcd(id, r, db, c, a, ct))
            .RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<SaveIcdCodeRequest>();

        group.MapGet("/procedure-codes", SearchProcedures)
            .RequireAnyPermission(ClinicalPermissions.EmrRead, ClinicalPermissions.MrdCoding, ClinicalPermissions.CatalogueImport);
        group.MapPost("/procedure-codes", (SaveProcedureCodeRequest r, HospitalDbContext db, IAuditLogger a, CancellationToken ct) => SaveProcedure(null, r, db, a, ct))
            .RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<SaveProcedureCodeRequest>();
        group.MapPut("/procedure-codes/{id:guid}", (Guid id, SaveProcedureCodeRequest r, HospitalDbContext db, IAuditLogger a, CancellationToken ct) =>
                SaveProcedure(id, r, db, a, ct))
            .RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<SaveProcedureCodeRequest>();
    }

    /// <summary>
    /// Code prefix ("E11", "e119") or title, keyword and inclusion-term words ("type 2 diabetes", "T2DM"). The database
    /// finds candidates with ILIKE on the trigram-indexed search text and orders them in the ranker's tiers (see
    /// <see cref="Ordered"/>) before taking the best 300; the ranker orders those.
    /// <c>release</c> is <c>ICD-10 2019</c> or <c>ICD-11 MMS</c> (any label <see cref="CodeReleases.Normalize"/> accepts).
    /// </summary>
    private static async Task<IResult> SearchIcd(string? search, string? chapter, bool? billableOnly, string? release, int? limit, HospitalDbContext db,
        IcdClassificationProvider classification, CancellationToken ct)
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
            if (CodeReleases.Normalize(release) is not ({ } rel and (CodeReleases.Icd10 or CodeReleases.Icd11)))
            {
                return Error.Field("release", "Search ICD-10 2019 or ICD-11 MMS.").ToProblem();
            }

            codes = codes.Where(c => c.Release == rel);
        }

        var take = Math.Clamp(limit ?? 25, 1, 100);
        var set = await classification.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(search))
        {
            var first = await codes.OrderBy(c => c.Code).Take(take).ToListAsync(ct);
            return TypedResults.Ok(first.Select(c => ToDto(c, set)).ToList());
        }

        var query = Bounded(search);
        var candidates = await Ordered(Candidates(codes, query), query).Take(CandidateLimit).ToListAsync(ct);
        var ranked = IcdSearchRanker.Rank(candidates, c => new CodeCandidate(c.Code, c.Title, c.Keywords, c.IsBillable, c.IncludedTerms), query, take);
        return TypedResults.Ok(ranked.Select(c => ToDto(c, set)).ToList());
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

        var q = Bounded(search);
        var code = q.ToUpperInvariant();
        var lower = q.ToLowerInvariant();
        var byWords = codes;
        foreach (var word in IcdSearchRanker.Words(q))
        {
            if (word.Length < 3)
            {
                var (start, inside) = WordStart(word);
                byWords = byWords.Where(c => EF.Functions.ILike(c.SearchText, start, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, inside, QueryExtensions.LikeEscape));
            }
            else
            {
                var like = word.ToLikePattern();
                byWords = byWords.Where(c => EF.Functions.ILike(c.SearchText, like, QueryExtensions.LikeEscape));
            }
        }

        // Ordered before the limit (as for ICD codes), so the best of some 80 000 PCS codes are the ones ranked.
        var w = WordPatterns.Of(IcdSearchRanker.Words(q));
        var rows = await codes.Where(c => c.Code.StartsWith(code)).Union(byWords)
            .OrderByDescending(c => c.Code == code)
            .ThenByDescending(c => c.Code.StartsWith(code))
            .ThenByDescending(c => c.Title.ToLower().StartsWith(lower))
            .ThenByDescending(c => c.Title.ToLower().Contains(lower))
            .ThenByDescending(c =>
                (w.S0 == null || EF.Functions.ILike(c.SearchText, w.S0, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I0!, QueryExtensions.LikeEscape))
                && (w.S1 == null || EF.Functions.ILike(c.SearchText, w.S1, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I1!, QueryExtensions.LikeEscape))
                && (w.S2 == null || EF.Functions.ILike(c.SearchText, w.S2, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I2!, QueryExtensions.LikeEscape))
                && (w.S3 == null || EF.Functions.ILike(c.SearchText, w.S3, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I3!, QueryExtensions.LikeEscape))
                && (w.S4 == null || EF.Functions.ILike(c.SearchText, w.S4, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I4!, QueryExtensions.LikeEscape))
                && (w.S5 == null || EF.Functions.ILike(c.SearchText, w.S5, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I5!, QueryExtensions.LikeEscape)))
            .ThenBy(c => c.Title.Length)
            .ThenBy(c => c.Code)
            .Take(CandidateLimit)
            .ToListAsync(ct);
        var ranked = IcdSearchRanker.Rank(rows, c => new CodeCandidate(c.Code, c.Title, c.Keywords, true), q, take);
        return TypedResults.Ok(ranked.Select(c => new ProcedureCodeDto(c.Id, c.Code, c.Title, c.System, c.Category, c.Keywords, c.Source, c.IsActive)).ToList());
    }

    /// <summary>
    /// WHO ICD-API search through the configured integration: ICD-11 MMS by words (the default), or an ICD-10 code and
    /// its subdivisions (<c>release=ICD-10</c>; the ICD-API has no ICD-10 word search). Internet problems never break the
    /// local workflow: the caller gets a clear message and keeps using the local catalogue. Results carry the release
    /// they would be saved under (<c>ICD-11 MMS</c>, <c>ICD-10 2019</c>).
    /// </summary>
    private static async Task<IResult> SearchOnline(string search, string? release, int? limit, HospitalDbContext db, IServiceProvider services,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2)
        {
            return Error.Field("search", "Type at least two characters.").ToProblem();
        }

        var wanted = string.IsNullOrWhiteSpace(release) ? CodeReleases.Icd11 : CodeReleases.Normalize(release);
        if (wanted is not (CodeReleases.Icd10 or CodeReleases.Icd11))
        {
            return Error.Field("release", "Search ICD-11 MMS, or look up an ICD-10 code.").ToProblem();
        }

        var text = Bounded(search);
        if (wanted == CodeReleases.Icd10)
        {
            text = IcdSearchRanker.NormalizeCode(text);
            if (!CodeReleases.IsIcd10Code(text))
            {
                return Error.Field("search", "WHO's ICD-10 service looks codes up by code only, for example E11. To search by words, use the local catalogue.")
                    .ToProblem();
            }
        }

        if (services.GetService<IIcdLookup>() is not { } lookup)
        {
            return Error.Rule("icd.online_unavailable", "Online ICD lookup is not configured. Use the local catalogue.").ToProblem();
        }

        IReadOnlyList<IcdSearchResult> results;
        try
        {
            results = await lookup.SearchAsync(text, wanted, Math.Clamp(limit ?? 20, 1, 50), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            loggers.CreateLogger("Clinical.IcdLookup").LogWarning(ex, "Online ICD lookup failed");
            return Error.Rule("icd.online_failed", "The online ICD service could not be reached. Use the local catalogue or try again later.").ToProblem();
        }

        var codes = results.Select(r => r.Code).ToList();
        var cached = (await db.Set<IcdCode>().Where(c => codes.Contains(c.Code)).Select(c => new { c.Code, c.Release }).ToListAsync(ct))
            .Select(c => (c.Code, Release: CodeReleases.Normalize(c.Release) ?? c.Release))
            .ToHashSet();
        return TypedResults.Ok(results.Select(r =>
        {
            var saveAs = CodeReleases.Normalize(r.Release) ?? wanted;
            return new OnlineIcdResult(r.Code, r.Title, saveAs, r.Chapter, r.IsLeaf, cached.Contains((r.Code, saveAs)));
        }).ToList());
    }

    /// <summary>The ICD-10 chapters with their blocks: the imported WHO file's, or the built-in list until one is imported.</summary>
    private static async Task<IResult> Classification(IcdClassificationProvider classification, CancellationToken ct)
    {
        var set = await classification.GetAsync(ct);
        return TypedResults.Ok(set.Chapters
            .Select(c => new IcdChapterDto(c.Number, c.Title, $"{c.Start}-{c.End}", set.BlocksIn(c).Select(b => new IcdBlockDto(b.Range, b.Title)).ToList()))
            .ToList());
    }

    private static async Task<IResult> SaveIcd(Guid? id, SaveIcdCodeRequest request, HospitalDbContext db, IcdClassificationProvider classification, IAuditLogger audit,
        CancellationToken ct)
    {
        var release = string.IsNullOrWhiteSpace(request.Release) ? CodeReleases.Icd10 : CodeReleases.Normalize(request.Release);
        if (release is not (CodeReleases.Icd10 or CodeReleases.Icd11))
        {
            return Error.Field("release", "Choose ICD-10 (WHO 2019) or ICD-11 MMS. Procedure codes go in the procedure catalogue.").ToProblem();
        }

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

        // A new code, or a changed code or release, must have the release's form (E11.9 is ICD-10, 5A11 is ICD-11), so the
        // code means the same in claims and in records shared with other providers.
        if ((id is null || entry.Code != code || entry.Release != release) && CodeReleases.OfDiagnosisCode(code) != release)
        {
            return Error.Field("code", release == CodeReleases.Icd10
                ? $"{code} is not an ICD-10 code. ICD-10 codes are a letter and two digits, then a dot and more characters (E11.9)."
                : $"{code} is not an ICD-11 MMS code. ICD-11 codes have a letter in second place (5A11, 1A00).").ToProblem();
        }

        var before = id is null ? null : Snapshot(entry);
        var set = await classification.GetAsync(ct);
        var title = request.Title.Trim();
        var edited = entry.Code != code || entry.Release != release || entry.Title != title
                     || entry.IsBillable != request.IsBillable;
        entry.Code = code;
        entry.Title = title;
        entry.Release = release;
        entry.Chapter = release == CodeReleases.Icd10 ? set.ChapterOf(code)?.Number : entry.Chapter;
        entry.Block = release == CodeReleases.Icd10 ? set.BlockOf(code)?.Range : entry.Block;
        entry.IsBillable = request.IsBillable;
        entry.Keywords = string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords.Trim();
        var switchedBackOn = request.IsActive && entry.WithdrawnAt is not null;
        entry.IsActive = request.IsActive;
        if (request.IsActive)
        {
            entry.WithdrawnAt = null;
        }

        entry.Source = KeptByImports(entry.Source, edited || switchedBackOn);
        entry.RefreshSearchText();
        AuditEdit(audit, nameof(IcdCode), entry.Id, before, Snapshot(entry));
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/mrd/icd-codes/{entry.Id}", ToDto(entry, set)) : TypedResults.Ok(ToDto(entry, set));
    }

    private static async Task<IResult> SaveProcedure(Guid? id, SaveProcedureCodeRequest request, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
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

        var before = id is null ? null : Snapshot(entry);
        var title = request.Title.Trim();
        var category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        var edited = entry.Code != code || entry.System != system || entry.Title != title || entry.Category != category;
        entry.Code = code;
        entry.Title = title;
        entry.System = system;
        entry.Category = category;
        entry.Keywords = string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords.Trim();
        var switchedBackOn = request.IsActive && entry.WithdrawnAt is not null;
        entry.IsActive = request.IsActive;
        if (request.IsActive)
        {
            entry.WithdrawnAt = null;
        }

        entry.Source = KeptByImports(entry.Source, edited || switchedBackOn);
        entry.RefreshSearchText();
        AuditEdit(audit, nameof(ProcedureCode), entry.Id, before, Snapshot(entry));
        await db.SaveChangesAsync(ct);
        var dto = new ProcedureCodeDto(entry.Id, entry.Code, entry.Title, entry.System, entry.Category, entry.Keywords, entry.Source, entry.IsActive);
        return id is null ? TypedResults.Created($"/api/v1/mrd/procedure-codes/{entry.Id}", dto) : TypedResults.Ok(dto);
    }

    /// <summary>
    /// A code from the starter list or an imported file that MRD edited by hand (its code, title, whether it is
    /// codable, its category) or switched back on after an import withdrew it becomes the hospital's own code, so the
    /// next import leaves it as it is instead of putting the file's version back. Search words and switching a code off
    /// do not count.
    /// </summary>
    private static CodeSource KeptByImports(CodeSource source, bool editedHere) =>
        editedHere && source is CodeSource.Seed or CodeSource.Import ? CodeSource.Manual : source;

    /// <summary>
    /// Candidates: code prefix (normalised, e.g. "e119" → "E11.9") or every query word in the search text. A word of one
    /// or two letters ("AF", "MI") must begin a word, as a coder means it; anywhere in a word it would match half the
    /// catalogue.
    /// </summary>
    private static IQueryable<IcdCode> Candidates(IQueryable<IcdCode> codes, string search)
    {
        var byWords = codes;
        foreach (var word in IcdSearchRanker.Words(search))
        {
            if (word.Length < 3)
            {
                var (start, inside) = WordStart(word);
                byWords = byWords.Where(c => EF.Functions.ILike(c.SearchText, start, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, inside, QueryExtensions.LikeEscape));
            }
            else
            {
                var like = word.ToLikePattern();
                byWords = byWords.Where(c => EF.Functions.ILike(c.SearchText, like, QueryExtensions.LikeEscape));
            }
        }

        if (!IcdSearchRanker.LooksLikeCode(search))
        {
            return byWords;
        }

        var prefix = IcdSearchRanker.NormalizeCode(search);
        var compact = prefix.Replace(".", string.Empty, StringComparison.Ordinal);
        return codes.Where(c => c.Code.StartsWith(prefix) || c.Code.StartsWith(compact)).Union(byWords);
    }

    /// <summary>
    /// The database's order before the candidate limit, in the ranker's own tiers so its best matches are among the
    /// candidates however many codes match: the exact code, then codes under the typed prefix (shortest first); for words,
    /// titles that start with the query, titles that hold it, codes where every word begins a word (title, shorthand or
    /// inclusion term), then the shortest titles. Plain LIKE tests: trigram word similarity orders the same way but
    /// costs about 30 µs a row, which a common fragment matching thousands of codes turns into most of a second.
    /// </summary>
    private static IQueryable<IcdCode> Ordered(IQueryable<IcdCode> candidates, string search)
    {
        if (IcdSearchRanker.LooksLikeCode(search))
        {
            var code = IcdSearchRanker.NormalizeCode(search);
            var compact = code.Replace(".", string.Empty, StringComparison.Ordinal);
            return candidates
                .OrderByDescending(c => c.Code == code || c.Code == compact)
                .ThenByDescending(c => c.Code.StartsWith(code) || c.Code.StartsWith(compact))
                .ThenBy(c => c.Code.Length)
                .ThenBy(c => c.Code);
        }

        var lower = search.ToLowerInvariant();
        var w = WordPatterns.Of(IcdSearchRanker.Words(search));
        return candidates
            .OrderByDescending(c => c.Title.ToLower().StartsWith(lower))
            .ThenByDescending(c => c.Title.ToLower().Contains(lower))
            .ThenByDescending(c =>
                (w.S0 == null || EF.Functions.ILike(c.SearchText, w.S0, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I0!, QueryExtensions.LikeEscape))
                && (w.S1 == null || EF.Functions.ILike(c.SearchText, w.S1, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I1!, QueryExtensions.LikeEscape))
                && (w.S2 == null || EF.Functions.ILike(c.SearchText, w.S2, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I2!, QueryExtensions.LikeEscape))
                && (w.S3 == null || EF.Functions.ILike(c.SearchText, w.S3, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I3!, QueryExtensions.LikeEscape))
                && (w.S4 == null || EF.Functions.ILike(c.SearchText, w.S4, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I4!, QueryExtensions.LikeEscape))
                && (w.S5 == null || EF.Functions.ILike(c.SearchText, w.S5, QueryExtensions.LikeEscape) || EF.Functions.ILike(c.SearchText, w.I5!, QueryExtensions.LikeEscape)))
            .ThenBy(c => c.Title.Length)
            .ThenBy(c => c.Code);
    }

    /// <summary>LIKE patterns for a word at the start of the text, and at the start of a later word.</summary>
    private static (string Start, string Inside) WordStart(string word)
    {
        var start = word.ToLikePattern()[1..];
        return (start, "% " + start);
    }

    /// <summary>The query's words (at most six) as <see cref="WordStart"/> patterns; null where there is no word.</summary>
    private sealed record WordPatterns(
        string? S0, string? I0, string? S1, string? I1, string? S2, string? I2, string? S3, string? I3, string? S4, string? I4, string? S5, string? I5)
    {
        public static WordPatterns Of(IReadOnlyList<string> words)
        {
            string? Start(int i) => i < words.Count ? WordStart(words[i]).Start : null;
            string? Inside(int i) => i < words.Count ? WordStart(words[i]).Inside : null;
            return new(Start(0), Inside(0), Start(1), Inside(1), Start(2), Inside(2), Start(3), Inside(3), Start(4), Inside(4), Start(5), Inside(5));
        }
    }

    private static string Bounded(string search)
    {
        var text = search.Trim();
        return text.Length <= MaxQueryLength ? text : text[..MaxQueryLength];
    }

    private static Dictionary<string, object?> Snapshot(IcdCode c) => new()
    {
        ["code"] = c.Code,
        ["release"] = c.Release,
        ["title"] = c.Title,
        ["isBillable"] = c.IsBillable,
        ["keywords"] = c.Keywords,
        ["isActive"] = c.IsActive,
        ["source"] = c.Source.ToString(),
    };

    private static Dictionary<string, object?> Snapshot(ProcedureCode c) => new()
    {
        ["code"] = c.Code,
        ["system"] = c.System,
        ["title"] = c.Title,
        ["category"] = c.Category,
        ["keywords"] = c.Keywords,
        ["isActive"] = c.IsActive,
    };

    /// <summary>
    /// The catalogue rows are not audited automatically (an import writes tens of thousands), so a change MRD makes by hand
    /// is recorded here, as the automatic data audit would: every value of a new code, and from/to for each changed one.
    /// </summary>
    private static void AuditEdit(IAuditLogger audit, string entity, Guid id, Dictionary<string, object?>? before, Dictionary<string, object?> after)
    {
        var details = before is null
            ? after
            : after.Where(a => !Equals(before.GetValueOrDefault(a.Key), a.Value))
                .ToDictionary(a => a.Key, a => (object?)new { from = before.GetValueOrDefault(a.Key), to = a.Value });
        if (before is not null && details.Count == 0)
        {
            return;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, $"{entity}.{(before is null ? "create" : "update")}", $"{after["code"]} {(before is null ? "added" : "changed")} by hand")
        {
            EntityType = entity,
            EntityId = id.ToString(),
            Details = details,
        });
    }

    private static IcdCodeDto ToDto(IcdCode c, IcdClassificationSet set) => new(
        c.Id, c.Code, c.Title, c.Release, c.Chapter, c.Block, c.Release == CodeReleases.Icd10 ? set.BlockOf(c.Code)?.Title : null, c.IsBillable, c.Usage, c.Keywords,
        c.IncludedTerms, c.Source, c.IsActive);
}
