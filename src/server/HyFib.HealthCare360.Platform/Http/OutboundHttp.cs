using System.Security.Authentication;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>Connections the server opens to outside services: SMS, WhatsApp, payments, AI, ICD, PACS and the like.</summary>
public static class OutboundHttp
{
    /// <summary>
    /// A redirect is an answer, never followed: it could lead the request, credentials and all, to another host. TLS 1.2
    /// is the floor, whatever older Windows versions still allow by default.
    /// </summary>
    public static SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Long-lived clients still follow the provider's DNS changes.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
#pragma warning disable CA5398 // Avoid hardcoded SslProtocols values: the floor is deliberate, as for the server's own listener.
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
#pragma warning restore CA5398
        return handler;
    }
}
