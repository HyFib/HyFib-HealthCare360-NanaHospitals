using System.IO.Compression;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace HyFib.HealthCare360.LoadTest.Tests;

public sealed class OptionsTests
{
    private static readonly Func<string, string?> WithPassword = name => name == LoadTestOptions.PasswordVariable ? "Load-Test-Pass-1" : null;

    [Fact]
    public void A_full_command_is_read_with_its_defaults()
    {
        var (options, problem) = LoadTestOptions.Parse(
            ["run", "--server", "https://hc360-srv:7360/", "--accounts", "frontoffice, doctor", "--test-server", "--users", "40", "--ramp", "90s", "--targets", "report"],
            WithPassword);

        problem.ShouldBeNull();
        options.ShouldNotBeNull();
        options.Server.ShouldBe(new Uri("https://hc360-srv:7360/"));
        options.Accounts.ShouldBe(["frontoffice", "doctor"]);
        options.Password.ShouldBe("Load-Test-Pass-1");
        options.Users.ShouldBe(40);
        options.Ramp.ShouldBe(TimeSpan.FromSeconds(90));
        options.Duration.ShouldBe(TimeSpan.FromMinutes(5), "the default");
        options.Think.ShouldBe(TimeSpan.FromSeconds(3), "the default");
        options.Targets.ShouldBe(TargetMode.Report);
    }

    [Fact]
    public void Payload_budgets_alone_can_be_enforced() =>
        LoadTestOptions.Parse(["run", "--server", "https://hc360-srv:7360/", "--accounts", "loadtest01", "--test-server", "--targets", "payloads"], WithPassword)
            .Options.ShouldNotBeNull().Targets.ShouldBe(TargetMode.Payloads);

    [Theory]
    [InlineData("--server https://hc360-srv:7360/ --accounts admin", "--test-server")]
    [InlineData("--server http://hc360-srv:7360/ --accounts admin --test-server", "https://")]
    [InlineData("--server https://hc360-srv:7360/ --test-server", "--accounts")]
    [InlineData("--server https://hc360-srv:7360/ --accounts admin --test-server --users many", "--users many")]
    [InlineData("--server https://hc360-srv:7360/ --accounts admin --test-server --password secret", "--password secret")]
    public void A_command_that_cannot_run_says_why(string arguments, string expected)
    {
        var (options, problem) = LoadTestOptions.Parse(["run", .. arguments.Split(' ')], WithPassword);

        options.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldContain(expected);
    }

    [Fact]
    public void The_password_comes_only_from_the_environment()
    {
        var (_, problem) = LoadTestOptions.Parse(["run", "--server", "https://hc360-srv:7360/", "--accounts", "admin", "--test-server"], _ => null);

        problem.ShouldNotBeNull().ShouldContain(LoadTestOptions.PasswordVariable);
    }

    [Theory]
    [InlineData("90s", 90_000)]
    [InlineData("5m", 300_000)]
    [InlineData("1.5h", 5_400_000)]
    [InlineData("250ms", 250)]
    public void Durations_are_read_in_their_units(string text, double milliseconds) =>
        LoadTestOptions.ParseDuration(text).ShouldBe(TimeSpan.FromMilliseconds(milliseconds));

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("5x")]
    [InlineData("minutes")]
    public void Anything_else_is_not_a_duration(string text) => LoadTestOptions.ParseDuration(text).ShouldBeNull();
}

public sealed class ResultsTests
{
    [Fact]
    public void Percentiles_are_by_nearest_rank()
    {
        double[] times = [.. Enumerable.Range(1, 100).Select(i => (double)i)];

        Results.Percentile(times, 50).ShouldBe(50);
        Results.Percentile(times, 95).ShouldBe(95);
        Results.Percentile(times, 99).ShouldBe(99);
        Results.Percentile(times, 100).ShouldBe(100);
        Results.Percentile([], 95).ShouldBe(0);
        Results.Percentile([42], 95).ShouldBe(42);
    }

