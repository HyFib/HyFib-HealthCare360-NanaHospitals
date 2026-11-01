using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// The server's HTTPS, as every listener serves it: TLS 1.2 or 1.3, and the server certificate with the chain the hospital
/// CA's devices need. After a rollover the chain carries the certificate the previous CA signed for the current one
/// (<see cref="LanCertificateManager.RollCa"/>), so a device that pins the previous CA still trusts the server.
/// </summary>
public static class HospitalHttps
{
    public static void Configure(HttpsConnectionAdapterOptions https, LanCertificateManager certificates)
    {
        // TLS 1.2 is the floor on purpose: older Windows versions the server may run on still enable TLS 1.0/1.1 by default.
#pragma warning disable CA5398 // Avoid hardcoded SslProtocols values
        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
#pragma warning restore CA5398

        // The certificate as it is now: it is issued again on a network change and before it expires, while the server runs.
        https.ServerCertificateSelector = (_, _) => certificates.ServerCertificate;

        // A selected certificate goes out alone, so the chain is given instead, where the server made the certificate. Kestrel
        // has set everything else (protocols, ALPN) by now; an administrator-supplied certificate is served as Kestrel finds it.
        https.OnAuthenticate = (_, tls) =>
        {
            if (certificates.ServerCertificateContext is { } context)
            {
                tls.ServerCertificateSelectionCallback = null;
                tls.ServerCertificate = null;
                tls.ServerCertificateContext = context;
            }
        };
    }
}
