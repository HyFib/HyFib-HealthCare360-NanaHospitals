using System.Text.Json;

namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

/// <summary>An RTCIceServer entry handed to the browser (STUN, or TURN with credentials).</summary>
public sealed record IceServer(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

/// <summary>
/// Reads the <c>telehealth.iceServers</c> setting (TEL-06). Patients outside the hospital network usually need a TURN
/// relay; administrators add one without a code change. The parser accepts RTCIceServer-shaped objects (with
/// <c>urls</c> as a string or an array) or plain URL strings, skips anything that is not a stun:/turn: URL, and falls
/// back to a public STUN server so a bad setting never breaks calls completely.
/// </summary>
public static class IceServers
{
    public const string DefaultStun = "stun:stun.l.google.com:19302";

    public static IReadOnlyList<IceServer> Default { get; } = [new([DefaultStun])];

    public static IReadOnlyList<IceServer> Parse(JsonElement? setting)
    {
        if (setting is not { ValueKind: JsonValueKind.Array } array)
        {
            return Default;
        }

        var servers = new List<IceServer>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && IsIceUrl(item.GetString()))
            {
                servers.Add(new IceServer([item.GetString()!]));
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var urls = ReadUrls(item);
                if (urls.Count > 0)
                {
                    servers.Add(new IceServer(urls, ReadString(item, "username"), ReadString(item, "credential")));
                }
            }
        }

        return servers.Count == 0 ? Default : servers;
    }

    public static bool IsIceUrl(string? url) =>
        url is { Length: > 5 and <= 512 }
        && (url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("stuns:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase));

    private static List<string> ReadUrls(JsonElement item)
    {
        var urls = new List<string>();
        foreach (var property in item.EnumerateObject())
        {
            if (!property.NameEquals("urls") && !property.Name.Equals("urls", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String && IsIceUrl(property.Value.GetString()))
            {
                urls.Add(property.Value.GetString()!);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                urls.AddRange(property.Value.EnumerateArray()
                    .Where(u => u.ValueKind == JsonValueKind.String && IsIceUrl(u.GetString()))
                    .Select(u => u.GetString()!));
            }
        }

        return urls;
    }

    private static string? ReadString(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