    [Fact]
    public void Each_operation_is_held_to_its_kind_of_target_and_failures_do_not_count_as_fast()
    {
        Operation[] operations =
        [
            new("Search", OperationKind.Search, 1, _ => new HttpRequestMessage()),
            new("Read", OperationKind.Read, 1, _ => new HttpRequestMessage()),
            new("Write", OperationKind.Write, 1, _ => new HttpRequestMessage()),
        ];
        List<Sample> samples =
        [
            .. Enumerable.Range(1, 20).Select(i => new Sample("Search", OperationKind.Search, TimeSpan.FromMilliseconds(i * 20), 200)),
            .. Enumerable.Range(1, 20).Select(i => new Sample("Read", OperationKind.Read, TimeSpan.FromMilliseconds(i * 20), 200)),
            new("Write", OperationKind.Write, TimeSpan.FromMilliseconds(5), 500),
        ];

        var results = Results.Summarize(operations, samples);

        results[0].P95.ShouldBe(380);
        results[0].TargetMs.ShouldBe(500);
        results[0].Met.ShouldBeTrue("search within 500 ms");
        results[1].Met.ShouldBeFalse("a read is held to 300 ms");
        results[2].Count.ShouldBe(1);
        results[2].Failures.ShouldBe(1);
        results[2].Met.ShouldBeFalse("an operation that never succeeded has no time to meet its target");
    }

    [Theory]
    [InlineData(80 * 1024, "br", 200, true)]
    [InlineData(80 * 1024, "gzip", 200, true)]
    [InlineData(80 * 1024, null, 200, false)]
    [InlineData(120 * 1024, "br", 200, false)]
    [InlineData(10, "br", 403, false)]
    public void A_list_must_be_sent_compressed_and_under_100_KB(long bytes, string? encoding, int status, bool met) =>
        new ListSize("Patients", bytes, encoding, status).Met.ShouldBe(met);

    [Fact]
    public void The_report_names_every_operation_and_list_and_what_was_missed()
    {
        var result = new LoadTestResult(
            new Uri("https://hc360-srv:7360/"), 75, TimeSpan.FromMinutes(5),
            [new("Search patients by name", OperationKind.Search, 1000, 2, 120, 640, 900, 1200, 500)],
            [new ListSize("Visits", 40 * 1024, "br", 200)],
            Requests: 1000, Failures: 2, new Dictionary<int, int> { [500] = 2 });

        var report = Results.Markdown(result);

        report.ShouldContain("| Search patients by name | 500 ms (PERF-02) | 1,000 | 2 | 120 | 640 | 900 | 1,200 | **no** |");
        report.ShouldContain("| Visits | 0 | 40.0 KB | br | yes |");
        report.ShouldContain("500: 2");
        report.ShouldContain("Some SRS 7.3 targets are missed");
        result.FewFailures.ShouldBeTrue("0.2% failed");
        result.TargetsMet.ShouldBeFalse();
    }

    [Theory]
    [InlineData("enforce", 640, 40, true, false)]
    [InlineData("report", 640, 120, true, true)]
    [InlineData("payloads", 640, 40, true, true)]
    [InlineData("payloads", 200, 120, true, false)]
    [InlineData("payloads", 200, 40, false, false)]
    [InlineData("enforce", 200, 40, true, true)]
    public void A_run_passes_on_the_targets_it_holds_to(string targets, double searchP95, long listKilobytes, bool fewFailures, bool passed)
    {
        var mode = Enum.Parse<TargetMode>(targets, ignoreCase: true);
        // CI's runners hold a run to its pages' sizes (PERF-05), the same on any machine, and only report its timings.
        var result = new LoadTestResult(
            new Uri("https://hc360-srv:7360/"), 75, TimeSpan.FromMinutes(5),
            [new("Search patients by name", OperationKind.Search, 1000, 0, 120, searchP95, 900, 1200, 500)],
            [new ListSize("Patients", listKilobytes * 1024, "br", 200)],
            Requests: 1000, Failures: fewFailures ? 0 : 50, new Dictionary<int, int>());

        result.Passed(mode).ShouldBe(passed);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    [InlineData(null)]
    public void A_pages_rows_are_counted_as_it_was_sent(string? encoding)
    {
        var page = System.Text.Encoding.UTF8.GetBytes("{\"items\":[{\"id\":1},{\"id\":2},{\"id\":3}],\"totalCount\":42}");
        using var sent = new MemoryStream();
        if (encoding is null)
        {
            sent.Write(page);
        }
        else
        {
            using Stream writer = encoding == "br"
                ? new BrotliStream(sent, CompressionLevel.Fastest, leaveOpen: true)
                : new GZipStream(sent, CompressionLevel.Fastest, leaveOpen: true);
            writer.Write(page);
        }

        LoadRunner.Rows(sent.ToArray(), encoding).ShouldBe(3);
    }
}

public sealed class OperationsTests
{
    [Fact]
    public void Operations_are_chosen_by_their_weight()
    {
        Operation[] operations =
        [
            new("Often", OperationKind.Read, 9, _ => new HttpRequestMessage()),
            new("Rarely", OperationKind.Read, 1, _ => new HttpRequestMessage()),
        ];
        var random = new Random(7);

        var often = Enumerable.Range(0, 10_000).Count(_ => Operations.Choose(operations, random).Name == "Often");

        often.ShouldBeInRange(8_700, 9_300);
    }

