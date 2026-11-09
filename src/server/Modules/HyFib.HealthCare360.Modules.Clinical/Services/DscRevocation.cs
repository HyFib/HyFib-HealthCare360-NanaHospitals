using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// A certificate revocation list (RFC 5280, section 5) as a Certifying Authority publishes it: who issued it, when, when
/// the next one is due, and the serial numbers it revokes. Read from DER or PEM; its signature is checked against the
/// issuing CA's certificate before it is believed (<see cref="IsSignedBy"/>).
/// </summary>
public sealed class CertificateRevocationList
{
    private static readonly Dictionary<string, HashAlgorithmName> RsaAlgorithms = new(StringComparer.Ordinal)
    {
        ["1.2.840.113549.1.1.11"] = HashAlgorithmName.SHA256,
        ["1.2.840.113549.1.1.12"] = HashAlgorithmName.SHA384,
        ["1.2.840.113549.1.1.13"] = HashAlgorithmName.SHA512,
    };

    private static readonly Dictionary<string, HashAlgorithmName> EcdsaAlgorithms = new(StringComparer.Ordinal)
    {
        ["1.2.840.10045.4.3.2"] = HashAlgorithmName.SHA256,
        ["1.2.840.10045.4.3.3"] = HashAlgorithmName.SHA384,
        ["1.2.840.10045.4.3.4"] = HashAlgorithmName.SHA512,
    };

    private CertificateRevocationList(byte[] raw, byte[] tbs, string algorithm, byte[] signature, X500DistinguishedName issuer, DateTimeOffset thisUpdate,
        DateTimeOffset? nextUpdate, IReadOnlySet<string> revoked)
    {
        Raw = raw;
        Tbs = tbs;
        Algorithm = algorithm;
        Signature = signature;
        Issuer = issuer;
        ThisUpdate = thisUpdate;
        NextUpdate = nextUpdate;
        Revoked = revoked;
    }

    /// <summary>The list as published (DER).</summary>
    public byte[] Raw { get; }

    public X500DistinguishedName Issuer { get; }

    /// <summary>When the CA issued the list: its age is counted from here.</summary>
    public DateTimeOffset ThisUpdate { get; }

    /// <summary>When the CA issues the next one; after this the list is stale (still used, with its age shown).</summary>
    public DateTimeOffset? NextUpdate { get; }

    /// <summary>The revoked serial numbers, in upper-case hex without leading zeros.</summary>
    public IReadOnlySet<string> Revoked { get; }

    private byte[] Tbs { get; }

    private string Algorithm { get; }

    private byte[] Signature { get; }

    public bool IsStale(DateTimeOffset now) => NextUpdate is { } next && now > next;

    public bool Lists(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Revoked.Contains(Serial(certificate.SerialNumberBytes.Span));
    }

    /// <summary>Reads a CRL from DER or PEM ("-----BEGIN X509 CRL-----"); null for anything else.</summary>
    public static CertificateRevocationList? TryRead(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            var der = data;
            if (data.Length > 0 && data[0] == (byte)'-')
            {
                var text = System.Text.Encoding.ASCII.GetString(data);
                var fields = PemEncoding.Find(text);
                der = Convert.FromBase64String(text[fields.Base64Data]);
            }

            return Read(der);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static CertificateRevocationList Read(byte[] der)
    {
        var outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        var tbsBytes = outer.PeekEncodedValue().ToArray();
        var tbs = outer.ReadSequence();
        var algorithm = outer.ReadSequence().ReadObjectIdentifier();
        var signature = outer.ReadBitString(out _);

        if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
        {
            tbs.ReadInteger();
        }

        tbs.ReadSequence(); // the signature algorithm, again
        var issuer = new X500DistinguishedName(tbs.ReadEncodedValue().Span);
        var thisUpdate = ReadTime(tbs);
        DateTimeOffset? nextUpdate = tbs.HasData && IsTime(tbs.PeekTag()) ? ReadTime(tbs) : null;
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            var entries = tbs.ReadSequence();
            while (entries.HasData)
            {
                var entry = entries.ReadSequence();
                revoked.Add(Serial(entry.ReadIntegerBytes().Span));
            }
        }

        return new CertificateRevocationList(der, tbsBytes, algorithm, signature, issuer, thisUpdate, nextUpdate, revoked);
    }

