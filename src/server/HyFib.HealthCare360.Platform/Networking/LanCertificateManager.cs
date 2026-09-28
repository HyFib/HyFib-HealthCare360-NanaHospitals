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

/// <summary>
/// TLS for the hospital LAN/WiFi (DED-04, ADR-005). On first start it creates a private hospital CA and a
/// server certificate covering the host name, <c>host.local</c>, localhost and every current IP address.
/// HC360 Desktop pins the CA fingerprint at enrolment, so the server certificate can be re-issued whenever
/// the WiFi/DHCP address changes without breaking workstations. Keys are wrapped with Data Protection.
/// </summary>
public sealed class LanCertificateManager : IDisposable
{
    private const string CaFile = "hospital-ca.pfx.protected";
    private const string ServerFile = "server.pfx.protected";
    private const string ServerMetaFile = "server.meta.json";
    private const string CaPublicFile = "hc360-hospital-ca.crt";
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
        _protector = dataProtection.CreateProtector("HC360.LanCertificates.v1");
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
            if (desired.SetEquals(meta) && existing.NotAfter.ToUniversalTime() - DateTime.UtcNow > RenewBefore)
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
