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
/// The hospital CA as <see cref="LanCertificateManager.ExportCa"/> wrote it: a password-protected PKCS#12 file. After a switch
/// (<see cref="LanCertificateManager.SwitchCa"/>) it also holds the CA this one replaced and the certificate that CA signed for
/// this one, until <paramref name="PreviousCaExpiresAt"/>: a standby given it hands them out as the primary does. A next CA
/// prepared here (<paramref name="NextCaLeftHere"/>) is not in it: it is exported once the switch has made it this server's.
/// </summary>
public sealed record ExportedHospitalCa(
    byte[] Pkcs12, string Fingerprint, DateTimeOffset ExpiresAt, DateTimeOffset? PreviousCaExpiresAt = null, bool NextCaLeftHere = false);

/// <summary>A hospital CA <see cref="LanCertificateManager.NewCa"/> made, and the one it replaced.</summary>
/// <param name="PreviousKeptAs">The file name the CA it replaced is kept under; null when there was none.</param>
/// <param name="PreviousFingerprint">The fingerprint of the CA it replaced; null when there was none, or it could not be opened.</param>
/// <param name="PreviousExpiresAt">When the CA it replaced expires.</param>
public sealed record NewHospitalCa(string Fingerprint, DateTimeOffset ExpiresAt, string? PreviousKeptAs, string? PreviousFingerprint, DateTimeOffset? PreviousExpiresAt);

/// <summary>What <see cref="LanCertificateManager.ImportCa"/> did.</summary>
/// <param name="Changed">False when this server already had that CA.</param>
/// <param name="PreviousKeptAs">The file name a different CA that was here was kept under.</param>
/// <param name="PreviousCaExpiresAt">After a switch on the server it came from, until when the CA it replaced is handed out here too.</param>
public sealed record ImportedHospitalCa(
    string Fingerprint, DateTimeOffset ExpiresAt, bool Changed, string? PreviousKeptAs, DateTimeOffset? PreviousCaExpiresAt = null);

/// <summary>
/// What <see cref="LanCertificateManager.PrepareCa"/> made: the next CA (this one's name, a new key, valid for ten years), and
/// the certificate this CA signed for its key, valid until this CA expires. The server keeps serving with this CA
/// (<paramref name="CurrentFingerprint"/>) until the switch.
/// </summary>
public sealed record PreparedHospitalCa(string Fingerprint, DateTimeOffset ExpiresAt, string CurrentFingerprint, DateTimeOffset CurrentExpiresAt);

