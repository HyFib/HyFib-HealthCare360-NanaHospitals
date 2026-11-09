using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The hospital's ICD-10-PCS catalogue for the insurance desk's pre-authorisations: looked up by code prefix or by words
/// of the title and keywords, and checked when a planned procedure is saved. Only ICD-10-PCS entries are offered.
/// </summary>
internal sealed class ProcedureCatalogue(HospitalDbContext db) : IProcedureCatalogue
{
    private const int MaxQueryLength = 100;

    public async Task<IReadOnlyList<CataloguedProcedure>> SearchAsync(string? search, int take, CancellationToken cancellationToken)
    {
        var codes = db.Set<ProcedureCode>().AsNoTracking().Where(c => c.IsActive && c.System == CodeReleases.Icd10Pcs);
        take = Math.Clamp(take, 1, 50);
        var text = (search ?? string.Empty).Trim();
        if (text.Length > MaxQueryLength)
        {
            text = text[..MaxQueryLength];
        }

        if (text.Length > 0)
        {
            var prefix = text.ToUpperInvariant();
            var byWords = codes;
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var like = word.ToLikePattern();
                byWords = byWords.Where(c => EF.Functions.ILike(c.SearchText, like, QueryExtensions.LikeEscape));
            }

            codes = codes.Where(c => c.Code.StartsWith(prefix)).Union(byWords);
            return await codes.OrderByDescending(c => c.Code == prefix).ThenByDescending(c => c.Code.StartsWith(prefix)).ThenBy(c => c.Code)
                .Take(take).Select(c => new CataloguedProcedure(c.Code, c.Title, c.IsActive)).ToListAsync(cancellationToken);
        }

        return await codes.OrderBy(c => c.Code).Take(take)
            .Select(c => new CataloguedProcedure(c.Code, c.Title, c.IsActive)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, CataloguedProcedure>> FindAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return new Dictionary<string, CataloguedProcedure>();
        }

        var wanted = codes.Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();
        return await db.Set<ProcedureCode>().AsNoTracking()
            .Where(c => c.System == CodeReleases.Icd10Pcs && wanted.Contains(c.Code))
            .Select(c => new CataloguedProcedure(c.Code, c.Title, c.IsActive))
            .ToDictionaryAsync(c => c.Code, StringComparer.Ordinal, cancellationToken);
    }
}
