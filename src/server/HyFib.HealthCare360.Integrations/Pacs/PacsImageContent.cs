using System.Globalization;
using System.Net.Http.Headers;

namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>
/// What HC360 passes on from a PACS image response: JPEG or PNG only, declared as such and starting with that format's
/// signature, within a size limit. Anything else (an HTML error page, a multipart body, DICOM itself) is refused, so the
/// staff app can only ever be handed a picture.
/// </summary>
internal static class PacsImageContent
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";

    /// <summary>Rendered images: a full-resolution PNG of a large radiograph stays well under this.</summary>
    public const int MaxRenderedBytes = 16 * 1024 * 1024;

    public const int MaxThumbnailBytes = 2 * 1024 * 1024;

    /// <summary>First buffer for an image of undeclared size; it doubles as the image arrives.</summary>
    private const int InitialBufferBytes = 256 * 1024;

    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static int LimitFor(PacsImageKind kind) => kind == PacsImageKind.Thumbnail ? MaxThumbnailBytes : MaxRenderedBytes;

    /// <summary>The image format of the bytes by their signature (JPEG or PNG), or null.</summary>
    public static string? Detect(ReadOnlySpan<byte> content) =>
        content.StartsWith(JpegSignature) ? Jpeg : content.StartsWith(PngSignature) ? Png : null;

    /// <summary>True for a declared media type HC360 accepts from a PACS (image/jpeg, the common misspelling image/jpg, image/png).</summary>
    public static bool IsAcceptedMediaType(string? mediaType) =>
        mediaType?.Trim().ToLowerInvariant() is Jpeg or "image/jpg" or Png;

    /// <exception cref="IntegrationException">The answer is not a JPEG or PNG image, or is larger than <paramref name="limit"/>.</exception>
    public static async Task<(ReadOnlyMemory<byte> Content, string ContentType)> ReadAsync(
        HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        var declared = response.Content.Headers.ContentType?.MediaType;
        if (!IsAcceptedMediaType(declared))
        {
            throw new IntegrationException($"The PACS sent \"{Describe(response.Content.Headers.ContentType)}\" instead of a JPEG or PNG image.");
        }

        if (response.Content.Headers.ContentLength > limit)
        {
            throw new IntegrationException(TooLarge(limit));
        }

        var content = await ReadLimitedAsync(response.Content, limit, cancellationToken);
        return Detect(content.Span) is { } type
            ? (content, type)
            : throw new IntegrationException("The PACS sent an image HC360 could not recognise as JPEG or PNG.");
    }

    /// <summary>
    /// Reads the image into one buffer, never more than <paramref name="limit"/> bytes: of the declared size when the
    /// PACS declares one, otherwise grown as the image arrives. The filled part is returned as it is, not copied again.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>> ReadLimitedAsync(
        HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        if (content.Headers.ContentLength is { } length)
        {
            var exact = new byte[length];
            try
            {
                await stream.ReadExactlyAsync(exact, cancellationToken);
            }
            catch (EndOfStreamException)
            {
                throw new IntegrationException("The PACS ended the image before its declared length.");
            }

            return await stream.ReadAsync(new byte[1], cancellationToken) == 0
                ? exact
                : throw new IntegrationException("The PACS sent more image data than it declared.");
        }

        var buffer = new byte[Math.Min(limit, InitialBufferBytes)];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                if (buffer.Length == limit)
                {
                    // Full at the limit: the image fits only if nothing more comes.
                    if (await stream.ReadAsync(new byte[1], cancellationToken) > 0)
                    {
                        throw new IntegrationException(TooLarge(limit));
                    }

                    break;
                }

                Array.Resize(ref buffer, (int)Math.Min(buffer.Length * 2L, limit));
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return buffer.AsMemory(0, filled);
    }

    private static string TooLarge(int limit) =>
        string.Create(CultureInfo.InvariantCulture, $"The PACS sent an image larger than {limit / (1024 * 1024)} MB, which HC360 does not pass on.");

    /// <summary>The declared type for a message, cut short and without anything but plain characters.</summary>
    private static string Describe(MediaTypeHeaderValue? type) =>
        type?.MediaType is { } text
            ? new string(text.Where(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '+' or '-' or '.').Take(60).ToArray())
            : "no content type";
}
