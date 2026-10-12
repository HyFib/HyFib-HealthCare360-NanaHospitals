using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.LoadTest;

/// <summary>
/// Connections to the server as HC360 Desktop makes them: the server's certificate must chain to the hospital CA and
/// name the host, and nothing else is trusted (src/desktop/…/ServerTrust.cs). Without a CA file, the machine's own trust
/// decides, for a server with a certificate from a public CA.
/// </summary>
internal static class Connections
{
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

    public static Func<bool, HttpMessageHandler> Factory(string? caCertificatePath, int connections)
    {
        var ca = caCertificatePath is null ? null : X509Certificate2.CreateFromPemFile(caCertificatePath);
        return decompress =>
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = decompress ? DecompressionMethods.Brotli | DecompressionMethods.GZip : DecompressionMethods.None,
                // A connection of its own for each user, as each workstation has.
                MaxConnectionsPerServer = Math.Max(connections, 2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            if (ca is not null)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    certificate is not null && IsTrusted(ca, new X509Certificate2(certificate), chain, errors);
            }

            return handler;
        };
    }

    /// <summary>
    /// Whether <paramref name="certificate"/> names the host (Windows' own name check, in <paramref name="errors"/>), is for
    /// a TLS server and chains to exactly <paramref name="ca"/>, through the intermediates the server sent.
    /// </summary>
    internal static bool IsTrusted(X509Certificate2 ca, X509Certificate2 certificate, X509Chain? handshake, SslPolicyErrors errors)
    {
        using (certificate)
        {
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            if (handshake is not null)
            {
                chain.ChainPolicy.ExtraStore.AddRange(handshake.ChainPolicy.ExtraStore);
            }

            chain.ChainPolicy.ApplicationPolicy.Add(ServerAuthentication);
            // A private CA on a hospital LAN publishes no revocation lists.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate) && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData);
        }
    }
}
