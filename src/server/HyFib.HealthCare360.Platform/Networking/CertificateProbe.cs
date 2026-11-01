using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>How a probe of an address came out, numbered as monitoring tools (Nagios, Zabbix, PRTG) read a check's exit code.</summary>
public enum CertificateProbeOutcome
{
    /// <summary>Nothing to do.</summary>
    Ok = 0,

    /// <summary>A certificate needs planning: it expires within its warning period.</summary>
    Warning = 1,

    /// <summary>Devices cannot connect, or will not soon: act now.</summary>
    Critical = 2,
}

/// <summary>What a probe of one address found: each finding is a sentence, the worst decides the outcome.</summary>
public sealed record CertificateProbeResult(
    Uri Address, CertificateProbeOutcome Outcome, IReadOnlyList<string> Findings, DateTimeOffset? ServerExpiresAt, DateTimeOffset? CaExpiresAt,
    string? CaFingerprint);

/// <summary>
/// Checks an HC360 address from another machine, as a device that connects to it would (SRS DED-04): the certificate served
/// is for the address's name, was issued by the hospital CA the devices trust, and neither expires within the periods System
/// health warns in (<see cref="SystemHealthService.CertificateWarnings"/>, the same rule). Unlike System health it works when
/// HC360 is down or cannot send its alerts, and it sees the certificate devices are served rather than the one the server
/// holds. A scheduled task or a monitoring tool runs it (docs/deployment/CA-ROLLOVER.md).
/// </summary>
public static class CertificateProbe
{
    /// <summary>Where HC360 hands out its hospital CA, as HC360 Desktop and the lab agent read it at enrolment.</summary>
    public const string CaCertificatePath = "api/v1/system/ca-certificate";

    /// <summary>After a rollover, where the server hands out the CA its current one replaced (404 otherwise).</summary>
    public const string PreviousCaCertificatePath = "api/v1/system/ca-certificate/previous";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Probes <paramref name="address"/> (https, the staff address for <paramref name="pinnedFingerprint"/>). The certificate
    /// served must chain to the CA whose SHA-256 fingerprint the devices pinned, which the server hands out at
    /// <see cref="CaCertificatePath"/>; or to <paramref name="trustedCa"/>; or, given neither, to a root this machine trusts.
    /// </summary>
    public static async Task<CertificateProbeResult> ProbeAsync(
        Uri address, string? pinnedFingerprint, X509Certificate2? trustedCa, DateTimeOffset now, CancellationToken ct)
    {
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            return Failed(address, $"{address} is not an https address.");
        }

        X509Certificate2 served;
        X509Certificate2Collection sent;
        try
        {
            (served, sent) = await HandshakeAsync(address, ct);
        }
        catch (Exception ex) when (IsConnectionFailure(ex, ct))
        {
            return Failed(address, $"No TLS connection: {Reason(ex)} Is HC360 running, and is this its address?");
        }

