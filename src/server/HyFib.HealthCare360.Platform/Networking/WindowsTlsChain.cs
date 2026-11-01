using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// Where Windows finds the certificates the server sends with its own. On Windows the TLS handshake runs in the operating
/// system (Schannel), which sends only the certificates it finds in the computer's certificate stores, not those the TLS
/// context was given. .NET puts a context's intermediates there for it, but not one that names itself as its issuer, as the
/// certificate the previous CA signs for the new one in a rollover does (<see cref="LanCertificateManager.RollCa"/>): it is
/// taken for a root and left out, and the server sends its own certificate alone. So HC360 puts that certificate, and the
/// previous CA it leads to, in the Intermediate Certification Authorities store itself: the computer's when it may (the
/// 'certificates' commands run as an administrator), else its own account's. Never among the trusted roots: the server
/// trusts nothing more for them. Elsewhere TLS sends what the context was given, and this does nothing.
/// </summary>
public static class WindowsTlsChain
{
    /// <summary>
    /// Puts copies of <paramref name="certificates"/> (without any private key) where Schannel finds them. False when no store
    /// would take them: Windows then sends the server's certificate without them.
    /// </summary>
    public static bool Publish(IEnumerable<X509Certificate2> certificates)
    {
        var list = certificates.ToList();
        if (!OperatingSystem.IsWindows() || list.Count == 0)
        {
            return true;
        }

        return TryAdd(StoreLocation.LocalMachine, list) || TryAdd(StoreLocation.CurrentUser, list);
    }

    /// <summary>Takes <paramref name="certificates"/> out of both stores again, from each where HC360 may.</summary>
    public static void Withdraw(IEnumerable<X509Certificate2> certificates)
    {
        var list = certificates.ToList();
        if (!OperatingSystem.IsWindows() || list.Count == 0)
        {
            return;
        }

        TryRemove(StoreLocation.LocalMachine, list);
        TryRemove(StoreLocation.CurrentUser, list);
    }

    /// <summary>Whether <paramref name="certificate"/> is in either store, for the tests run on Windows.</summary>
    public static bool Holds(X509Certificate2 certificate)
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.CertificateAuthority, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                if (store.Certificates.Any(held => held.RawData.AsSpan().SequenceEqual(certificate.RawData)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or SecurityException)
            {
                // A store this account cannot read holds nothing it put there.
            }
        }

        return false;
    }

    private static bool TryAdd(StoreLocation location, IReadOnlyList<X509Certificate2> certificates)
    {
        try
        {
            using var store = new X509Store(StoreName.CertificateAuthority, location);
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in certificates)
            {
                using var withoutKey = X509CertificateLoader.LoadCertificate(certificate.RawData);
                store.Add(withoutKey);
            }

            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static void TryRemove(StoreLocation location, IReadOnlyList<X509Certificate2> certificates)
    {
        try
        {
            using var store = new X509Store(StoreName.CertificateAuthority, location);
            store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
            foreach (var certificate in certificates)
            {
                using var withoutKey = X509CertificateLoader.LoadCertificate(certificate.RawData);
                store.Remove(withoutKey);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or SecurityException)
        {
            // Not this account's to change: an administrator's 'certificates' command takes it out.
        }
    }
}
