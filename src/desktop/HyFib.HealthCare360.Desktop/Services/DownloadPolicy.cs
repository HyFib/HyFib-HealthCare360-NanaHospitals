using System.IO;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// What HC360 Desktop lets a page save to disk: files from the hospital server, or copies the staff app builds in the
/// page, and only as the document types HC360 produces. A program, script or shortcut is refused before it reaches the
/// disk, whatever a page or a stored file's name says.
/// </summary>
internal static class DownloadPolicy
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Reports, exports and accounts files.
        ".pdf", ".csv", ".xlsx", ".xml", ".json", ".txt",
        // Stored documents: scans, images and voice recordings.
        ".png", ".jpg", ".jpeg", ".webp", ".dcm", ".webm", ".ogg", ".mp3", ".wav", ".m4a",
        // The hospital CA certificate, for installing on tablets.
        ".crt",
    };

    public static bool IsAllowed(string sourceUri, string resultFilePath, Uri server) =>
        FromServer(sourceUri, server) && Extensions.Contains(Path.GetExtension(resultFilePath));

    private static bool FromServer(string sourceUri, Uri server)
    {
        // A data: URL is built by the page itself, and pages come only from the server (see OnNavigationStarting).
        if (sourceUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A blob: URL names the origin of the page that made it.
        var location = sourceUri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ? sourceUri[5..] : sourceUri;
        return Uri.TryCreate(location, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && Uri.Compare(uri, server, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }
}