        using (served)
        {
            var findings = new List<(CertificateProbeOutcome Outcome, string Text)>();
            var ca = trustedCa;
            X509Certificate2? handedOut = null;
            X509Certificate2? previous = null;
            DateTimeOffset? currentCaExpires = null;
            try
            {
                if (pinnedFingerprint is not null)
                {
                    try
                    {
                        handedOut = await FetchCaAsync(address, ct);
                    }
                    catch (Exception ex) when (IsConnectionFailure(ex, ct) || ex is HttpRequestException or CryptographicException)
                    {
                        return Failed(address, $"The hospital CA could not be read from {new Uri(address, CaCertificatePath)}: {Reason(ex)} "
                            + "Only the staff address hands it out; for another address give the CA's file (--ca-file).");
                    }

                    var handedOutFingerprint = Fingerprint(handedOut);
                    if (!string.Equals(handedOutFingerprint, Normalise(pinnedFingerprint), StringComparison.Ordinal))
                    {
                        // A rollover: the pinned CA, now the previous one, still vouches for the current one. Anything else is
                        // a CA the devices do not trust.
                        previous = await TryFetchAsync(new Uri(address, PreviousCaCertificatePath), ct);
                        if (previous is null || !string.Equals(Fingerprint(previous), Normalise(pinnedFingerprint), StringComparison.Ordinal))
                        {
                            return Failed(address,
                                $"The server hands out the CA {Group(handedOutFingerprint)}, not {Group(Normalise(pinnedFingerprint))}, which the devices "
                                + "pinned: none of them connects. If the CA was replaced on purpose, set every device up again (CA-ROLLOVER.md).");
                        }

                        // Devices that pin it trust the server only until it expires: in its last months, that is critical.
                        var rolledUntil = new DateTimeOffset(previous.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                        currentCaExpires = new DateTimeOffset(handedOut.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                        findings.Add((rolledUntil - now < SystemHealthService.CaUrgentPeriod ? CertificateProbeOutcome.Critical : CertificateProbeOutcome.Warning,
                            $"The hospital CA was rolled over: the server's CA is now {Group(handedOutFingerprint)}, and devices that pin "
                            + $"{Group(Normalise(pinnedFingerprint))} trust the server until {rolledUntil:dd MMM yyyy}. Give this probe the new "
                            + "fingerprint (--ca-fingerprint) before then, as every other device that pins the CA (CA-ROLLOVER.md)."));
                        ca = previous;
                    }
                    else
                    {
                        ca = handedOut;
                    }
                }

                if (!IssuedBy(served, ca, now, sent))
                {
                    findings.Add((CertificateProbeOutcome.Critical, ca is null
                        ? $"This machine does not trust the certificate served ({served.Subject}, issued by {served.Issuer})."
                        : $"The certificate served ({served.Subject}, issued by {served.Issuer}) was not issued by the hospital CA "
                            + $"{Group(Fingerprint(ca))}: the devices that trust that CA refuse it."));
                }

                var host = Host(address);
                if (!served.MatchesHostname(host))
                {
                    findings.Add((CertificateProbeOutcome.Critical,
                        $"The certificate served is not for {host} but for {string.Join(", ", Names(served))}: devices that use this address refuse it."));
                }

                // The rule System health uses on the server, here on what the server serves. Without the hospital CA (a
                // certificate an administrator supplied, checked against this machine's roots) only the certificate is judged.
                var serverExpires = new DateTimeOffset(served.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                DateTimeOffset? caExpires = ca is null ? null : new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                // After a rollover the CA that matters from here on is the server's current one; the previous one's end is said above.
                foreach (var warning in SystemHealthService.CertificateWarnings(new CertificateInfo(null, serverExpires, [], currentCaExpires ?? caExpires), now))
                {
                    findings.Add((warning.Severity == "critical" ? CertificateProbeOutcome.Critical : CertificateProbeOutcome.Warning, warning.Message));
                }

                var outcome = findings.Count == 0 ? CertificateProbeOutcome.Ok : findings.Max(f => f.Outcome);
                IReadOnlyList<string> said = findings.Count == 0
                    ? [$"The server certificate is valid until {serverExpires:dd MMM yyyy}" + (caExpires is { } until
                        ? $", and the hospital CA {Group(Fingerprint(ca!))} until {until:dd MMM yyyy}."
                        : ".")]
                    : [.. findings.OrderByDescending(f => f.Outcome).Select(f => f.Text)];
                return new CertificateProbeResult(address, outcome, said, serverExpires, caExpires, ca is null ? null : Fingerprint(ca));
            }
            finally
            {
                handedOut?.Dispose();
                previous?.Dispose();
                foreach (var certificate in sent)
                {
                    certificate.Dispose();
                }
            }
        }
    }

    /// <summary>The certificate the address serves, whatever it is: it is judged after the handshake, against the time given.</summary>
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation", Justification = "Nothing is sent over the connection: the certificate is checked after the handshake, against the CA the devices pinned.")]
    private static async Task<(X509Certificate2 Served, X509Certificate2Collection Sent)> HandshakeAsync(Uri address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        // Each address the name resolves to is tried, IPv4 or IPv6.
        using var client = new TcpClient();
        await client.ConnectAsync(Host(address), address.Port, timeout.Token);
        X509Certificate2? served = null;
        var sent = new X509Certificate2Collection();
        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = Host(address),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
            {
                served = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

                // The other certificates the server sent (after a rollover, the one the previous CA signed for the current CA),
                // as the devices take them: from the handshake's extra store, not the chain the platform built, which may hold
                // certificates from this computer's own stores.
                if (chain is not null && served is not null)
                {
                    foreach (var other in chain.ChainPolicy.ExtraStore)
                    {
                        if (!other.RawData.AsSpan().SequenceEqual(served.RawData) && !sent.Any(s => s.RawData.AsSpan().SequenceEqual(other.RawData)))
                        {
                            sent.Add(X509CertificateLoader.LoadCertificate(other.RawData));
                        }
                    }
                }

                return true;
            },
        }, timeout.Token);
        return (served ?? throw new AuthenticationException("The server sent no certificate."), sent);
    }

