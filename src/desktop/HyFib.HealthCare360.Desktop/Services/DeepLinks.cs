namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// <c>hc360://</c> links from other applications (e.g. a lab analyser console or an email) open a screen of the
/// staff app: <c>hc360://patients/{id}</c> or <c>hc360://open?path=/front-office/queue</c>. Only app-relative paths
/// are accepted, so a link can never send the embedded browser to another site.
/// </summary>
internal static class DeepLinks
{
    public const string Scheme = "hc360";

    public static string? Resolve(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (!Uri.TryCreate(arg, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? path;
            if (string.Equals(uri.Host, "open", StringComparison.OrdinalIgnoreCase))
            {
                path = QueryValue(uri.Query, "path");
            }
            else
            {
                path = "/" + uri.Host + uri.AbsolutePath.TrimEnd('/');
                if (uri.Query.Length > 1)
                {
                    path += uri.Query;
                }
            }

            if (path is not null && IsSafePath(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Relative, same-app path: starts with a single "/", no scheme, no protocol-relative or backslash tricks.</summary>
    public static bool IsSafePath(string path) =>
        path.StartsWith('/')
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.StartsWith("/\\", StringComparison.Ordinal)
        && !path.Contains("://", StringComparison.Ordinal)
        && path.All(c => !char.IsControl(c));

    private static string? QueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }
}