    [Fact]
    public void Every_operation_and_list_asks_the_staff_API()
    {
        var context = new OperationContext([Guid.NewGuid()], new Random(1), () => 1);

        foreach (var operation in Operations.All)
        {
            using var request = operation.Request(context);
            request.RequestUri.ShouldNotBeNull().OriginalString.ShouldStartWith("api/v1/", Case.Sensitive, operation.Name);
        }

        Operations.Lists.ShouldAllBe(l => l.Path.StartsWith("api/v1/", StringComparison.Ordinal) && l.Path.Contains("pageSize=200", StringComparison.Ordinal));
        Operations.All.Select(o => o.Kind).Distinct().Count().ShouldBe(3, "reads, searches and writes are all measured");
    }

    [Fact]
    public async Task A_registration_is_a_new_patient_with_a_phone_number_of_its_own()
    {
        var numbers = 41L;
        var context = new OperationContext([Guid.NewGuid()], new Random(3), () => Interlocked.Increment(ref numbers));

        using var first = Operations.Register(context);
        using var second = Operations.Register(context);
        var a = await first.Content.ShouldNotBeNull().ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var b = await second.Content.ShouldNotBeNull().ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        first.Method.ShouldBe(HttpMethod.Post);
        a.GetProperty("phone").GetString().ShouldBe("8000000042");
        b.GetProperty("phone").GetString().ShouldBe("8000000043");
        a.GetProperty("confirmedNotDuplicate").GetBoolean().ShouldBeTrue();
        a.GetProperty("ageYears").GetInt32().ShouldBeInRange(1, 89);
    }
}

public sealed class ConnectionsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc360-loadtest-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_hospital_CA_is_read_from_its_certificate_alone_as_the_server_gives_it()
    {
        using var ca = CreateCa("CN=Nana Hospitals HC360 CA");
        var path = WriteCa(ca);

        var factory = Connections.Factory(path, 4);

        using var handler = factory(true).ShouldBeOfType<SocketsHttpHandler>();
        handler.SslOptions.RemoteCertificateValidationCallback.ShouldNotBeNull();
    }

    [Fact]
    public void Only_a_certificate_from_the_hospital_CA_for_the_host_is_trusted()
    {
        using var ca = CreateCa("CN=Nana Hospitals HC360 CA");
        using var other = CreateCa("CN=Another CA");

        Connections.IsTrusted(ca, CreateServerCertificate(ca, "hc360-srv"), null, SslPolicyErrors.RemoteCertificateChainErrors)
            .ShouldBeTrue("Windows does not know the hospital CA; the load test does");
        Connections.IsTrusted(ca, CreateServerCertificate(other, "hc360-srv"), null, SslPolicyErrors.RemoteCertificateChainErrors)
            .ShouldBeFalse("another CA");
        Connections.IsTrusted(ca, CreateServerCertificate(ca, "hc360-srv"), null, SslPolicyErrors.RemoteCertificateNameMismatch)
            .ShouldBeFalse("a certificate for another host");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string WriteCa(X509Certificate2 ca)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "hc360-hospital-ca.crt");
        File.WriteAllText(path, ca.ExportCertificatePem());
        return path;
    }

    private static X509Certificate2 CreateCa(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 CreateServerCertificate(
        X509Certificate2 ca, string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));
        return request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMonths(6), [1, 2, 3, 4]);
    }
}
