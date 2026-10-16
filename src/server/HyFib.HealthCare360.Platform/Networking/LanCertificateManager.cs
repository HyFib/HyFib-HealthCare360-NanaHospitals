using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>The hospital CA as <see cref="LanCertificateManager.ExportCa"/> wrote it: a password-protected PKCS#12 file.</summary>
public sealed record ExportedHospitalCa(byte[] Pkcs12, string Fingerprint, DateTimeOffset ExpiresAt);

/// <summary>What <see cref="LanCertificateManager.ImportCa"/> did.</summary>
/// <param name="Changed">False when this server already had that CA.</param>
/// <param name="PreviousKeptAs">The file name a different CA that was here was kept under.</param>
public sealed record ImportedHospitalCa(string Fingerprint, DateTimeOffset ExpiresAt, bool Changed, string? PreviousKeptAs);

/// <summary>
/// TLS for the hospital LAN/WiFi (DED-04, ADR-005). On first start it creates a private hospital CA and a
/// server certificate covering the host name, <c>host.local</c>, localhost and every current IP address.
/// HC360 Desktop pins the CA fingerprint at enrolment, so the server certificate can be re-issued whenever
/// the WiFi/DHCP address changes without breaking workstations. Keys are wrapped with Data Protection.
/// <para>
/// The CA can be exported, with a password, and imported on a warm standby Server Node or on replacement hardware
/// (<see cref="ExportCa"/>, <see cref="ImportCa"/>; SRS DED-20). That server then issues its own server certificate from the
/// same CA, so workstations and lab agents trust it as they trusted the first, and find it by the CA it announces.
/// </para>
/// </summary>
public sealed class LanCertificateManager : IDisposable
{
    /// <summary>The fewest characters the password of an exported CA may have.</summary>
    public const int MinimumExportPasswordLength = 16;

    private const string CaFile = "hospital-ca.pfx.protected";
    private const string ServerFile = "server.pfx.protected";
    private const string ServerMetaFile = "server.meta.json";
    private const string CaPublicFile = "hc360-hospital-ca.crt";
    private const string ProtectorPurpose = "HC360.LanCertificates.v1";

    /// <summary>
    /// PBKDF2-SHA256 rounds for the key that encrypts an exported CA (AES-256-CBC) and for its integrity check: within what
    /// .NET reads by default (<see cref="Pkcs12LoaderLimits"/>), and far more than the 2000 PKCS#12 files commonly use.
    /// </summary>
    private const int ExportIterations = 200_000;

    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private readonly DataDirectories _directories;
    private readonly PlatformOptions _options;
    private readonly IDataProtector _protector;
    private readonly ILogger<LanCertificateManager> _logger;
    private readonly Lock _gate = new();
    private X509Certificate2? _ca;
    private X509Certificate2? _server;

