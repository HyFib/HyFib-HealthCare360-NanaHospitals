using System.IO.Ports;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;

namespace HyFib.HealthCare360.LabAgent.Tests;

/// <summary>The agent's pieces that need no server: its wire contract, configuration, CA pinning, port mapping and plans.</summary>
public sealed class AgentTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly SerialSettings Com3 = new("COM3", 9600, 8, "None", "One", "None", true, true);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc360-agent-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Calls_the_endpoints_the_server_serves_with_the_same_credential_headers()
    {
        ("/" + AgentProtocol.HeartbeatPath).ShouldBe(LabAgentProtocol.HeartbeatPath);
        ("/" + AgentProtocol.RelayPath).ShouldBe(LabAgentProtocol.RelayPath);
        ("/" + AgentProtocol.RelayPathFor("XN550")).ShouldBe(LabAgentProtocol.RelayPathFor("XN550"));
        AgentProtocol.DeviceIdHeader.ShouldBe(LabAgentProtocol.DeviceIdHeader);
        AgentProtocol.DeviceKeyHeader.ShouldBe(LabAgentProtocol.DeviceKeyHeader);
        AgentProtocol.Subprotocol.ShouldBe(LabAgentProtocol.Subprotocol);
    }

    [Fact]
    public void Reads_the_servers_assignments_and_writes_reports_the_server_reads()
    {
        var deviceId = Guid.CreateVersion7();
        var sent = new LabAgentAssignments(deviceId, "Lab PC 1", true, 60,
            [new LabAgentAssignment("XN550", "Haematology analyser", "Astm", new LabAgentSerialPort("COM3", 9600, 7, "Even", "Two", "XOnXOff", true, false))]);

        var received = JsonSerializer.Deserialize<Assignments>(JsonSerializer.Serialize(sent, Web), Web)!;
        var report = JsonSerializer.Deserialize<LabAgentHeartbeatRequest>(
            JsonSerializer.Serialize(new HeartbeatRequest("1.0.0", [new PortReport("XN550", false, "Access to the port 'COM3' is denied.")]), Web), Web)!;

        received.DeviceId.ShouldBe(deviceId);
        received.HeartbeatSeconds.ShouldBe(60);
        received.Instruments.ShouldHaveSingleItem().Port.ShouldBe(new SerialSettings("COM3", 9600, 7, "Even", "Two", "XOnXOff", true, false));
        report.Ports.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new LabAgentPortReport("XN550", false, "Access to the port 'COM3' is denied."));
    }

    [Fact]
    public void Keeps_the_device_secret_protected_for_this_pc()
    {
        var path = Path.Combine(_folder, "agent.json");
        var configuration = new AgentConfiguration
        {
            ServerUrl = "https://hc360-srv:7360/",
            CaCertificatePem = "-----BEGIN CERTIFICATE-----",
            DeviceId = Guid.CreateVersion7(),
            DeviceSecret = "s3cr3t-device-key",
        };

        configuration.Save(path);
        var loaded = AgentConfiguration.Load(path);

        File.ReadAllText(path).ShouldNotContain("s3cr3t-device-key");
        loaded.DeviceSecret.ShouldBe("s3cr3t-device-key");
        loaded.IsEnrolled.ShouldBeTrue();
        loaded.ServerUri.ShouldBe(new Uri("https://hc360-srv:7360/"));
    }

    [Fact]
    public void Refuses_a_plain_http_server_and_treats_an_unreadable_file_as_not_enrolled()
    {
        new AgentConfiguration { ServerUrl = "http://hc360-srv:7360/" }.ServerUri.ShouldBeNull();
        new AgentConfiguration { ProtectedDeviceSecret = "not-dpapi" }.DeviceSecret.ShouldBeNull();

        var path = Path.Combine(_folder, "broken.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{ not json");
        AgentConfiguration.Load(path).IsEnrolled.ShouldBeFalse();
        AgentConfiguration.Load(Path.Combine(_folder, "missing.json")).IsEnrolled.ShouldBeFalse();
    }

    [Fact]
    public void Trusts_only_the_hospital_ca_for_the_host_it_names()
    {
        using var ca = CreateCa("CN=Nana Hospitals CA");
        using var otherCa = CreateCa("CN=Somebody else");
        using var server = CreateLeaf(ca, "hc360-srv");
        using var impostor = CreateLeaf(otherCa, "hc360-srv");
        using var trust = new ServerTrust(ca.ExportCertificatePem());

        trust.IsTrusted(server, "hc360-srv").ShouldBeTrue();
        trust.IsTrusted(server, "other-host").ShouldBeFalse();
        trust.IsTrusted(impostor, "hc360-srv").ShouldBeFalse();
        trust.Validate(null, null, "hc360-srv").ShouldBeFalse();
        trust.Fingerprint.ShouldBe(Convert.ToHexString(SHA256.HashData(ca.RawData)));
    }

    [Fact]
    public void Trusts_a_server_certificate_from_an_intermediate_below_the_pinned_ca_when_the_server_sends_it()
    {
        using var ca = CreateCa("CN=Nana Hospitals CA");
        using var intermediate = CreateIntermediate(ca, "CN=Nana Hospitals LAN CA");
        using var server = CreateLeaf(intermediate, "hc360-srv");
        using var trust = new ServerTrust(ca.ExportCertificatePem());

        trust.IsTrusted(server, "hc360-srv", [intermediate]).ShouldBeTrue();
        trust.IsTrusted(server, "hc360-srv").ShouldBeFalse("the chain cannot be built without the intermediate");
        trust.IsTrusted(server, "other-host", [intermediate]).ShouldBeFalse();
    }

    [Fact]
    public void Trusts_an_intermediate_the_server_sent_and_not_one_only_this_computer_found()
    {
        using var ca = CreateCa("CN=Nana Hospitals CA");
        using var intermediate = CreateIntermediate(ca, "CN=Nana Hospitals LAN CA");
        using var server = CreateLeaf(intermediate, "hc360-srv");
        using var trust = new ServerTrust(ca.ExportCertificatePem());
        using var handshake = new X509Chain();
        handshake.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        handshake.ChainPolicy.CustomTrustStore.Add(ca);
        handshake.ChainPolicy.ExtraStore.Add(intermediate);
        handshake.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        handshake.Build(server);

        trust.Validate(server, handshake, "hc360-srv").ShouldBeTrue("the server sent it: the handshake's extra store holds it");

        // Not sent, only among the elements of the chain the platform built (on Windows, from this computer's own stores):
        // whether the server is trusted must not depend on what happens to be on this computer.
        handshake.ChainPolicy.ExtraStore.Clear();
        ServerTrust.Intermediates(handshake, server).ShouldBeEmpty();
        trust.Validate(server, handshake, "hc360-srv").ShouldBeFalse();
        trust.IsTrusted(server, "hc360-srv", [intermediate]).ShouldBeTrue();
    }

    [Fact]
    public void Refuses_a_certificate_that_is_not_for_a_tls_server()
    {
        using var ca = CreateCa("CN=Nana Hospitals CA");
        using var clientOnly = CreateLeaf(ca, "hc360-srv", usage: "1.3.6.1.5.5.7.3.2");
        using var serverAuth = CreateLeaf(ca, "hc360-srv", usage: "1.3.6.1.5.5.7.3.1");
        using var trust = new ServerTrust(ca.ExportCertificatePem());

        trust.IsTrusted(clientOnly, "hc360-srv").ShouldBeFalse();
        trust.IsTrusted(serverAuth, "hc360-srv").ShouldBeTrue();
    }

    [Theory]
    [InlineData("ab12 cd34 ef56 7890 ab12 cd34 ef56 7890 ab12 cd34 ef56 7890 ab12 cd34 ef56 7890", true)]
    [InlineData("AB:12:CD:34:EF:56:78:90:AB:12:CD:34:EF:56:78:90:AB:12:CD:34:EF:56:78:90:AB:12:CD:34:EF:56:78:90", true)]
    [InlineData("AB12CD34", false)]
    [InlineData("ZZ12CD34EF567890AB12CD34EF567890AB12CD34EF567890AB12CD34EF567890", false)]
    [InlineData(null, false)]
    public void Reads_a_fingerprint_however_it_was_typed(string? typed, bool valid) =>
        (ServerTrust.NormalizeFingerprint(typed) is not null).ShouldBe(valid);

    [Fact]
    public void Waits_longer_after_each_failure_up_to_the_ceiling()
    {
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));

        var waits = Enumerable.Range(0, 6).Select(_ => backoff.Next()).ToList();

        waits[0].ShouldBeInRange(TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(1));
        waits[1].ShouldBeInRange(TimeSpan.FromMilliseconds(1600), TimeSpan.FromSeconds(2));
        waits[4].ShouldBeInRange(TimeSpan.FromMilliseconds(6400), TimeSpan.FromSeconds(8));
        waits[5].ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(8));
        backoff.Reset();
        backoff.Next().ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Opens_the_port_with_the_settings_the_server_gave()
    {
        using var port = SerialPortLineFactory.Configure(new SerialSettings("com7", 19200, 7, "Even", "Two", "XOnXOff", true, false));

        port.PortName.ShouldBe("COM7");
        port.BaudRate.ShouldBe(19200);
        port.DataBits.ShouldBe(7);
        port.Parity.ShouldBe(Parity.Even);
        port.StopBits.ShouldBe(StopBits.Two);
        port.Handshake.ShouldBe(Handshake.XOnXOff);
        port.DtrEnable.ShouldBeTrue();
        port.RtsEnable.ShouldBeFalse();
    }

    [Fact]
    public void Leaves_rts_to_the_driver_with_an_rts_handshake_and_opens_only_com_ports()
    {
        using var port = SerialPortLineFactory.Configure(Com3 with { Handshake = "RequestToSend" });

        port.Handshake.ShouldBe(Handshake.RequestToSend);
        Should.Throw<ArgumentException>(() => SerialPortLineFactory.Configure(Com3 with { PortName = @"\\.\PhysicalDrive0" }));
        Should.Throw<ArgumentException>(() => SerialPortLineFactory.Configure(Com3 with { PortName = "LPT1" }));
    }

    [Fact]
    public void Starts_new_and_changed_relays_and_stops_removed_ones_leaving_the_rest_alone()
    {
        var running = new Dictionary<string, Assignment>(StringComparer.OrdinalIgnoreCase)
        {
            ["XN550"] = new("XN550", "Haematology", "Astm", Com3),
            ["EM360"] = new("EM360", "Chemistry", "Astm", Com3 with { PortName = "COM4" }),
            ["OLD1"] = new("OLD1", "Retired", "Astm", Com3 with { PortName = "COM5" }),
        };
        Assignment[] assigned =
        [
            new("XN550", "Haematology", "Astm", Com3),
            new("EM360", "Chemistry", "Astm", Com3 with { PortName = "COM4", BaudRate = 19200 }),
            new("CL900", "Immunoassay", "Hl7v2", Com3 with { PortName = "COM6" }),
        ];

        var (start, stop) = RelayPlan.Diff(running, assigned);

        stop.ShouldBe(["EM360", "OLD1"], ignoreOrder: true);
        start.Select(a => a.Code).ShouldBe(["EM360", "CL900"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("--code ABCD-EFGH --ca-fingerprint AB", "--server")]
    [InlineData("--server http://hc360-srv:7360/ --code ABCD-EFGH", "--server")]
    [InlineData("--server https://hc360-srv:7360/ --code ABC", "8-character")]
    [InlineData("--server https://hc360-srv:7360/ --code ABCD-EFGH --ca-fingerprint AB12", "fingerprint")]
    public async Task Enrolment_refuses_incomplete_instructions_before_calling_the_server(string arguments, string message)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await EnrolCommand.RunAsync(arguments.Split(' '), output, error, TestContext.Current.CancellationToken);

        exit.ShouldBe(1);
        error.ToString().ShouldContain(message);
        output.ToString().ShouldBeEmpty();
    }

    private static X509Certificate2 CreateCa(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 CreateIntermediate(X509Certificate2 issuer, string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(9), serial);
        return issued.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string host, string? usage = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        if (usage is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], critical: false));
        }

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: false));
        using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), RandomNumberGenerator.GetBytes(8));
        return issued.CopyWithPrivateKey(key);
    }
}
