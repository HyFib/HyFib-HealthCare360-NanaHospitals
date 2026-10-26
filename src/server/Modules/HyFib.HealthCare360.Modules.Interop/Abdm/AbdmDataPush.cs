using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// Posts pages of encrypted documents to an HIU's data-push URL, as NHA's wrapper does: plain JSON, no gateway session
/// (the documents are encrypted for the HIU alone). The URL comes from the gateway's signed request; it is still checked
/// (<see cref="IsAllowed"/>) so that a request cannot point the hospital's server at a machine on its own network.
/// </summary>
internal sealed class AbdmDataPush(HttpClient http, ILogger<AbdmDataPush> log)
{
    /// <summary>A page of a few documents with their PDFs can be several megabytes.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>True when the HIU accepted the page (any 2xx).</summary>
    public async Task<bool> PushAsync(Uri url, AbdmGatewayMessages.DataPush page, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(page, AbdmGatewayMessages.Json), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(url, content, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("The HIU refused page {Page} of transfer {Transaction}: HTTP {Status}", page.PageNumber, page.TransactionId, (int)response.StatusCode);
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "The HIU's data-push URL could not be reached for transfer {Transaction}", page.TransactionId);
            return false;
        }
    }

    /// <summary>
    /// An https URL with no user name, whose host is not this machine or an address on a private, link-local or
    /// carrier network. A host name is not resolved here: the HIU's address comes from the consent manager's signed request.
    /// </summary>
    public static bool IsAllowed(string? value, out Uri? url)
    {
        url = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo)
            || parsed.IsLoopback || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var address) && !IsPublic(address))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast);
        }

        var b = address.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] == 0 || b[0] >= 224);
    }
}