    /// <summary>Whether <paramref name="issuer"/> issued this list: its name matches and its key verifies the list's signature.</summary>
    public bool IsSignedBy(X509Certificate2 issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        if (!issuer.SubjectName.RawData.AsSpan().SequenceEqual(Issuer.RawData))
        {
            return false;
        }

        try
        {
            if (RsaAlgorithms.TryGetValue(Algorithm, out var rsaHash) && issuer.GetRSAPublicKey() is { } rsa)
            {
                using (rsa)
                {
                    return rsa.VerifyData(Tbs, Signature, rsaHash, RSASignaturePadding.Pkcs1);
                }
            }

            if (EcdsaAlgorithms.TryGetValue(Algorithm, out var ecHash) && issuer.GetECDsaPublicKey() is { } ec)
            {
                using (ec)
                {
                    return ec.VerifyData(Tbs, Signature, ecHash, DSASignatureFormat.Rfc3279DerSequence);
                }
            }
        }
        catch (CryptographicException)
        {
            return false;
        }

        return false;
    }

    /// <summary>A serial number as upper-case hex, without leading zero bytes (a CRL keeps one where the high bit is set).</summary>
    public static string Serial(ReadOnlySpan<byte> bigEndian)
    {
        var start = 0;
        while (start < bigEndian.Length - 1 && bigEndian[start] == 0)
        {
            start++;
        }

        return Convert.ToHexString(bigEndian[start..]);
    }

    /// <summary>The HTTP addresses a certificate's CRL distribution points name (extension 2.5.29.31).</summary>
    public static IReadOnlyList<string> DistributionPoints(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var extension = certificate.Extensions["2.5.29.31"];
        if (extension is null)
        {
            return [];
        }

        var urls = new List<string>();
        try
        {
            CollectUris(new AsnReader(extension.RawData, AsnEncodingRules.DER), urls);
        }
        catch (AsnContentException)
        {
            return [];
        }

        return urls.Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    // A GeneralName's uniformResourceIdentifier is [6] IA5String, wherever in the distribution points it sits.
    private static void CollectUris(AsnReader reader, List<string> urls)
    {
        while (reader.HasData)
        {
            var tag = reader.PeekTag();
            if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6 && !tag.IsConstructed)
            {
                urls.Add(reader.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6)));
            }
            else if (tag.IsConstructed)
            {
                CollectUris(new AsnReader(reader.ReadEncodedValue(), AsnEncodingRules.DER).ReadSequence(tag), urls);
            }
            else
            {
                reader.ReadEncodedValue();
            }
        }
    }

    private static bool IsTime(Asn1Tag tag) => tag.HasSameClassAndValue(Asn1Tag.UtcTime) || tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime);

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();
}

/// <summary>A cached CRL, as System health and the signing check describe it.</summary>
public sealed record CachedCrl(string Issuer, DateTimeOffset ThisUpdate, DateTimeOffset? NextUpdate, DateTimeOffset SavedAt, string File);

/// <summary>
/// The server's cache of the Certifying Authorities' CRLs (ERX-04): <c>HealthCare360:Clinical:DscCrlDirectory</c>, by default
/// <c>certs\dsc-crl</c> in the data folder. HC360 saves a CA's CRL there whenever it can reach the CA (at signing, and
/// every six hours from the CRL addresses it has seen: <see cref="DscCrlRefreshJob"/>), and uses it when it cannot,
/// with its age shown; IT may copy CRLs there by hand for a server with no route to the CAs. A CRL is believed only once
/// its signature checks out against the issuing CA's certificate.
/// </summary>
public sealed class DscCrlCache(IConfiguration configuration, DataDirectories directories, TimeProvider time)
{
    public const string ConfigurationKey = "HealthCare360:Clinical:DscCrlDirectory";

    public const string DefaultFolderName = "dsc-crl";

    private readonly Lock _gate = new();
    private string? _stamp;
    private IReadOnlyList<(CertificateRevocationList List, CachedCrl Info)> _lists = [];

