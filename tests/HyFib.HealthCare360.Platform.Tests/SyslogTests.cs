using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Logging;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The syslog collector (CERT-In direction (iv), SRS SEC-10): messages in RFC 5424's format, framed as RFC 5425 has them
/// over TLS, with every personal identifier and secret masked whatever the caller passed in; delivered only to a collector
/// whose certificate chains to the pinned CA; and System health told when it cannot be reached.
/// </summary>
public sealed partial class SyslogTests : IDisposable
{
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 11, 9, 10, 15, 30, 123, TimeSpan.FromHours(5.5)).AddTicks(4560);

    // A made-up JSON Web Token, put together here so that the secret scan does not take the source for one.
    private static readonly string FakeJwt = string.Join('.', "eyJhbGciOiJIUzI1NiJ9", "eyJzdWIiOiIxMjM0NTY3ODkwIn0", "sig");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc360-syslog-" + Guid.NewGuid().ToString("N"));

    public SyslogTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_message_is_RFC_5424_with_its_values_as_structured_data()
    {
        var entry = new SyslogEntry(At, SyslogSeverity.Informational, null, "Bill 42 settled",
            [new("logger", "HyFib.HealthCare360.Billing"), new("correlationId", "c0ffee"), new("empty", null)]);

        var text = Encoding.UTF8.GetString(SyslogFormat.Message(entry, 16, "hc360-srv", "hc360", "4242"));

        // local0 (16) * 8 + informational (6) = 134; the time in UTC to the microsecond; nil MSGID; BOM before the text.
        text.ShouldBe("<134>1 2026-11-09T04:45:30.123456Z hc360-srv hc360 4242 - [hc360@32473 logger=\"HyFib.HealthCare360.Billing\" correlationId=\"c0ffee\"] \uFEFFBill 42 settled");
        Rfc5424().IsMatch(text).ShouldBeTrue(text);
    }

    [Fact]
    public void No_phone_Aadhaar_ABHA_e_mail_or_secret_leaves_in_the_text_or_the_structured_data()
    {
        var entry = new SyslogEntry(At, SyslogSeverity.Warning, "LOG",
            "Patient 9876543210 (Aadhaar 2345 6789 0123, ABHA 12-3456-7890-1234) wrote from ravi.kumar@example.com; password=hunter2 Bearer abcdefghijklmnop",
            [new("caller", "+91 98765 43210"), new("token", "token=" + FakeJwt)]);

        var text = Encoding.UTF8.GetString(SyslogFormat.Message(entry, 16, "hc360-srv", "hc360", "1"));

        foreach (var raw in new[] { "9876543210", "98765 43210", "2345 6789 0123", "12-3456-7890-1234", "ravi.kumar@", "hunter2", "abcdefghijklmnop", "eyJzdWIi" })
        {
            text.ShouldNotContain(raw);
        }

        text.ShouldContain("******3210");
        text.ShouldContain("XXXX-XXXX-0123");
        text.ShouldContain("**-****-****-1234");
        text.ShouldContain("r***@example.com");
        text.ShouldContain("caller=\"******3210\"");
    }

    [Fact]
    public void Structured_data_values_are_escaped_and_header_fields_kept_to_printable_ASCII()
    {
        var entry = new SyslogEntry(At, SyslogSeverity.Error, "A MSGID LONGER THAN THIRTY-TWO CHARACTERS", "x",
            [new("path", "C:\\data\\\"a]b\""), new("bad name=\"]", "v")]);

        var text = Encoding.UTF8.GetString(SyslogFormat.Message(entry, 16, "host name", new string('a', 60), null));

        text.ShouldStartWith("<131>1 2026-11-09T04:45:30.123456Z host_name " + new string('a', 48) + " - A_MSGID_LONGER_THAN_THIRTY-TWO_C ");
        text.ShouldContain("path=\"C:\\\\data\\\\\\\"a\\]b\\\"\"");
        text.ShouldContain("bad_name___=\"v\"");
    }

    [Fact]
    public void A_frame_is_the_message_length_in_bytes_a_space_and_the_message()
    {
        var message = SyslogFormat.Message(new SyslogEntry(At, SyslogSeverity.Notice, null, "नमस्ते"), 16, "h", "hc360", "1");

        var frame = SyslogFormat.Frame(message);

        var prefix = message.Length.ToString(CultureInfo.InvariantCulture) + " ";
        Encoding.ASCII.GetString(frame, 0, prefix.Length).ShouldBe(prefix);
        frame.AsSpan(prefix.Length).SequenceEqual(message).ShouldBeTrue();
        message.Length.ShouldBeGreaterThan(Encoding.UTF8.GetString(message).Length, "Devanagari takes several bytes a character: the length counts bytes");
    }

    [Fact]
    public async Task Messages_reach_a_collector_whose_certificate_chains_to_the_pinned_CA()
    {
        using var ca = Ca("HC360 test syslog CA");
        await using var collector = Collector.Start(Issue(ca, "localhost"));
        var client = new SyslogClient(Options(collector.Port, ca), TimeProvider.System);
        await using (client)
        {
            await client.SendAsync(new SyslogEntry(At, SyslogSeverity.Notice, "AUDIT_ANCHOR", "Anchored at 9876543210"), TestContext.Current.CancellationToken);
            client.Enqueue(new SyslogEntry(At, SyslogSeverity.Error, null, "second"));

            var first = await collector.ReadAsync();
            first.ShouldStartWith("<133>1 2026-11-09T04:45:30.123456Z ");
            first.ShouldContain(" hc360 ");
            first.ShouldContain(" AUDIT_ANCHOR - \uFEFFAnchored at ******3210");
            (await collector.ReadAsync()).ShouldEndWith("\uFEFFsecond");

            client.Status.LastDeliveredAt.ShouldNotBeNull();
            client.Status.Failing.ShouldBeFalse();
            SyslogHealthWarnings.Warnings(client.Status).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_collector_with_a_certificate_from_another_CA_gets_nothing_and_System_health_says_so()
    {
        using var pinned = Ca("HC360 pinned CA");
        using var other = Ca("Someone else's CA");
        await using var collector = Collector.Start(Issue(other, "localhost"));
        await using var client = new SyslogClient(Options(collector.Port, pinned), TimeProvider.System);

        await Should.ThrowAsync<Exception>(() => client.SendAsync(new SyslogEntry(At, SyslogSeverity.Notice, null, "x"), TestContext.Current.CancellationToken));

        client.Status.Failing.ShouldBeTrue();
        client.Status.LastError.ShouldNotBeNull().ShouldContain("TLS handshake");
        var warning = SyslogHealthWarnings.Warnings(client.Status).ShouldHaveSingleItem();
        warning.Code.ShouldBe("logs.syslog_unreachable");
        warning.Message.ShouldContain($"localhost:{collector.Port}");
    }

    [Fact]
    public async Task A_collector_that_does_not_answer_fails_the_send_and_is_reported()
    {
        using var ca = Ca("HC360 test syslog CA");
        var port = FreePort();
        await using var client = new SyslogClient(Options(port, ca), TimeProvider.System);

        await Should.ThrowAsync<Exception>(() => client.SendAsync(new SyslogEntry(At, SyslogSeverity.Notice, null, "x"), TestContext.Current.CancellationToken));

        SyslogHealthWarnings.Warnings(client.Status).ShouldHaveSingleItem().Code.ShouldBe("logs.syslog_unreachable");
    }

    [Fact]
    public void Off_by_default_it_sends_nothing_and_warns_of_nothing()
    {
        var client = new SyslogClient(new SyslogOptions(), TimeProvider.System);
        client.IsEnabled.ShouldBeFalse();
        client.Enqueue(new SyslogEntry(At, SyslogSeverity.Error, null, "x"));
        SyslogHealthWarnings.Warnings(client.Status).ShouldBeEmpty();
        new PlatformOptions().Syslog.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Its_settings_are_checked_when_it_is_on()
    {
        new SyslogOptions { Enabled = true }.Problems().ShouldHaveSingleItem().ShouldContain("Host");
        new SyslogOptions { Enabled = true, Host = "siem", Port = 0, Facility = 30, AppName = "has space", MinimumLevel = "Loud", QueueLimit = 1 }
            .Problems().Count.ShouldBe(5);
        new SyslogOptions { Enabled = true, Host = "siem", MinimumLevel = "Warning" }.Problems().ShouldBeEmpty();

        var options = new PlatformOptions { DataDirectory = _folder };
        options.Audit.AnchorsToSyslog = true;
        PlatformOptionsValidator.OffServerLogProblems(options).ShouldHaveSingleItem().ShouldContain("AnchorsToSyslog");
        options.Audit.AnchorsToSyslog = false;
        options.Audit.AnchorMirrorFile = Path.Combine(_folder, "audit", "anchors.log");
        PlatformOptionsValidator.OffServerLogProblems(options).ShouldHaveSingleItem().ShouldContain("the anchor file itself");
    }

    private SyslogOptions Options(int port, X509Certificate2 ca)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".cer");
        File.WriteAllBytes(path, ca.Export(X509ContentType.Cert));
        return new SyslogOptions { Enabled = true, Host = "localhost", Port = port, CaCertificatePath = path };
    }

    internal static X509Certificate2 Ca(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(ca.Export(X509ContentType.Pfx), null);
    }

    internal static X509Certificate2 Issue(X509Certificate2 ca, string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(8));
        using var withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [GeneratedRegex(@"^<\d{1,3}>1 \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{1,6})?Z [!-~]{1,255} [!-~]{1,48} [!-~]{1,128} [!-~]{1,32} (-|\[[^\]]+\])( \uFEFF.*)?$", RegexOptions.Singleline)]
    private static partial Regex Rfc5424();

    /// <summary>A syslog collector on TLS that keeps each frame it reads (RFC 5425 octet counting).</summary>
    internal sealed class Collector : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly System.Threading.Channels.Channel<string> _frames = System.Threading.Channels.Channel.CreateUnbounded<string>();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;

        private Collector(X509Certificate2 certificate, int port)
        {
            _certificate = certificate;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static Collector Start(X509Certificate2 certificate, int port = 0) => new(certificate, port);

        public async Task<string> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await _frames.Reader.ReadAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _accepting;
            }
            catch (Exception)
            {
            }

            _certificate.Dispose();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var tls = new SslStream(client.GetStream());
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                    while (await ReadFrameAsync(tls) is { } frame)
                    {
                        await _frames.Writer.WriteAsync(frame, _stop.Token);
                    }
                }
                catch (Exception) when (!_stop.IsCancellationRequested)
                {
                    // A client that gave up on the handshake; wait for the next.
                }
            }
        }

        private async Task<string?> ReadFrameAsync(Stream stream)
        {
            var length = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0)
                {
                    return null;
                }

                if (one[0] == (byte)' ')
                {
                    break;
                }

                length.Append((char)one[0]);
            }

            var message = new byte[int.Parse(length.ToString(), CultureInfo.InvariantCulture)];
            await stream.ReadExactlyAsync(message, _stop.Token);
            return Encoding.UTF8.GetString(message);
        }
    }
}
