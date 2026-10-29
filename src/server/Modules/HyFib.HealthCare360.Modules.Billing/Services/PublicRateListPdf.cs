using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Documents;
using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The rate list's PDF for anyone to download from the patient portal, made once for each version of the list. The server's
/// browser makes the PDFs staff print and send as well, a few at a time; so downloads nobody signed in for take at most one
/// of its places, and only when the list has changed since its PDF was last made. Each version is kept an hour after it was
/// last asked for.
/// </summary>
public sealed class PublicRateListPdf(IPdfRenderer renderer, IMemoryCache cache)
{
    private static readonly TimeSpan Kept = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>The PDF of <paramref name="document"/>: the one made of the same HTML before, else a new one.</summary>
    /// <exception cref="PdfRenderException">It could not be made.</exception>
    public async Task<byte[]> RenderAsync(PrintableDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var source = document.ToPdfSource();
        var key = (typeof(PublicRateListPdf), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Html))));
        if (cache.TryGetValue(key, out byte[]? made) && made is not null)
        {
            return made;
        }

        await _one.WaitAsync(cancellationToken);
        try
        {
            // Made while this waited for its turn.
            if (cache.TryGetValue(key, out made) && made is not null)
            {
                return made;
            }

            made = await renderer.RenderAsync(source, cancellationToken);
            cache.Set(key, made, new MemoryCacheEntryOptions { SlidingExpiration = Kept });
            return made;
        }
        finally
        {
            _one.Release();
        }
    }
}
