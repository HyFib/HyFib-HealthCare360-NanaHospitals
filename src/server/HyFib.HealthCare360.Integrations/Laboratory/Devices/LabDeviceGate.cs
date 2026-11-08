using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Laboratory.Devices;

/// <summary>What the gate decided about a connection, for the log and the status board.</summary>
public enum LabDeviceAdmission
{
    /// <summary>The connection asks for no device authentication (<see cref="LabDeviceAuthentication.None"/>).</summary>
    Open,

    Admitted,

    /// <summary>A device this connection does not know: refused, and listed for the lab to add.</summary>
    Unknown,

    /// <summary>A device added but not yet approved by someone else, or revoked.</summary>
    NotApproved,

    /// <summary>A wrong answer to the challenge, a malformed one, no client certificate, or a failed handshake.</summary>
    Failed,
}

/// <summary>
/// Stands between an analyser connection and the HL7 or ASTM session: before a byte of a message is read, the device
/// proves who it is the way its connection asks (<see cref="LabDeviceAuthentication"/>), and only an approved device of
/// that connection is let through. A device that is not known is refused and recorded for the lab to add, an added one
/// is refused until someone other than who added it approves it, and every refusal is counted on the device's row.
/// </summary>
/// <remarks>
/// The shared-secret exchange, one line each way in ASCII before the protocol starts:
/// <code>
/// gateway → device:  HC360-CHALLENGE &lt;32 hex digits, fresh for each connection&gt;\r\n
/// device → gateway:  HC360-AUTH &lt;key id&gt; &lt;HMAC-SHA256(secret, challenge) as 64 hex digits&gt;\r\n
/// gateway → device:  HC360-OK\r\n   (anything else: the connection is closed)
/// </code>
/// The secret never crosses the network and an answer is good for its own challenge only. Mutual TLS uses the
/// gateway's LAN certificate as the server certificate and requires a client certificate; its SHA-256 fingerprint is the
/// device's identity (the lab may use its own CA: the fingerprint, not the chain, is what is approved).
/// </remarks>
internal sealed class LabDeviceGate(IServiceScopeFactory scopes, Func<X509Certificate2?> serverCertificate, TimeProvider time, ILogger<LabDeviceGate> logger)
{
    public const string ChallengePrefix = "HC360-CHALLENGE ";
    public const string AnswerPrefix = "HC360-AUTH ";
    public const string Accepted = "HC360-OK\r\n";

    /// <summary>How long a device has to answer the challenge or finish the TLS handshake.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

    private const int MaxAnswerLength = 160;

