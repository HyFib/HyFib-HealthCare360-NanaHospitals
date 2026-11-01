using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.WindowsInterop.Trust;

/// <summary>
/// Where Windows finds the certificates the server sends with its own. On Windows the TLS handshake runs in the operating
/// system (Schannel), which builds the server's chain from the computer's certificate stores, not from what the TLS context
/// was given, and sends it only when it ends at a root this computer trusts; otherwise the server's certificate goes alone.
/// In a rollover the server sends the certificate the previous CA signed for the new one, and that chain ends at the
/// previous CA, which a Server Node never trusted. So HC360 puts that certificate in the computer's Intermediate
/// Certification Authorities store, and the previous CA among its Trusted Root Certification Authorities, trusted for TLS
/// server authentication only ("Enable only the following purposes" in the Certificates console), and takes both out with
/// the rollover. The 'certificates' commands do it, as they run as an administrator; the running server, which may not
/// write the computer's stores, finds them there. Elsewhere TLS sends what the context was given, and this does nothing.
/// </summary>
public static class WindowsTlsChain
{
    /// <summary>TLS server authentication (id-kp-serverAuth): the one purpose a previous CA is trusted for on the Server Node.</summary>
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>CERT_ENHKEY_USAGE_PROP_ID: the purposes a certificate in a store is trusted for, whatever it says itself.</summary>
    private const uint EnhancedKeyUsageProperty = 9;

    /// <summary>
    /// Puts copies of <paramref name="certificates"/> (without any private key) where Schannel finds them: a CA certificate
    /// that signed itself among the trusted roots, for server authentication only, any other among the intermediate CAs.
    /// False when one is not there and cannot be put there: Windows then sends the server's certificate without them.
    /// </summary>
    public static bool Publish(IEnumerable<X509Certificate2> certificates)
    {
        var list = certificates.ToList();
        if (!OperatingSystem.IsWindows() || list.Count == 0)
        {
            return true;
        }

        var placed = true;
        foreach (var certificate in list)
        {
            placed &= Place(certificate, StoreFor(certificate));
        }

        return placed;
    }

    /// <summary>Takes <paramref name="certificates"/> out of the computer's stores again, and out of this account's, where HC360 may.</summary>
    public static void Withdraw(IEnumerable<X509Certificate2> certificates)
    {
        var list = certificates.ToList();
        if (!OperatingSystem.IsWindows() || list.Count == 0)
        {
            return;
        }

        foreach (var certificate in list)
        {
            TryRemove(certificate, StoreFor(certificate), StoreLocation.LocalMachine);
            // An earlier version put them in this account's store when it could not write the computer's.
            TryRemove(certificate, StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        }
    }

    /// <summary>Whether the computer's store <paramref name="certificate"/> belongs in holds it, for the tests run on Windows.</summary>
    public static bool Holds(X509Certificate2 certificate) =>
        OperatingSystem.IsWindows() && InStore(certificate, StoreFor(certificate), StoreLocation.LocalMachine);

    /// <summary>
    /// Whether <paramref name="certificate"/> is issued in its own name and names its own key as its issuer's (or no key), as a
    /// self-signed CA does. The certificate the previous CA signs for a CA in a rollover has the CA's name too, but names the
    /// previous CA's key.
    /// </summary>
    public static bool NamesItselfAsIssuer(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var issuerKey = certificate.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
        var ownKey = certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifierBytes;
        return certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
            && (issuerKey is not { } named || ownKey is not { } own || named.Span.SequenceEqual(own.Span));
    }

    private static StoreName StoreFor(X509Certificate2 certificate) =>
        NamesItselfAsIssuer(certificate) ? StoreName.Root : StoreName.CertificateAuthority;

    private static bool Place(X509Certificate2 certificate, StoreName name)
    {
        // Put there by an administrator's 'certificates' command, as the running server finds it.
        if (InStore(certificate, name, StoreLocation.LocalMachine))
        {
            return true;
        }

        try
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            using var copy = X509CertificateLoader.LoadCertificate(certificate.RawData);
            // Copied into the store with the certificate: this computer trusts the previous CA for nothing but TLS servers.
            if (name == StoreName.Root && !TrustOnlyForServerAuthentication(copy))
            {
                return false;
            }

            store.Add(copy);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static bool InStore(X509Certificate2 certificate, StoreName name, StoreLocation location)
    {
        try
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Any(held => held.RawData.AsSpan().SequenceEqual(certificate.RawData));
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or SecurityException)
        {
            // A store this account cannot read holds nothing it put there.
            return false;
        }
    }

    private static void TryRemove(X509Certificate2 certificate, StoreName name, StoreLocation location)
    {
        try
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
            using var withoutKey = X509CertificateLoader.LoadCertificate(certificate.RawData);
            store.Remove(withoutKey);
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or SecurityException)
        {
            // Not this account's to change: an administrator's 'certificates' command takes it out.
        }
    }

    /// <summary>Sets on <paramref name="certificate"/> the purposes it is trusted for: server authentication only.</summary>
    private static bool TrustOnlyForServerAuthentication(X509Certificate2 certificate)
    {
        var usage = new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ServerAuthentication) }, critical: false).RawData;
        var pinned = GCHandle.Alloc(usage, GCHandleType.Pinned);
        try
        {
            var blob = new NativeMethods.CryptDataBlob { Size = (uint)usage.Length, Data = pinned.AddrOfPinnedObject() };
            return NativeMethods.CertSetCertificateContextProperty(certificate.Handle, EnhancedKeyUsageProperty, 0, ref blob);
        }
        finally
        {
            pinned.Free();
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct CryptDataBlob
        {
            public uint Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CertSetCertificateContextProperty(IntPtr certificateContext, uint propertyId, uint flags, ref CryptDataBlob data);
    }
}