    public LanCertificateManager(DataDirectories directories, PlatformOptions options, IDataProtectionProvider dataProtection, ILogger<LanCertificateManager> logger)
    {
        _directories = directories;
        _options = options;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    public X509Certificate2 ServerCertificate
    {
        get
        {
            lock (_gate)
            {
                return _server ?? throw new InvalidOperationException("Certificates are not initialised. Call EnsureCertificates at start-up.");
            }
        }
    }

    /// <summary>SHA-256 fingerprint (upper-case hex) of the hospital CA pinned by HC360 Desktop.</summary>
    public string? CaFingerprint { get; private set; }

    public string? CaPem { get; private set; }

    public IReadOnlyList<string> SubjectAlternativeNames { get; private set; } = [];

    public DateTimeOffset? ServerExpiresAt
    {
        get
        {
            lock (_gate)
            {
                return _server is null ? null : new DateTimeOffset(_server.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            }
        }
    }

    /// <summary>
    /// When the hospital CA expires; null with an administrator-supplied certificate, whose issuer HC360 does not hold.
    /// Unlike the server certificate it is never renewed by itself: every device trusts this one CA.
    /// </summary>
    public DateTimeOffset? CaExpiresAt
    {
        get
        {
            lock (_gate)
            {
                return _ca is null ? null : new DateTimeOffset(_ca.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            }
        }
    }

    public void EnsureCertificates()
    {
        Directory.CreateDirectory(_directories.Certificates);

        if (!string.IsNullOrWhiteSpace(_options.Network.CertificatePath))
        {
            lock (_gate)
            {
                _server = X509CertificateLoader.LoadPkcs12FromFile(_options.Network.CertificatePath, _options.Network.CertificatePassword);
            }

            SubjectAlternativeNames = [_server.GetNameInfo(X509NameType.DnsName, false)];
            _logger.LogInformation("Using administrator-supplied TLS certificate {Subject} (expires {Expiry:u})", _server.Subject, _server.NotAfter);
            return;
        }

        if (!_options.Network.GenerateLanCertificate)
        {
            throw new InvalidOperationException("No TLS certificate configured and LAN certificate generation is disabled.");
        }

        lock (_gate)
        {
            _ca = LoadOrCreateCa();
            CaFingerprint = Convert.ToHexString(SHA256.HashData(_ca.RawData));
            CaPem = _ca.ExportCertificatePem();
            File.WriteAllText(Path.Combine(_directories.Certificates, CaPublicFile), CaPem);
            _server = LoadOrCreateServerCertificate(_ca, force: false);
        }
    }

    /// <summary>Re-issues the server certificate if the machine's IP addresses changed or it is near expiry.</summary>
    public bool RefreshIfNeeded()
    {
        if (!string.IsNullOrWhiteSpace(_options.Network.CertificatePath) || _ca is null)
        {
            return false;
        }

        lock (_gate)
        {
            var desired = DesiredNames();
            var needsRenewal = _server is null
                || _server.NotAfter.ToUniversalTime() - DateTime.UtcNow < RenewBefore
                || !desired.SetEquals(SubjectAlternativeNames);
            if (!needsRenewal)
            {
                return false;
            }

            var previous = _server;
            _server = LoadOrCreateServerCertificate(_ca, force: true);
            previous?.Dispose();
            return true;
        }
    }

    private X509Certificate2 LoadOrCreateCa()
    {
        var path = Path.Combine(_directories.Certificates, CaFile);
        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12(_protector.Unprotect(File.ReadAllBytes(path)), null);
        }

        using var key = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=HyFib HealthCare360 Hospital CA ({Environment.MachineName}), O=HyFib HealthCare360, OU={_options.DefaultFacilityName}",
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        using var ca = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        var pfx = ca.Export(X509ContentType.Pkcs12);
        WriteProtected(path, pfx);
        _logger.LogInformation("Created hospital CA certificate (valid until {Expiry:u})", ca.NotAfter);
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    private X509Certificate2 LoadOrCreateServerCertificate(X509Certificate2 ca, bool force)
    {
        var path = Path.Combine(_directories.Certificates, ServerFile);
        var metaPath = Path.Combine(_directories.Certificates, ServerMetaFile);
        var desired = DesiredNames();

        if (!force && File.Exists(path) && File.Exists(metaPath))
        {
            var meta = JsonSerializer.Deserialize<string[]>(File.ReadAllText(metaPath)) ?? [];
            var existing = X509CertificateLoader.LoadPkcs12(_protector.Unprotect(File.ReadAllBytes(path)), null);
            // Issued by another CA when one was imported (ImportCa) while the server ran: workstations would refuse it.
            if (desired.SetEquals(meta) && existing.NotAfter.ToUniversalTime() - DateTime.UtcNow > RenewBefore && IsIssuedBy(existing, ca))
            {
                SubjectAlternativeNames = meta;
                return existing;
            }

            existing.Dispose();
        }

        using var key = RSA.Create(2048);
        var host = Environment.MachineName.ToLowerInvariant();
        var request = new CertificateRequest($"CN={host}, O=HyFib HealthCare360", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in desired)
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }

        request.CertificateExtensions.Add(san.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var now = DateTimeOffset.UtcNow;
        // 397 days keeps Apple/Chrome happy for tablets that trust the hospital CA.
        using var issued = request.Create(ca, now.AddDays(-1), now.AddDays(397), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        var pfx = withKey.Export(X509ContentType.Pkcs12);

        WriteProtected(path, pfx);
        var names = desired.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        File.WriteAllText(metaPath, JsonSerializer.Serialize(names));
        SubjectAlternativeNames = names;
        _logger.LogInformation("Issued LAN server certificate for {Names} (expires {Expiry:u})", string.Join(", ", names), issued.NotAfter);
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    private HashSet<string> DesiredNames()
    {
        var host = Environment.MachineName.ToLowerInvariant();
        var names = new HashSet<string>(StringComparer.Ordinal) { host, host + ".local", "localhost", "127.0.0.1", "::1" };
        foreach (var extra in _options.Network.AdditionalHostNames.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            names.Add(extra.Trim().ToLowerInvariant());
        }

        foreach (var address in CurrentAddresses())
        {
            names.Add(address.ToString());
        }

        return names;
    }

    internal static IEnumerable<IPAddress> CurrentAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
            .Distinct();

    /// <summary>
    /// This server's hospital CA, with its private key, as a PKCS#12 file encrypted with <paramref name="password"/>
    /// (AES-256-CBC, PBKDF2-SHA256), for a warm standby or replacement hardware to import with <see cref="ImportCa"/>.
    /// Whoever has the file and its password can issue certificates workstations trust: it is kept like the backup
    /// recovery key.
    /// </summary>
    /// <exception cref="FileNotFoundException">This server has no hospital CA yet: it makes one when it first starts.</exception>
    /// <exception cref="ArgumentException">The password is shorter than <see cref="MinimumExportPasswordLength"/>.</exception>
    /// <exception cref="CryptographicException">The CA cannot be opened on this machine.</exception>
    public static ExportedHospitalCa ExportCa(DataDirectories directories, IDataProtectionProvider dataProtection, string password)
    {
        if (password.Length < MinimumExportPasswordLength)
        {
            throw new ArgumentException($"Use a password of at least {MinimumExportPasswordLength} characters.", nameof(password));
        }

        var path = Path.Combine(directories.Certificates, CaFile);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("This server has no hospital CA yet: HC360 makes one when it first starts.", path);
        }

        var unprotected = dataProtection.CreateProtector(ProtectorPurpose).Unprotect(File.ReadAllBytes(path));
        try
        {
            using var ca = X509CertificateLoader.LoadPkcs12(unprotected, null, X509KeyStorageFlags.Exportable);
            var file = ca.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, ExportIterations), password);
            return new ExportedHospitalCa(file, Convert.ToHexString(SHA256.HashData(ca.RawData)), new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unprotected);
        }
    }

    /// <summary>
    /// Installs a hospital CA that <see cref="ExportCa"/> exported, protected for this machine, in place of this server's
    /// own. A different CA already here is kept under a new name (<c>hospital-ca.pfx.protected.replaced-&lt;time&gt;</c>),
    /// never deleted, and the server certificate it issued is removed: the server issues a new one from the imported CA when
    /// it next starts. Run with the HC360 service stopped, or restart it after.
    /// </summary>
    /// <exception cref="CryptographicException">The password does not open the file, or it is damaged.</exception>
    /// <exception cref="InvalidDataException">The file does not hold a CA certificate with its private key, or it has expired.</exception>
    public static ImportedHospitalCa ImportCa(DataDirectories directories, IDataProtectionProvider dataProtection, byte[] pkcs12, string password, DateTimeOffset now)
    {
        using var ca = X509CertificateLoader.LoadPkcs12(pkcs12, password, X509KeyStorageFlags.Exportable);
        if (!ca.HasPrivateKey)
        {
            throw new InvalidDataException("The file holds a certificate without its private key: export the CA with 'certificates export-ca'.");
        }

        if (ca.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true }
            || !ca.SubjectName.RawData.AsSpan().SequenceEqual(ca.IssuerName.RawData))
        {
            throw new InvalidDataException($"The file holds {ca.Subject}, which is not a hospital CA: export the CA with 'certificates export-ca'.");
        }

        var expires = new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        if (expires <= now)
        {
            throw new InvalidDataException($"The CA in the file expired on {expires:dd MMM yyyy}.");
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(ca.RawData));
        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        Directory.CreateDirectory(directories.Certificates);
        var path = Path.Combine(directories.Certificates, CaFile);
        var publicPath = Path.Combine(directories.Certificates, CaPublicFile);
        string? keptAs = null;
        if (File.Exists(path))
        {
            if (SameCertificate(path, protector, ca))
            {
                File.WriteAllText(publicPath, ca.ExportCertificatePem());
                return new ImportedHospitalCa(fingerprint, expires, Changed: false, PreviousKeptAs: null);
            }

            keptAs = $"{path}.replaced-{now.UtcDateTime:yyyyMMdd-HHmmss}";
        }

        // The new CA is on disk before the old one is moved aside, and the old one goes back if the new one cannot take its
        // place, so the server never starts without a CA and makes a new one that no workstation trusts.
        var unprotected = ca.Export(X509ContentType.Pkcs12);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, protector.Protect(unprotected));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unprotected);
        }

        try
        {
            if (keptAs is not null)
            {
                File.Move(path, keptAs, overwrite: false);
            }

            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            if (keptAs is not null && File.Exists(keptAs) && !File.Exists(path))
            {
                File.Move(keptAs, path);
            }

            File.Delete(temp);
            throw;
        }

        File.Delete(Path.Combine(directories.Certificates, ServerFile));
        File.Delete(Path.Combine(directories.Certificates, ServerMetaFile));
        File.WriteAllText(publicPath, ca.ExportCertificatePem());
        return new ImportedHospitalCa(fingerprint, expires, Changed: true, keptAs is null ? null : Path.GetFileName(keptAs));
    }

    /// <summary>Whether the CA at <paramref name="path"/> is <paramref name="ca"/>; one this machine cannot open is not.</summary>
    private static bool SameCertificate(string path, IDataProtector protector, X509Certificate2 ca)
    {
        try
        {
            using var existing = X509CertificateLoader.LoadPkcs12(protector.Unprotect(File.ReadAllBytes(path)), null);
            return existing.RawData.AsSpan().SequenceEqual(ca.RawData);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="certificate"/> chains to <paramref name="ca"/>, its signature checked.</summary>
    private static bool IsIssuedBy(X509Certificate2 certificate, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        // The private CA publishes no revocation lists.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate) && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData);
    }

    private void WriteProtected(string path, byte[] data)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, _protector.Protect(data));
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _server?.Dispose();
            _ca?.Dispose();
        }
    }
}