    /// <summary>The stream the session reads, or null when the connection must be closed.</summary>
    public async Task<(Stream? Stream, LabDeviceAdmission Admission)> AdmitAsync(
        LabInstrumentSettings connection, Stream raw, IPAddress? remote, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AnswerTimeout);
        try
        {
            return connection.DeviceAuthentication switch
            {
                LabDeviceAuthentication.SharedSecret => await SharedSecretAsync(connection.Code, raw, remote, timeout.Token),
                LabDeviceAuthentication.MutualTls => await MutualTlsAsync(connection.Code, raw, remote, timeout.Token),
                LabDeviceAuthentication.None => (raw, LabDeviceAdmission.Open),
                _ => (null, LabDeviceAdmission.Failed),
            };
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("A device at {Address} failed to authenticate on analyser connection {Connection}", remote, connection.Code);
            return (null, LabDeviceAdmission.Failed);
        }
    }

    private async Task<(Stream?, LabDeviceAdmission)> SharedSecretAsync(string code, Stream raw, IPAddress? remote, CancellationToken ct)
    {
        var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await raw.WriteAsync(Encoding.ASCII.GetBytes(ChallengePrefix + challenge + "\r\n"), ct);
        var line = await ReadLineAsync(raw, ct);
        var parts = line?.StartsWith(AnswerPrefix, StringComparison.Ordinal) == true ? line[AnswerPrefix.Length..].Split(' ') : [];
        if (parts.Length != 2 || parts[0].Length is 0 or > 64 || parts[1].Length != 64)
        {
            logger.LogWarning("A device at {Address} sent no valid answer to the challenge on analyser connection {Connection}", remote, code);
            return (null, LabDeviceAdmission.Failed);
        }

        var (keyId, answer) = (parts[0], parts[1]);
        var admission = await DecideAsync(code, LabDeviceAuthentication.SharedSecret, keyId, null, remote,
            device => device.SecretCipher is { } secret && SecretTokens.FixedTimeEquals(Answer(secret, challenge), answer.ToUpperInvariant()), ct);
        if (admission != LabDeviceAdmission.Admitted)
        {
            return (null, admission);
        }

        await raw.WriteAsync(Encoding.ASCII.GetBytes(Accepted), ct);
        return (raw, admission);
    }

    /// <summary>
    /// A lab agent, already authenticated by its device key, asking to relay a serial analyser of a connection that asks for
    /// device authentication: admitted only as an approved device of that connection; otherwise refused and listed.
    /// </summary>
    public Task<LabDeviceAdmission> AdmitAgentAsync(string code, Guid agentDeviceId, IPAddress? remote, CancellationToken ct) =>
        DecideAsync(code, LabDeviceAuthentication.LabAgent, agentDeviceId.ToString("N").ToUpperInvariant(), null, remote, _ => true, ct);

    /// <summary>The answer a device with <paramref name="secret"/> gives to <paramref name="challenge"/> (upper-case hex).</summary>
    public static string Answer(string secret, string challenge) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(challenge)));

    private async Task<(Stream?, LabDeviceAdmission)> MutualTlsAsync(string code, Stream raw, IPAddress? remote, CancellationToken ct)
    {
        var certificate = serverCertificate() ?? throw new AuthenticationException("The gateway has no server certificate.");
        // A device's certificate is usually self-signed, so no chain is asked of it: the handshake only needs one presented,
        // and the device is then admitted by its SHA-256 fingerprint against the connection's approved devices (DecideAsync).
        var tls = new SslStream(raw, leaveInnerStreamOpen: false, (_, presented, _, _) => presented is not null);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            // TLS 1.2 is the floor on purpose, as for the server's HTTPS (HospitalHttps): older Windows versions the server may
            // run on still enable TLS 1.0/1.1 by default.
#pragma warning disable CA5398 // Avoid hardcoded SslProtocols values
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
#pragma warning restore CA5398
        }, ct);

        if (tls.RemoteCertificate is not { } presented)
        {
            await tls.DisposeAsync();
            return (null, LabDeviceAdmission.Failed);
        }

        using var client = new X509Certificate2(presented);
        var fingerprint = Convert.ToHexString(SHA256.HashData(client.RawData));
        var admission = await DecideAsync(code, LabDeviceAuthentication.MutualTls, fingerprint, client.Subject, remote, _ => true, ct);
        if (admission != LabDeviceAdmission.Admitted)
        {
            await tls.DisposeAsync();
            return (null, admission);
        }

        return (tls, admission);
    }

    /// <summary>
    /// Looks the device up for the connection and records the attempt: an approved device that proves itself is admitted
    /// (and its last sighting noted); anything else is refused, counted, and a device never seen before is listed.
    /// </summary>
    private async Task<LabDeviceAdmission> DecideAsync(
        string code, LabDeviceAuthentication method, string identifier, string? subject, IPAddress? remote, Func<LabDevice, bool> proves,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = time.GetUtcNow();
        var device = await db.Set<LabDevice>().FirstOrDefaultAsync(d => d.ConnectionCode == code && d.Method == method && d.Identifier == identifier, ct);
        LabDeviceAdmission admission;
        if (device is null)
        {
            admission = LabDeviceAdmission.Unknown;
            if (await db.Set<LabDevice>().CountAsync(d => d.ConnectionCode == code && d.Status == LabDeviceStatus.Seen, ct) < LabDevice.MaxSeenPerConnection)
            {
                db.Add(device = new LabDevice
                {
                    ConnectionCode = code,
                    Method = method,
                    Identifier = identifier,
                    Subject = subject,
                    Status = LabDeviceStatus.Seen,
                    FirstSeenAt = now,
                });
            }
        }
        else if (device.Status != LabDeviceStatus.Approved)
        {
            admission = device.Status == LabDeviceStatus.Seen ? LabDeviceAdmission.Unknown : LabDeviceAdmission.NotApproved;
        }
        else
        {
            admission = proves(device) ? LabDeviceAdmission.Admitted : LabDeviceAdmission.Failed;
        }

        if (device is not null)
        {
            device.LastSeenAt = now;
            device.LastAddress = remote?.ToString();
            if (admission != LabDeviceAdmission.Admitted)
            {
                device.RefusedAttempts++;
            }

            await db.SaveChangesAsync(ct);
        }

        if (admission != LabDeviceAdmission.Admitted)
        {
            logger.LogWarning("Device {Identifier} at {Address} was refused on analyser connection {Connection}: {Admission}",
                identifier[..Math.Min(16, identifier.Length)], remote, code, admission);
        }

        return admission;
    }

    /// <summary>One line of ASCII ending in CR LF (or LF), at most <see cref="MaxAnswerLength"/> characters; null if none.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (line.Length <= MaxAnswerLength)
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                return null;
            }

            if (one[0] == (byte)'\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            line.Append((char)one[0]);
        }

        return null;
    }
}