/// <summary>
/// What <see cref="LanCertificateManager.SwitchCa"/> did: the prepared CA is now this server's, and the one it replaced
/// (kept under <paramref name="PreviousKeptAs"/>) is handed out until it expires, for devices that keep it as a second pin.
/// </summary>
public sealed record SwitchedHospitalCa(
    string Fingerprint, DateTimeOffset ExpiresAt, string PreviousFingerprint, DateTimeOffset PreviousExpiresAt, string? PreviousKeptAs);

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
/// <para>
/// The CA is never renewed by itself. It is rolled over in two steps, so no device is cut off. First the next CA is
/// prepared (<see cref="PrepareCa"/>): the old one's name, a new key, and a certificate the old CA signs for that key. The
/// server keeps serving with the old CA, and hands both out, so devices take the next CA as a second pin on the old CA's
/// word. Then, once every device holds it, the server switches to it (<see cref="SwitchCa"/>). The server only ever sends
/// its own certificate: on Windows its TLS sends nothing else that devices could need. A new CA (<see cref="NewCa"/>), for
/// a key that may have been seen, means setting every device up again (docs/deployment/CA-ROLLOVER.md). A server whose CA
/// has expired does not start, and says what to do.
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

    /// <summary>The next CA, prepared, with its private key (protected for this machine), until the switch.</summary>
    private const string NextCaFile = "hospital-ca-next.pfx.protected";

    /// <summary>
    /// The certificate the older of two CAs signed for the newer one's key (public; PEM): this CA's for the next one while it
    /// is prepared, and after the switch the previous CA's for this one.
    /// </summary>
    private const string CrossFile = "hospital-ca-cross.crt";

    /// <summary>After a switch: the CA this one replaced (public; PEM), until it expires.</summary>
    private const string PreviousCaPublicFile = "hc360-hospital-ca-previous.crt";
    private const string ProtectorPurpose = "HC360.LanCertificates.v1";

    /// <summary>
    /// PBKDF2-SHA256 rounds for the key that encrypts an exported CA (AES-256-CBC) and for its integrity check: within what
    /// .NET reads by default (<see cref="Pkcs12LoaderLimits"/>), and far more than the 2000 PKCS#12 files commonly use.
    /// </summary>
    private const int ExportIterations = 200_000;

    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    /// <summary>
    /// How often a running server checks its certificate's expiry: it is issued again <see cref="RenewBefore"/> before it
    /// expires however long the server has been up, network change or not.
    /// </summary>
    private static readonly TimeSpan ExpiryCheck = TimeSpan.FromHours(6);

    private readonly DataDirectories _directories;
    private readonly PlatformOptions _options;
    private readonly IDataProtector _protector;
    private readonly ILogger<LanCertificateManager> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private X509Certificate2? _ca;
    private X509Certificate2? _server;
    private ITimer? _expiryCheck;

    public LanCertificateManager(
        DataDirectories directories, PlatformOptions options, IDataProtectionProvider dataProtection, ILogger<LanCertificateManager> logger,
        TimeProvider? time = null)
    {
        _directories = directories;
        _options = options;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _logger = logger;
        _time = time ?? TimeProvider.System;
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

    /// <summary>
    /// The next CA, prepared (<see cref="PrepareCa"/>) and not yet switched to (upper-case hex SHA-256); null otherwise. The
    /// server hands it out with <see cref="NextCaCrossPem"/>, so devices take it as a second pin before the switch.
    /// </summary>
    public string? NextCaFingerprint { get; private set; }

    /// <summary>The next CA's certificate (PEM) while <see cref="NextCaFingerprint"/> is set.</summary>
    public string? NextCaPem { get; private set; }

    /// <summary>The certificate this CA signed for the next CA's key (PEM): its word that the next CA succeeds it.</summary>
    public string? NextCaCrossPem { get; private set; }

    /// <summary>After a switch (<see cref="SwitchCa"/>), the CA this one replaced, until it expires; null otherwise.</summary>
    public string? PreviousCaFingerprint { get; private set; }

    /// <summary>When <see cref="PreviousCaFingerprint"/> expires: devices keep it as a second pin until then.</summary>
    public DateTimeOffset? PreviousCaExpiresAt { get; private set; }

    /// <summary>The previous CA's certificate (PEM) while <see cref="PreviousCaFingerprint"/> is set.</summary>
    public string? PreviousCaPem { get; private set; }

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
            var ca = LoadOrCreateCa();
            var caExpires = new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            if (caExpires <= _time.GetUtcNow())
            {
                // Nothing can be issued from it, and no device would trust what was: say so, rather than fail to issue.
                var next = ReadNext(_directories, _protector, ca, _time.GetUtcNow(), switching: true);
                ca.Dispose();
                next?.Ca.Dispose();
                next?.Cross.Dispose();
                throw new InvalidOperationException(
                    $"HC360 cannot start: the hospital CA expired on {caExpires:dd MMM yyyy}, so no workstation, tablet, HC360 Desktop or lab "
                    + "agent would trust a certificate it issued. "
                    + (next is null
                        ? "Make a new CA with 'HyFib.HealthCare360.Server.exe certificates new-ca' and set every device up again, as "
                            + "docs/deployment/CA-ROLLOVER.md says, or give the server a certificate of the hospital's own (Network:CertificatePath)."
                        : "The next CA is prepared: switch to it with 'HyFib.HealthCare360.Server.exe certificates switch-ca --confirm', and every "
                            + "device that holds it trusts the server again, as docs/deployment/CA-ROLLOVER.md says."));
            }

            _ca = ca;
            CaFingerprint = Convert.ToHexString(SHA256.HashData(_ca.RawData));
            CaPem = _ca.ExportCertificatePem();
            File.WriteAllText(Path.Combine(_directories.Certificates, CaPublicFile), CaPem);
            _server = LoadOrCreateServerCertificate(_ca, force: false);
            LoadRollover();
            _expiryCheck ??= _time.CreateTimer(_ => RenewBeforeExpiry(), null, ExpiryCheck, ExpiryCheck);
        }
    }

    /// <summary>Re-issues the server certificate if the machine's IP addresses changed or it is near expiry.</summary>
    public bool RefreshIfNeeded() => Reissue(checkNames: true);

    /// <summary>
    /// The running server's own check (<see cref="ExpiryCheck"/>). Addresses are left to <see cref="RefreshIfNeeded"/> on a
    /// network change: rotating IPv6 temporary addresses would otherwise re-issue the certificate every day. A failure is
    /// logged and tried again at the next check; until then System health warns that the certificate is expiring.
    /// </summary>
    private void RenewBeforeExpiry()
    {
        try
        {
            if (Reissue(checkNames: false))
            {
                _logger.LogInformation("LAN server certificate issued again before it expires");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The LAN server certificate could not be issued again; it is tried again in {Hours} hours", ExpiryCheck.TotalHours);
        }
    }

    private bool Reissue(bool checkNames)
    {
        if (!string.IsNullOrWhiteSpace(_options.Network.CertificatePath) || _ca is null)
        {
            return false;
        }

        lock (_gate)
        {
            // Once the previous CA has expired it vouches for nothing: stop handing it out.
            if (PreviousCaExpiresAt is { } previousEnds && previousEnds <= _time.GetUtcNow())
            {
                LoadRollover();
            }

            // Nothing can be issued from a CA that has expired since the server started: System health says so.
            if (_ca.NotAfter.ToUniversalTime() <= _time.GetUtcNow().UtcDateTime)
            {
                return false;
            }

            // In the CA's last weeks a certificate issued again would expire with the CA, as this one does.
            var needsRenewal = _server is null
                || (_server.NotAfter.ToUniversalTime() - _time.GetUtcNow().UtcDateTime < RenewBefore && IssuedUntil(_ca) > _server.NotAfter.ToUniversalTime())
                || (checkNames && !DesiredNames().SetEquals(SubjectAlternativeNames));
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

    /// <summary>
    /// The rollover's state, from the files beside the CA: a next CA prepared for this one, or after a switch the CA this one
    /// replaced. Each is taken only with the certificate the older CA signed for the newer one's key, valid now; anything
    /// else in their place is left aside. Under <see cref="_gate"/>.
    /// </summary>
    private void LoadRollover()
    {
        (NextCaFingerprint, NextCaPem, NextCaCrossPem) = (null, null, null);
        (PreviousCaFingerprint, PreviousCaExpiresAt, PreviousCaPem) = (null, null, null);
        if (_ca is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (ReadNext(_directories, _protector, _ca, now) is { } next)
        {
            using (next.Ca)
            using (next.Cross)
            {
                NextCaFingerprint = Convert.ToHexString(SHA256.HashData(next.Ca.RawData));
                NextCaPem = next.Ca.ExportCertificatePem();
                NextCaCrossPem = next.Cross.ExportCertificatePem();
            }

            _logger.LogInformation(
                "The next hospital CA ({Next}) is prepared: the server hands it out until it switches to it with 'certificates switch-ca'", NextCaFingerprint);
        }

        if (ReadPrevious(_directories, _ca, now) is { } previous)
        {
            using (previous.Previous)
            using (previous.Cross)
            {
                PreviousCaFingerprint = Convert.ToHexString(SHA256.HashData(previous.Previous.RawData));
                PreviousCaPem = previous.Previous.ExportCertificatePem();
                PreviousCaExpiresAt = new DateTimeOffset(previous.Previous.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            }

            _logger.LogInformation(
                "The hospital CA was switched: the previous CA ({Previous}) is handed out until it expires on {Until:u}", PreviousCaFingerprint, PreviousCaExpiresAt);
        }
    }

    /// <summary>
    /// The next CA prepared for <paramref name="ca"/> (its public certificate), with the certificate <paramref name="ca"/> signed
    /// for its key, when both are on disk, the next CA is valid at <paramref name="now"/>, and that certificate is for the next
    /// CA's name and key, signed by <paramref name="ca"/>, and valid at <paramref name="now"/>: devices take the next CA only
    /// while it is. To switch (<paramref name="switching"/>) it need not be: the devices took the next CA already, and a
    /// server whose CA expired before the switch is switched to the next CA all the same.
    /// </summary>
    private static (X509Certificate2 Ca, X509Certificate2 Cross)? ReadNext(
        DataDirectories directories, IDataProtector protector, X509Certificate2 ca, DateTimeOffset now, bool switching = false)
    {
        var nextPath = Path.Combine(directories.Certificates, NextCaFile);
        var crossPath = Path.Combine(directories.Certificates, CrossFile);
        if (!File.Exists(nextPath) || !File.Exists(crossPath))
        {
            return null;
        }

        X509Certificate2? next = null;
        X509Certificate2? cross = null;
        try
        {
            using (var withKey = Open(nextPath, protector))
            {
                next = withKey is null ? null : X509CertificateLoader.LoadCertificate(withKey.RawData);
            }

            cross = X509Certificate2.CreateFromPem(File.ReadAllText(crossPath));
            if (next is not null && ValidAt(next, now) && (switching || ValidAt(cross, now)) && IsCrossFor(cross, next, ca))
            {
                return (next, cross);
            }
        }
        catch (CryptographicException)
        {
            // Unreadable: as if it were not there.
        }

        next?.Dispose();
        cross?.Dispose();
        return null;
    }

    /// <summary>
    /// After a switch, the CA <paramref name="ca"/> replaced, with the certificate it signed for <paramref name="ca"/>'s key,
    /// while the previous CA is valid at <paramref name="now"/> and that certificate is for <paramref name="ca"/>, signed by it.
    /// </summary>
    private static (X509Certificate2 Previous, X509Certificate2 Cross)? ReadPrevious(DataDirectories directories, X509Certificate2 ca, DateTimeOffset now)
    {
        var previousPath = Path.Combine(directories.Certificates, PreviousCaPublicFile);
        var crossPath = Path.Combine(directories.Certificates, CrossFile);
        if (!File.Exists(previousPath) || !File.Exists(crossPath))
        {
            return null;
        }

        X509Certificate2? previous = null;
        X509Certificate2? cross = null;
        try
        {
            previous = X509Certificate2.CreateFromPem(File.ReadAllText(previousPath));
            cross = X509Certificate2.CreateFromPem(File.ReadAllText(crossPath));
            if (ValidAt(previous, now) && IsCrossFor(cross, ca, previous))
            {
                return (previous, cross);
            }
        }
        catch (CryptographicException)
        {
            // Unreadable: as if it were not there.
        }

        previous?.Dispose();
        cross?.Dispose();
        return null;
    }

    private static bool ValidAt(X509Certificate2 certificate, DateTimeOffset now) =>
        certificate.NotBefore.ToUniversalTime() <= now.UtcDateTime && now.UtcDateTime < certificate.NotAfter.ToUniversalTime();

    /// <summary>
    /// Whether <paramref name="cross"/> is the certificate <paramref name="older"/> (a hospital CA, self-signed) signed for
    /// <paramref name="newer"/>'s key: the same name, the same key, another key than <paramref name="older"/>'s, its signature
    /// checked.
    /// </summary>
    private static bool IsCrossFor(X509Certificate2 cross, X509Certificate2 newer, X509Certificate2 older) =>
        cross.SubjectName.RawData.AsSpan().SequenceEqual(newer.SubjectName.RawData)
        && cross.PublicKey.EncodedKeyValue.RawData.AsSpan().SequenceEqual(newer.PublicKey.EncodedKeyValue.RawData)
        && !older.PublicKey.EncodedKeyValue.RawData.AsSpan().SequenceEqual(newer.PublicKey.EncodedKeyValue.RawData)
        && older.SubjectName.RawData.AsSpan().SequenceEqual(older.IssuerName.RawData)
        && IsIssuedBy(cross, older);

    /// <summary>
    /// Whether <paramref name="certificate"/> is issued in its own name and names its own key as its issuer's (or no key), as a
    /// self-signed CA does. The certificate the previous CA signs for a CA has the CA's name too, but names the previous CA's key.
    /// </summary>
    private static bool NamesItselfAsIssuer(X509Certificate2 certificate)
    {
        var issuerKey = certificate.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
        var ownKey = certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifierBytes;
        return certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
            && (issuerKey is not { } named || ownKey is not { } own || named.Span.SequenceEqual(own.Span));
    }

    private X509Certificate2 LoadOrCreateCa()
    {
        var path = Path.Combine(_directories.Certificates, CaFile);
        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12(_protector.Unprotect(File.ReadAllBytes(path)), null);
        }

        using var ca = CreateCa(_options.DefaultFacilityName, _time.GetUtcNow());
        var pfx = ca.Export(X509ContentType.Pkcs12);
        WriteProtected(path, pfx);
        _logger.LogInformation("Created hospital CA certificate (valid until {Expiry:u})", ca.NotAfter);
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    /// <summary>
    /// A new hospital CA with its private key: RSA 3072, valid for ten years, able to issue only the server certificates
    /// under it. Its name carries the day it was made, so a device that trusts two (after <see cref="NewCa"/>) tells them
    /// apart; the facility's name may hold a comma or other characters a distinguished name escapes.
    /// </summary>
    private static X509Certificate2 CreateCa(string facilityName, DateTimeOffset now)
    {
        var name = new X500DistinguishedNameBuilder();
        name.AddOrganizationalUnitName(facilityName);
        name.AddOrganizationName("HyFib HealthCare360");
        name.AddCommonName($"HyFib HealthCare360 Hospital CA ({Environment.MachineName} {now.UtcDateTime:yyyy-MM-dd})");
        return CreateCa(name.Build(), now);
    }

    /// <summary>A new hospital CA named <paramref name="name"/>, with a new key; see <see cref="CreateCa(string, DateTimeOffset)"/>.</summary>
    private static X509Certificate2 CreateCa(X500DistinguishedName name, DateTimeOffset now)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(request);

        using var ca = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return X509CertificateLoader.LoadPkcs12(ca.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// A hospital CA's extensions: a CA whose path length is 0 (it issues server certificates only), able to sign
    /// certificates and revocation lists, and its key's identifier.
    /// </summary>
    private static void AddCaExtensions(CertificateRequest request)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    }

    /// <summary>
    /// The certificate <paramref name="previous"/> signs for the next CA <paramref name="ca"/>'s key: its word that
    /// <paramref name="ca"/> succeeds it. The same name as both (self-issued, as Windows certificate services renew a CA with a
    /// new key: RFC 4210 4.4), valid until <paramref name="previous"/> expires, and naming <paramref name="previous"/>'s key
    /// as its issuer's. Devices check it themselves; it is never part of a chain the server sends.
    /// </summary>
    private static X509Certificate2 CreateCross(X509Certificate2 ca, X509Certificate2 previous, DateTimeOffset now)
    {
        var request = new CertificateRequest(ca.SubjectName, ca.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(request);
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(previous, true, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notBefore = now.AddDays(-1) < previous.NotBefore ? new DateTimeOffset(previous.NotBefore.ToUniversalTime(), TimeSpan.Zero) : now.AddDays(-1);
        return request.Create(previous, notBefore, new DateTimeOffset(previous.NotAfter.ToUniversalTime(), TimeSpan.Zero), serial);
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
            if (desired.SetEquals(meta) && existing.NotAfter.ToUniversalTime() - _time.GetUtcNow().UtcDateTime > RenewBefore && IsIssuedBy(existing, ca))
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
        using var issued = request.Create(ca, _time.GetUtcNow().AddDays(-1), IssuedUntil(ca), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        var pfx = withKey.Export(X509ContentType.Pkcs12);

        WriteProtected(path, pfx);
        var names = desired.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        File.WriteAllText(metaPath, JsonSerializer.Serialize(names));
        SubjectAlternativeNames = names;
        _logger.LogInformation("Issued LAN server certificate for {Names} (expires {Expiry:u})", string.Join(", ", names), issued.NotAfter);
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    /// <summary>
    /// When a server certificate issued now expires: in 397 days, which keeps Apple and Chrome happy for tablets that trust
    /// the hospital CA, but no later than the CA itself, as nothing can be issued past its issuer's expiry. In the CA's last
    /// year the certificate is shorter, and System health warns before the CA expires.
    /// </summary>
    private DateTime IssuedUntil(X509Certificate2 ca)
    {
        var full = _time.GetUtcNow().UtcDateTime.AddDays(397);
        var caExpires = ca.NotAfter.ToUniversalTime();
        return full < caExpires ? full : caExpires;
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
    /// This server's hospital CA, from the public certificate it hands out (no secret, so it opens without administrator
    /// rights); null before the server first starts.
    /// </summary>
    public static X509Certificate2? PublicCa(DataDirectories directories)
    {
        var path = Path.Combine(directories.Certificates, CaPublicFile);
        return File.Exists(path) ? X509Certificate2.CreateFromPem(File.ReadAllText(path)) : null;
    }

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
            var now = DateTimeOffset.UtcNow;

            // After a switch the standby hands out the CA this one replaced, as the primary does. A next CA prepared here stays
            // here: the standby serves with this CA, which the devices hold, and is given the next one once it is switched to.
            var previous = ReadPrevious(directories, ca, now);
            var next = ReadNext(directories, dataProtection.CreateProtector(ProtectorPurpose), ca, now);
            try
            {
                var bundle = new X509Certificate2Collection(ca);
                if (previous is { } carried)
                {
                    bundle.Add(carried.Cross);
                    bundle.Add(carried.Previous);
                }

                var file = bundle.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, ExportIterations), password);
                return new ExportedHospitalCa(
                    file, Convert.ToHexString(SHA256.HashData(ca.RawData)), new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                    PreviousExpiresAt(previous), NextCaLeftHere: next is not null);
            }
            finally
            {
                previous?.Cross.Dispose();
                previous?.Previous.Dispose();
                next?.Ca.Dispose();
                next?.Cross.Dispose();
            }
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
        var bundle = X509CertificateLoader.LoadPkcs12Collection(pkcs12, password, X509KeyStorageFlags.Exportable);
        try
        {
            return ImportCa(directories, dataProtection, bundle, now);
        }
        finally
        {
            foreach (var certificate in bundle)
            {
                certificate.Dispose();
            }
        }
    }

    private static ImportedHospitalCa ImportCa(DataDirectories directories, IDataProtectionProvider dataProtection, X509Certificate2Collection bundle, DateTimeOffset now)
    {
        // The CA: the certificate with the key that names that key as its issuer's. After a switch the file also holds the
        // certificate the previous CA signed for the same key, which a PKCS#12 reader may pair with the key as well.
        var ca = bundle.FirstOrDefault(c => c.HasPrivateKey && NamesItselfAsIssuer(c)) ?? bundle.FirstOrDefault(c => c.HasPrivateKey) ?? bundle.FirstOrDefault()
            ?? throw new InvalidDataException("The file holds no certificate: export the CA with 'certificates export-ca'.");
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
        var path = Path.Combine(directories.Certificates, CaFile);

        // After a switch, the CA this one replaced comes with it, with the certificate it signed for this one; any left here from
        // another CA vouches for nothing, and goes.
        var cross = bundle.FirstOrDefault(c => !c.RawData.AsSpan().SequenceEqual(ca.RawData)
            && c.PublicKey.EncodedKeyValue.RawData.AsSpan().SequenceEqual(ca.PublicKey.EncodedKeyValue.RawData));
        var previous = cross is null ? null : bundle.FirstOrDefault(c => !c.RawData.AsSpan().SequenceEqual(cross.RawData) && IsCrossFor(cross, ca, c));
        var carried = cross is not null && previous is not null && ValidAt(previous, now) ? (cross, previous) : ((X509Certificate2, X509Certificate2)?)null;

        if (File.Exists(path) && SameCertificate(path, protector, ca))
        {
            File.WriteAllText(Path.Combine(directories.Certificates, CaPublicFile), ca.ExportCertificatePem());
            if (carried is not null)
            {
                WritePrevious(directories, carried);
            }

            return new ImportedHospitalCa(fingerprint, expires, Changed: false, PreviousKeptAs: null, PreviousExpiresAt(carried));
        }

        // Another CA here: a next CA prepared for it, and what it replaced, belong to it.
        ClearRollover(directories);
        WritePrevious(directories, carried);
        return new ImportedHospitalCa(fingerprint, expires, Changed: true, Install(directories, protector, ca, now), PreviousExpiresAt(carried));
    }

    private static DateTimeOffset? PreviousExpiresAt((X509Certificate2 Cross, X509Certificate2 Previous)? carried) =>
        carried is { } it ? new DateTimeOffset(it.Previous.NotAfter.ToUniversalTime(), TimeSpan.Zero) : null;

    /// <summary>
    /// Writes the CA a switch replaced, and the certificate it signed for the current one, beside the CA; nothing when there
    /// is none. They are public, and taken only with the CA they are for (<see cref="ReadPrevious"/>).
    /// </summary>
    private static void WritePrevious(DataDirectories directories, (X509Certificate2 Cross, X509Certificate2 Previous)? carried)
    {
        if (carried is not { } written)
        {
            return;
        }

        Directory.CreateDirectory(directories.Certificates);
        File.WriteAllText(Path.Combine(directories.Certificates, PreviousCaPublicFile), written.Previous.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directories.Certificates, CrossFile), written.Cross.ExportCertificatePem());
    }

    /// <summary>
    /// Removes a prepared next CA, the certificate the older CA signed for the newer, and the previous CA; nothing on a machine
    /// that has no certificates yet (a standby given the CA before it first started).
    /// </summary>
    private static void ClearRollover(DataDirectories directories)
    {
        if (!Directory.Exists(directories.Certificates))
        {
            return;
        }

        foreach (var file in new[] { NextCaFile, CrossFile, PreviousCaPublicFile })
        {
            File.Delete(Path.Combine(directories.Certificates, file));
        }
    }

    /// <summary>
    /// Prepares the next hospital CA, the first of a rollover's two steps: this CA's name, a new key, valid for ten years, and
    /// a certificate this CA signs for the new key (self-issued: RFC 4210 4.4, as Windows certificate services renew a CA
    /// with a new key). The server keeps serving with this CA, and hands both out: HC360 Desktop and the lab agents take the
    /// next CA as a second pin, on this CA's word, and tablets and browsers are given it, before <see cref="SwitchCa"/>. The
    /// next CA's key is protected for this machine. Run with the HC360 service stopped, or restart it after.
    /// <para>
    /// Not for a CA whose key may have been seen by someone who should not have it: whoever has the key could sign a "next"
    /// CA of their own the same way. Use <see cref="NewCa"/> then, and set every device up again.
    /// </para>
    /// </summary>
    /// <exception cref="FileNotFoundException">This server has no hospital CA yet.</exception>
    /// <exception cref="InvalidOperationException">
    /// The CA has expired, a next CA is prepared already, or the CA this one replaced in the last switch has not expired yet.
    /// </exception>
    public static PreparedHospitalCa PrepareCa(DataDirectories directories, IDataProtectionProvider dataProtection, DateTimeOffset now)
    {
        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        var path = Path.Combine(directories.Certificates, CaFile);
        using var current = (File.Exists(path) ? Open(path, protector) : null)
            ?? throw new FileNotFoundException("This server has no hospital CA yet: HC360 makes one when it first starts.", path);
        var currentExpires = new DateTimeOffset(current.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        if (currentExpires <= now)
        {
            throw new InvalidOperationException(
                $"The hospital CA expired on {currentExpires:dd MMM yyyy}, and an expired CA cannot vouch for the next one. Make a new CA with "
                + "'certificates new-ca' and set every device up again, as docs/deployment/CA-ROLLOVER.md says.");
        }

        if (ReadNext(directories, protector, current, now) is { } prepared)
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(prepared.Ca.RawData));
            prepared.Ca.Dispose();
            prepared.Cross.Dispose();
            throw new InvalidOperationException(
                $"The next CA, {fingerprint}, is prepared already: switch to it with 'certificates switch-ca' once every device holds it.");
        }

        if (ReadPrevious(directories, current, now) is { } previous)
        {
            var until = new DateTimeOffset(previous.Previous.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            previous.Previous.Dispose();
            previous.Cross.Dispose();
            throw new InvalidOperationException(
                $"Devices keep the CA this one replaced as a second pin until it expires on {until:dd MMM yyyy}, and hold two at most: prepare the "
                + "next CA after that day.");
        }

        using var next = CreateCa(current.SubjectName, now);
        using var cross = CreateCross(next, current, now);

        // The next CA's key first, then the certificate that vouches for it: one cut short leaves nothing a device would take.
        Directory.CreateDirectory(directories.Certificates);
        ClearRollover(directories);
        var unprotected = next.Export(X509ContentType.Pkcs12);
        try
        {
            var nextPath = Path.Combine(directories.Certificates, NextCaFile);
            File.WriteAllBytes(nextPath + ".tmp", protector.Protect(unprotected));
            File.Move(nextPath + ".tmp", nextPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unprotected);
        }

        File.WriteAllText(Path.Combine(directories.Certificates, CrossFile), cross.ExportCertificatePem());
        return new PreparedHospitalCa(
            Convert.ToHexString(SHA256.HashData(next.RawData)), new DateTimeOffset(next.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            Convert.ToHexString(SHA256.HashData(current.RawData)), currentExpires);
    }

    /// <summary>
    /// The next CA prepared for this server's CA (upper-case hex SHA-256), as the devices report it once they hold it, to
    /// switch to; null when none is, or this server has no CA yet. A server whose CA has expired is switched all the same.
    /// </summary>
    public static string? PreparedNextCaFingerprint(DataDirectories directories, IDataProtectionProvider dataProtection, DateTimeOffset now)
    {
        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        var path = Path.Combine(directories.Certificates, CaFile);
        using var current = File.Exists(path) ? Open(path, protector) : null;
        if (current is null || ReadNext(directories, protector, current, now, switching: true) is not { } next)
        {
            return null;
        }

        using (next.Ca)
        using (next.Cross)
        {
            return Convert.ToHexString(SHA256.HashData(next.Ca.RawData));
        }
    }

    /// <summary>
    /// Switches the server to the prepared next CA (<see cref="PrepareCa"/>), the second of a rollover's two steps. The current
    /// CA is kept under a new name, never deleted, and its public certificate is handed out until it expires, for devices
    /// that keep it as a second pin and for a standby not yet given the new CA; the server certificate it issued is removed,
    /// and the server issues one from the new CA when it next starts. Every device that holds the next CA trusts the server
    /// after the switch; one that holds only the current CA does not, so 'certificates switch-ca' refuses while any device
    /// seen does not report it. Run with the HC360 service stopped, then start it.
    /// </summary>
    /// <exception cref="FileNotFoundException">This server has no hospital CA yet.</exception>
    /// <exception cref="InvalidOperationException">No next CA is prepared.</exception>
    public static SwitchedHospitalCa SwitchCa(DataDirectories directories, IDataProtectionProvider dataProtection, DateTimeOffset now)
    {
        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        var path = Path.Combine(directories.Certificates, CaFile);
        using var current = (File.Exists(path) ? Open(path, protector) : null)
            ?? throw new FileNotFoundException("This server has no hospital CA yet: HC360 makes one when it first starts.", path);
        if (ReadNext(directories, protector, current, now, switching: true) is not { } prepared)
        {
            throw new InvalidOperationException(
                "No next CA is prepared for this server's CA: prepare one with 'certificates prepare-ca', and switch once every device holds it.");
        }

        prepared.Ca.Dispose();
        prepared.Cross.Dispose();
        var nextPath = Path.Combine(directories.Certificates, NextCaFile);
        using var next = OpenExportable(nextPath, protector)
            ?? throw new InvalidOperationException("The prepared next CA cannot be opened on this machine: prepare it again with 'certificates prepare-ca'.");

        // The CA it replaces is written beside it first, and the certificate that vouched for the next CA now vouches for this
        // one: a switch cut short leaves the next CA prepared, and nothing a device would take for a switch.
        File.WriteAllText(Path.Combine(directories.Certificates, PreviousCaPublicFile), current.ExportCertificatePem());
        var keptAs = Install(directories, protector, next, now);
        File.Delete(nextPath);
        return new SwitchedHospitalCa(
            Convert.ToHexString(SHA256.HashData(next.RawData)), new DateTimeOffset(next.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            Convert.ToHexString(SHA256.HashData(current.RawData)), new DateTimeOffset(current.NotAfter.ToUniversalTime(), TimeSpan.Zero), keptAs);
    }

    /// <summary>
    /// Makes a new hospital CA in place of this server's: for a CA in its last year, or one whose key may have been seen by
    /// someone it should not have been (docs/deployment/CA-ROLLOVER.md). The CA it replaces is kept under a new name
    /// (<c>hospital-ca.pfx.protected.replaced-&lt;time&gt;</c>), never deleted, so it can be put back, and the server
    /// certificate it issued is removed: the server issues a new one from the new CA when it next starts. Every workstation,
    /// tablet, HC360 Desktop and lab agent trusts the CA it replaces, so each must be set up again. Run with the HC360
    /// service stopped, or restart it after.
    /// </summary>
    public static NewHospitalCa NewCa(DataDirectories directories, IDataProtectionProvider dataProtection, string facilityName, DateTimeOffset now)
    {
        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        var path = Path.Combine(directories.Certificates, CaFile);
        var previous = File.Exists(path) ? Open(path, protector) : null;
        try
        {
            using var ca = CreateCa(facilityName, now);
            ClearRollover(directories);
            var keptAs = Install(directories, protector, ca, now);
            return new NewHospitalCa(
                Convert.ToHexString(SHA256.HashData(ca.RawData)), new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero), keptAs,
                previous is null ? null : Convert.ToHexString(SHA256.HashData(previous.RawData)),
                previous is null ? null : new DateTimeOffset(previous.NotAfter.ToUniversalTime(), TimeSpan.Zero));
        }
        finally
        {
            previous?.Dispose();
        }
    }

    /// <summary>
    /// The name a CA that is replaced is kept under: the time it was replaced, and a number after it when another was
    /// replaced in the same second (a switch, then a new CA, by a script).
    /// </summary>
    private static string KeptAsName(string path, DateTimeOffset now)
    {
        var name = $"{path}.replaced-{now.UtcDateTime:yyyyMMdd-HHmmss}";
        for (var n = 2; File.Exists(name); n++)
        {
            name = $"{path}.replaced-{now.UtcDateTime:yyyyMMdd-HHmmss}-{n}";
        }

        return name;
    }

    /// <summary>
    /// Puts <paramref name="ca"/> in place of this server's CA, and removes the server certificate the CA it replaces issued.
    /// Returns the file name that CA is kept under, null when there was none.
    /// </summary>
    private static string? Install(DataDirectories directories, IDataProtector protector, X509Certificate2 ca, DateTimeOffset now)
    {
        Directory.CreateDirectory(directories.Certificates);
        var path = Path.Combine(directories.Certificates, CaFile);
        var keptAs = File.Exists(path) ? KeptAsName(path, now) : null;

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
        File.WriteAllText(Path.Combine(directories.Certificates, CaPublicFile), ca.ExportCertificatePem());
        return keptAs is null ? null : Path.GetFileName(keptAs);
    }

    /// <summary>Whether the CA at <paramref name="path"/> is <paramref name="ca"/>; one this machine cannot open is not.</summary>
    private static bool SameCertificate(string path, IDataProtector protector, X509Certificate2 ca)
    {
        using var existing = Open(path, protector);
        return existing is not null && existing.RawData.AsSpan().SequenceEqual(ca.RawData);
    }

    /// <summary>The CA at <paramref name="path"/>; null when this machine cannot open it.</summary>
    private static X509Certificate2? Open(string path, IDataProtector protector) => Open(path, protector, X509KeyStorageFlags.DefaultKeySet);

    /// <summary>The CA at <paramref name="path"/> with a key that can be written out again, to install it under another name.</summary>
    private static X509Certificate2? OpenExportable(string path, IDataProtector protector) => Open(path, protector, X509KeyStorageFlags.Exportable);

    private static X509Certificate2? Open(string path, IDataProtector protector, X509KeyStorageFlags flags)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(protector.Unprotect(File.ReadAllBytes(path)), null, flags);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="certificate"/> chains to <paramref name="ca"/>, its signature checked.</summary>
    private static bool IsIssuedBy(X509Certificate2 certificate, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        // The private CA publishes no revocation lists. Whether a certificate is valid now is each caller's own check: that a CA
        // signed it stays true after either expires (the next CA's word from a CA that expired before the switch).
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
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
        _expiryCheck?.Dispose();
        lock (_gate)
        {
            _server?.Dispose();
            _ca?.Dispose();
        }
    }
}
