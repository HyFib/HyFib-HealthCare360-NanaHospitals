using HyFib.HealthCare360.Modules.Clinical.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;

/// <summary>
/// The ICD-10 chapters and blocks in use (MRD-01, MRD-06): those of the imported WHO file once a hospital has imported
/// one, and the built-in list until then. Read on every ICD search, so it is cached for ten minutes; an import clears
/// the cache on the server that ran it.
/// </summary>
public sealed class IcdClassificationProvider(HospitalDbContext db, IMemoryCache cache)
{
    private const string CacheKey = "clinical:icd10-classification";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public async Task<IcdClassificationSet> GetAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out IcdClassificationSet? cached) && cached is not null)
        {
            return cached;
        }

        var entries = await db.Set<IcdClassificationEntry>().AsNoTracking()
            .Where(e => e.Release == CodeReleases.Icd10)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        var set = From(entries);
        cache.Set(CacheKey, set, Lifetime);
        return set;
    }

    public void Invalidate() => cache.Remove(CacheKey);

    /// <summary>The imported chapters and the blocks that hold categories, in range order; the built-in set when there are none.</summary>
    internal static IcdClassificationSet From(IReadOnlyCollection<IcdClassificationEntry> entries)
    {
        var chapters = entries.Where(e => e.Kind == IcdClassificationKind.Chapter && e.Start.Length > 0)
            .OrderBy(e => e.Start, StringComparer.Ordinal)
            .Select(e => new IcdChapter(e.Code, e.Start, e.End, e.Title))
            .ToList();
        var blocks = entries.Where(e => e.Kind == IcdClassificationKind.Block && e.HoldsCategories)
            .OrderBy(e => e.Start, StringComparer.Ordinal)
            .Select(e => new IcdBlock(e.Start, e.End, e.Title))
            .ToList();
        return chapters.Count == 0 ? IcdClassificationSet.BuiltIn : new IcdClassificationSet(chapters, blocks, fromFile: true);
    }
}
