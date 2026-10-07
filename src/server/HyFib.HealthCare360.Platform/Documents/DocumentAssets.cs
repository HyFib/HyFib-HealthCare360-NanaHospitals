using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QRCoder;

namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>
/// The hospital as printed at the top of its documents, with its logo ready to embed, and how this kind of document is
/// printed (<see cref="Template"/>). The logo is left out when the template hides it.
/// </summary>
public sealed record DocumentLetterhead(
    string Name,
    string? LegalName,
    string? Address,
    string? Phone,
    string? Email,
    string? Website,
    string? RegistrationNumber,
    string? NabhNumber,
    string? Gstin,
    string? Pan,
    string? Note,
    ImageData? Logo)
{
    public PrintTemplate Template { get; init; } = PrintTemplate.Default;
}

/// <summary>
/// What documents print besides their data: the facility's letterhead and logo, how the hospital prints each kind of
/// document (<see cref="PrintTemplate"/>), doctors' signatures and QR codes. Images are read from the encrypted file
/// store and kept for 10 minutes, since every copy of a document needs them.
/// </summary>
/// <param name="templates">Null (in tests) prints every document with the default template.</param>
public sealed class DocumentAssets(HospitalDbContext db, IFileStore files, IMemoryCache cache, IPrintTemplates? templates = null)
{
    /// <summary>
    /// The largest logo or signature a document embeds. Logos and signatures are small; a bigger image is left out rather
    /// than making every copy heavy: HC360 Desktop prints through WebView2, which takes at most 2 MB of HTML, and images are
    /// embedded as base64 (a third larger). The masters refuse a bigger image when it is chosen.
    /// </summary>
    public const int MaxImageBytes = 300 * 1024;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The facility's letterhead with the template of <paramref name="templateKey"/> (<see cref="PrintTemplateCatalog"/>),
    /// the first facility's when <paramref name="facilityId"/> is null or unknown. The logo is left out when the template
    /// hides it.
    /// </summary>
    public async Task<DocumentLetterhead> LetterheadAsync(Guid? facilityId, string templateKey, CancellationToken cancellationToken)
    {
        var template = await TemplateAsync(templateKey, facilityId, cancellationToken);
        var letterhead = await LetterheadAsync(facilityId, cancellationToken);
        return letterhead with { Logo = template.ShowLogo ? letterhead.Logo : null, Template = template };
    }

    /// <summary>How the facility prints <paramref name="templateKey"/>; the defaults without a template service (in tests).</summary>
    public Task<PrintTemplate> TemplateAsync(string templateKey, Guid? facilityId, CancellationToken cancellationToken) =>
        templates?.GetAsync(templateKey, facilityId, cancellationToken) ?? Task.FromResult(PrintTemplate.Default);

    /// <summary>The facility's letterhead with the default template; the first facility's when <paramref name="facilityId"/> is null or unknown.</summary>
    public async Task<DocumentLetterhead> LetterheadAsync(Guid? facilityId, CancellationToken cancellationToken)
    {
        var facilities = db.Set<Facility>().AsNoTracking();
        var facility = facilityId is { } id ? await facilities.FirstOrDefaultAsync(f => f.Id == id, cancellationToken) : null;
        facility ??= await facilities.OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (facility is null)
        {
            return new DocumentLetterhead("Hospital", null, null, null, null, null, null, null, null, null, null, null);
        }

        return new DocumentLetterhead(
            facility.Name,
            Blank(facility.LegalName),
            Blank(facility.Address.ToString()),
            Blank(facility.Phone),
            Blank(facility.Email),
            Blank(facility.Website),
            Blank(facility.RegistrationNumber),
            Blank(facility.NabhNumber),
            Blank(facility.Gstin),
            Blank(facility.Pan),
            Blank(facility.LetterheadNote),
            await ImageAsync(facility.LogoFileId, cancellationToken));
    }

    /// <summary>An uploaded image (logo, signature) to embed; null when there is none, it is not an image, or it is over 300 KB.</summary>
    public async Task<ImageData?> ImageAsync(Guid? fileId, CancellationToken cancellationToken)
    {
        if (fileId is not { } id)
        {
            return null;
        }

        if (cache.TryGetValue(CacheKey(id), out ImageData? cached))
        {
            return cached;
        }

        var read = await files.ReadAsync(id, cancellationToken);
        var image = read.IsSuccess && read.Value.Content.Length <= MaxImageBytes
            ? ImageData.FromBytes(read.Value.Content, read.Value.File.ContentType)
            : null;
        cache.Set(CacheKey(id), image, CacheFor);
        return image;
    }

    /// <summary>A QR code for <paramref name="text"/> (a verification link), as an SVG image that scales without blurring.</summary>
    public static ImageData QrCode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
        return ImageData.FromBytes(System.Text.Encoding.UTF8.GetBytes(svg), "image/svg+xml")!;
    }

    private static string CacheKey(Guid fileId) => $"document-image:{fileId:N}";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
