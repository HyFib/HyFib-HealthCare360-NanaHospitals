using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// The server's HTTPS, as every listener serves it: TLS 1.2 or 1.3, and the server certificate alone. Nothing is sent with
/// it: on Windows the operating system's TLS sends no certificate the devices would need, so a CA rollover never depends on
/// one (<see cref="LanCertificateManager.PrepareCa"/>, <see cref="LanCertificateManager.SwitchCa"/>).
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
    }
}