    /// <summary>A certificate the server hands out at <paramref name="url"/>, or null when it has none there (404).</summary>
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation", Justification = "The CA is trusted only once its fingerprint matches the one the devices pinned.")]
    private static async Task<X509Certificate2?> TryFetchAsync(Uri url, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = Timeout,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var http = new HttpClient(handler) { Timeout = Timeout };
        using var response = await http.GetAsync(url, ct);
        return response.IsSuccessStatusCode ? X509Certificate2.CreateFromPem(await response.Content.ReadAsStringAsync(ct)) : null;
    }

    /// <summary>The CA the server hands out, read as HC360 Desktop reads it at enrolment: not trusted until its fingerprint is checked.</summary>
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation", Justification = "The CA is trusted only once its fingerprint matches the one the devices pinned.")]
    private static async Task<X509Certificate2> FetchCaAsync(Uri address, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = Timeout,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var http = new HttpClient(handler) { Timeout = Timeout };
        var pem = await http.GetStringAsync(new Uri(address, CaCertificatePath), ct);
        return X509Certificate2.CreateFromPem(pem);
    }

    /// <summary>
    /// Whether <paramref name="served"/> chains to <paramref name="ca"/> (or, without one, to a root this machine trusts),
    /// its signatures checked. Expiry is left to the warnings, so an expired certificate is said to have expired.
    /// </summary>
    private static bool IssuedBy(X509Certificate2 served, X509Certificate2? ca, DateTimeOffset now, X509Certificate2Collection sent)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.AddRange(sent);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        if (ca is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
        }

        if (!chain.Build(served) || (ca is not null && !chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData)))
        {
            return false;
        }

        // As a device checks it: through what the server sent only. Windows also builds chains from this computer's stores,
        // and the probe on the Server Node itself would otherwise find there what the server does not send.
        for (var link = 1; link < chain.ChainElements.Count - 1; link++)
        {
            var linked = chain.ChainElements[link].Certificate.RawData;
            if (!sent.Any(certificate => certificate.RawData.AsSpan().SequenceEqual(linked)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The address's host as a name or an IP address, without the brackets an IPv6 address has in a URL.</summary>
    private static string Host(Uri address) => address.HostNameType == UriHostNameType.IPv6 ? address.Host.Trim('[', ']') : address.IdnHost;

    private static IEnumerable<string> Names(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault() is { } names
            ? names.EnumerateDnsNames().Concat(names.EnumerateIPAddresses().Select(ip => ip.ToString()))
            : [certificate.GetNameInfo(X509NameType.SimpleName, false)];

    private static CertificateProbeResult Failed(Uri address, string finding) =>
        new(address, CertificateProbeOutcome.Critical, [finding], null, null, null);

    private static bool IsConnectionFailure(Exception ex, CancellationToken ct) =>
        ex is SocketException or IOException or AuthenticationException or TimeoutException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static string Reason(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => $"no answer within {Timeout.TotalSeconds:0} seconds.",
        _ => (ex.InnerException?.Message ?? ex.Message).TrimEnd('.') + ".",
    };

    /// <summary>
    /// A CA certificate from a file: PEM, as the server writes <c>hc360-hospital-ca.crt</c> and hands it out, or DER, as
    /// Windows exports a <c>.cer</c>.
    /// </summary>
    public static X509Certificate2 LoadCaFile(string path)
    {
        var text = File.ReadAllText(path);
        return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(text)
            : X509CertificateLoader.LoadCertificateFromFile(path);
    }

    /// <summary>The SHA-256 fingerprint, upper-case hex, as the server announces it and HC360 Desktop pins it.</summary>
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>A fingerprint as given: upper case, without the spaces or colons it is often written with.</summary>
    public static string Normalise(string fingerprint) =>
        new([.. fingerprint.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant)]);

    /// <summary>"AB12CD34…" as "AB12 CD34 …", as the installer prints it and HC360 Desktop shows it.</summary>
    public static string Group(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, fingerprint.Length / 4).Select(i => fingerprint.Substring(i * 4, 4)));
}