    public string Folder { get; } = configuration[ConfigurationKey] is { Length: > 0 } configured
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), directories.Root)
        : Path.Combine(directories.Certificates, DefaultFolderName);

    /// <summary>The CRLs in the folder now, newest first.</summary>
    public IReadOnlyList<CachedCrl> Lists => Load().Select(l => l.Info).ToList();

    /// <summary>The newest CRL <paramref name="issuer"/> signed, or null.</summary>
    public (CertificateRevocationList List, CachedCrl Info)? For(X509Certificate2 issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        foreach (var entry in Load())
        {
            if (entry.List.IsSignedBy(issuer))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Saves a CRL fetched from <paramref name="url"/> if it is newer than the one cached for its issuer, with the address, so
    /// the refresh job fetches it again. A list that does not read as a CRL is not saved.
    /// </summary>
    public bool Save(byte[] data, string url)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (CertificateRevocationList.TryRead(data) is not { } list)
        {
            return false;
        }

        Directory.CreateDirectory(Folder);
        var name = Convert.ToHexString(SHA256.HashData(list.Issuer.RawData))[..32];
        var path = Path.Combine(Folder, name + ".crl");
        if (File.Exists(path) && CertificateRevocationList.TryRead(File.ReadAllBytes(path)) is { } cached && cached.ThisUpdate >= list.ThisUpdate)
        {
            File.WriteAllText(Path.Combine(Folder, name + ".url"), url);
            return false;
        }

        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, list.Raw);
        File.Move(temporary, path, overwrite: true);
        File.WriteAllText(Path.Combine(Folder, name + ".url"), url);
        return true;
    }

    /// <summary>The addresses the cached CRLs came from, for the refresh job.</summary>
    public IReadOnlyList<string> Addresses()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(Folder, "*.url").Select(f => File.ReadAllText(f).Trim()).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IReadOnlyList<(CertificateRevocationList List, CachedCrl Info)> Load()
    {
        string[] files;
        try
        {
            files = Directory.Exists(Folder)
                ? [.. Directory.GetFiles(Folder).Where(f => f.EndsWith(".crl", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        var stamp = string.Join('|', files.Select(f => $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}"));
        lock (_gate)
        {
            if (_stamp == stamp)
            {
                return _lists;
            }
        }

        var lists = new List<(CertificateRevocationList, CachedCrl)>();
        foreach (var file in files)
        {
            try
            {
                if (CertificateRevocationList.TryRead(File.ReadAllBytes(file)) is { } list)
                {
                    lists.Add((list, new CachedCrl(list.Issuer.Name, list.ThisUpdate, list.NextUpdate, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero),
                        Path.GetFileName(file))));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file being written, or one the service may not read: the next check reads the folder again.
            }
        }

        var sorted = lists.OrderByDescending(l => l.Item1.ThisUpdate).ToList();
        lock (_gate)
        {
            (_stamp, _lists) = (stamp, sorted);
            return _lists;
        }
    }

    internal DateTimeOffset Now => time.GetUtcNow();
}

/// <summary>What an online revocation check of a certificate found.</summary>
public enum OnlineRevocationAnswer
{
    /// <summary>The CA could not be reached, or did not say.</summary>
    Unknown = 0,
    Good,
    Revoked,
}

/// <summary>
/// The CA asked whether a certificate is revoked, when it can be reached: OCSP where the certificate names a responder, or
/// the CRL at its distribution point (which is then cached). <see cref="DscOnlineRevocation"/> is the server's; a test
/// stands in one that answers as it is told.
/// </summary>
public interface IDscOnlineRevocation
{
    Task<OnlineRevocationAnswer> CheckAsync(X509Certificate2 certificate, X509Certificate2Collection extra, DscTrustAnchors anchors, CancellationToken ct);

    /// <summary>Fetches the CRL at <paramref name="url"/>, or null when the CA cannot be reached.</summary>
    Task<byte[]?> FetchCrlAsync(string url, CancellationToken ct);
}

/// <summary>
/// Asks the CA through Windows' (or OpenSSL's) own revocation checking, with the CCA India roots as the only trust, a short
/// time-out, and after a failure not again for ten minutes, so a server with no route to the CAs does not make every
/// doctor wait. <c>HealthCare360:Clinical:DscOnlineRevocation</c> false switches it off (the cached CRL alone is used).
/// </summary>
public sealed class DscOnlineRevocation(IConfiguration configuration, IHttpClientFactory http, TimeProvider time) : IDscOnlineRevocation
{
    public const string ConfigurationKey = "HealthCare360:Clinical:DscOnlineRevocation";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Backoff = TimeSpan.FromMinutes(10);
    private DateTimeOffset _unreachableUntil = DateTimeOffset.MinValue;

    private bool Enabled => !string.Equals(configuration[ConfigurationKey], "false", StringComparison.OrdinalIgnoreCase);

    public Task<OnlineRevocationAnswer> CheckAsync(X509Certificate2 certificate, X509Certificate2Collection extra, DscTrustAnchors anchors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(extra);
        ArgumentNullException.ThrowIfNull(anchors);
        if (!Enabled || anchors.IsEmpty || time.GetUtcNow() < _unreachableUntil)
        {
            return Task.FromResult(OnlineRevocationAnswer.Unknown);
        }

        return Task.Run(() =>
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(anchors.Roots);
            chain.ChainPolicy.ExtraStore.AddRange(anchors.Intermediates);
            chain.ChainPolicy.ExtraStore.AddRange(extra);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EndCertificateOnly;
            chain.ChainPolicy.UrlRetrievalTimeout = Timeout;
            chain.Build(certificate);
            var statuses = chain.ChainElements.Count > 0 ? chain.ChainElements[0].ChainElementStatus.Select(s => s.Status).ToList() : [];
            if (statuses.Contains(X509ChainStatusFlags.Revoked))
            {
                return OnlineRevocationAnswer.Revoked;
            }

            if (statuses.Contains(X509ChainStatusFlags.RevocationStatusUnknown) || statuses.Contains(X509ChainStatusFlags.OfflineRevocation))
            {
                _unreachableUntil = time.GetUtcNow() + Backoff;
                return OnlineRevocationAnswer.Unknown;
            }

            return OnlineRevocationAnswer.Good;
        }, ct);
    }

    public async Task<byte[]?> FetchCrlAsync(string url, CancellationToken ct)
    {
        if (!Enabled || time.GetUtcNow() < _unreachableUntil)
        {
            return null;
        }

        try
        {
            using var client = http.CreateClient(nameof(DscOnlineRevocation));
            client.Timeout = TimeSpan.FromSeconds(30);
            return await client.GetByteArrayAsync(new Uri(url), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            _unreachableUntil = time.GetUtcNow() + Backoff;
            return null;
        }
    }
}

/// <summary>How a DSC's revocation was checked at signing, and what was found.</summary>
/// <param name="Refusal">Why signing is refused (revoked or expired), or null.</param>
/// <param name="Words">The check in words, kept on the prescription: "Not revoked (checked online)", or the CRL's date and age.</param>
public sealed record DscRevocationOutcome(Error? Refusal, string Words);

/// <summary>
/// Whether a DSC may sign now (ERX-04): never an expired certificate, nor a revoked one. Revocation is asked of the CA when it
/// can be reached (<see cref="IDscOnlineRevocation"/>), and its CRL is fetched and cached then; when it cannot, the cached
/// CRL decides, however old, and its age is recorded. With no CRL cached and no route to the CA, the signature is
/// accepted and the prescription says its revocation was not checked; System health warns.
/// </summary>
public sealed class DscRevocationCheck(DscCrlCache cache, IDscOnlineRevocation online, TimeProvider time)
{
    public const string RevokedCode = "prescription.certificate_revoked";

    public const string ExpiredCode = "prescription.certificate_expired";

    public async Task<DscRevocationOutcome> CheckAsync(byte[] content, byte[] signature, DscTrustAnchors anchors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.Decode(signature);
        var certificate = cms.SignerInfos[0].Certificate!;
        var now = time.GetUtcNow();
        if (now.UtcDateTime > certificate.NotAfter.ToUniversalTime() || now.UtcDateTime < certificate.NotBefore.ToUniversalTime())
        {
            return new(Error.Rule(ExpiredCode,
                $"This DSC is not valid today (valid {certificate.NotBefore:d MMM yyyy} to {certificate.NotAfter:d MMM yyyy}). Renew it with your Certifying Authority and sign with the new one."),
                "Expired");
        }

        var issuer = Issuer(certificate, cms.Certificates, anchors);

        // The CA, when it answers: OCSP, or its CRL, which is cached for when it does not.
        var answer = await online.CheckAsync(certificate, cms.Certificates, anchors, ct);
        if (answer == OnlineRevocationAnswer.Revoked)
        {
            return Revoked(certificate, "the Certifying Authority says so");
        }

        foreach (var url in CertificateRevocationList.DistributionPoints(certificate))
        {
            if (await online.FetchCrlAsync(url, ct) is { } fetched && CertificateRevocationList.TryRead(fetched) is { } list && issuer is not null && list.IsSignedBy(issuer))
            {
                cache.Save(fetched, url);
                break;
            }
        }

        if (issuer is not null && cache.For(issuer) is { } cached)
        {
            if (cached.List.Lists(certificate))
            {
                return Revoked(certificate, $"it is on the CRL of {Day(cached.Info.ThisUpdate)}");
            }

            if (answer == OnlineRevocationAnswer.Good)
            {
                return new(null, "Not revoked (checked online)");
            }

            var age = Age(now - cached.Info.ThisUpdate);
            return new(null, cached.List.IsStale(now)
                ? $"Not revoked on the CRL of {Day(cached.Info.ThisUpdate)} ({age} old, past its next update of {Day(cached.Info.NextUpdate!.Value)})"
                : $"Not revoked on the CRL of {Day(cached.Info.ThisUpdate)} ({age} old)");
        }

        return new(null, answer == OnlineRevocationAnswer.Good
            ? "Not revoked (checked online)"
            : "Revocation not checked: the Certifying Authority could not be reached and no CRL of it is cached");
    }

    // The CA that issued the DSC: from the trust folder or the certificates the signature carries, by the chain.
    private static X509Certificate2? Issuer(X509Certificate2 certificate, X509Certificate2Collection included, DscTrustAnchors anchors)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(anchors.Roots);
        chain.ChainPolicy.ExtraStore.AddRange(anchors.Intermediates);
        chain.ChainPolicy.ExtraStore.AddRange(included);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.Build(certificate);
        return chain.ChainElements.Count > 1 ? chain.ChainElements[1].Certificate : null;
    }

    private static DscRevocationOutcome Revoked(X509Certificate2 certificate, string because) =>
        new(Error.Rule(RevokedCode,
            $"This DSC (serial {certificate.SerialNumber}) has been revoked: {because}. It cannot sign. Contact your Certifying Authority for a new DSC."),
            "Revoked");

    public static string Age(TimeSpan age) => age.TotalHours < 48 ? $"{Math.Max(0, (int)age.TotalHours)} hours" : $"{(int)age.TotalDays} days";

    private static string Day(DateTimeOffset at) => at.ToOffset(TimeSpan.FromHours(5.5)).ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Fetches again, every six hours, the CRLs whose addresses HC360 has seen (where it runs jobs), so the cache is fresh for
/// when the CAs cannot be reached. A CRL is saved only when it reads as one and is newer than the one cached; one whose
/// issuer HC360 cannot confirm is still checked against the issuer at signing before it is believed.
/// </summary>
internal sealed class DscCrlRefreshJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<DscCrlRefreshJob> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var cache = scope.ServiceProvider.GetRequiredService<DscCrlCache>();
                var online = scope.ServiceProvider.GetRequiredService<IDscOnlineRevocation>();
                var trust = scope.ServiceProvider.GetRequiredService<DscTrustStore>().Current;
                var addresses = cache.Addresses()
                    .Concat(trust.Intermediates.Cast<X509Certificate2>().SelectMany(CertificateRevocationList.DistributionPoints))
                    .Distinct(StringComparer.Ordinal);
                foreach (var url in addresses)
                {
                    if (await online.FetchCrlAsync(url, stoppingToken) is { } crl)
                    {
                        cache.Save(crl, url);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Refreshing the DSC CRLs failed; it runs again on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>A number for System health: the age of a CRL in words.</summary>
internal static class CrlAges
{
    public static string Describe(CachedCrl crl, DateTimeOffset now) =>
        $"{crl.Issuer}: issued {DscRevocationCheck.Age(now - crl.ThisUpdate)} ago{(crl.NextUpdate is { } next && now > next ? $", past its next update ({DscRevocationCheck.Age(now - next)} overdue)" : string.Empty)}";
}
