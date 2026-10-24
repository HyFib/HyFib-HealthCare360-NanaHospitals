using System.IO.Compression;
using System.Text.Json;
using HyFib.HealthCare360.LoadTest;
using HyFib.HealthCare360.Modules.Patients.Seed;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The load test's pieces against the whole server: the synthetic patients it searches among, the accounts it signs in
/// with, every step its virtual users take (docs/testing/LOAD-TESTS.md), and a short run of the load test itself.
/// </summary>
public sealed class LoadTestTests(HospitalServer server)
{
    [Fact]
    public async Task Load_test_accounts_are_made_once_and_take_the_password_given_each_time()
    {
        var names = await EnsureAccountsAsync(2);

        names.ShouldBe(["loadtest01", "loadtest02"]);
        (await EnsureAccountsAsync(2)).ShouldBe(names, "the second time they are only updated");
        var signedIn = await server.SignInAsync("loadtest02");
        (await signedIn.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("userName").GetString().ShouldBe("loadtest02");
    }

    [Fact]
    public async Task Synthetic_patients_follow_those_added_before_and_are_found_by_their_UHID()
    {
        var ct = TestContext.Current.CancellationToken;
        long first;
        long second;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            first = (await scope.ServiceProvider.GetRequiredService<LoadTestPatients>().AddAsync(30, null, ct)).Value;
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            second = (await scope.ServiceProvider.GetRequiredService<LoadTestPatients>().AddAsync(5, null, ct)).Value;
        }

        second.ShouldBe(first + 5);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var page = await frontOffice.GetAsync<JsonElement>($"/api/v1/patients?search={LoadTestPatients.Uhid(second)}");
        var patient = page.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        patient.GetProperty("uhid").GetString().ShouldBe(LoadTestPatients.Uhid(second));
        var expected = LoadTestPatients.Create(second, Guid.Empty, DateOnly.FromDateTime(DateTime.Today), DateTimeOffset.UtcNow);
        patient.GetProperty("fullName").GetString().ShouldBe(expected.FullName);
    }

    /// <summary>The accounts the load test workflow (.github/workflows/load.yml) shares between its virtual users, and the front office.</summary>
    [Theory]
    [InlineData("loadtest01")]
    [InlineData(HospitalServer.Users.FrontOffice)]
    public async Task Each_step_of_the_load_test_is_one_the_account_may_take(string account)
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureAccountsAsync(1);
        var frontOffice = await server.SignInAsync(account);
        // As the load test does, not VIPs, whose records open only through break-glass (other tests make some).
        var patients = (await frontOffice.GetAsync<JsonElement>("/api/v1/patients?pageSize=50"))
            .GetProperty("items").EnumerateArray().Where(p => !p.GetProperty("isVip").GetBoolean()).Select(p => p.GetProperty("id").GetGuid()).ToList();
        var context = new OperationContext(patients, new Random(5), () => Random.Shared.NextInt64(1, 999_999_999));

        foreach (var operation in Operations.All)
        {
            using var request = operation.Request(context);
            using var response = await frontOffice.Http.SendAsync(request, ct);
            response.IsSuccessStatusCode.ShouldBeTrue($"{operation.Name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        }

        foreach (var (name, path) in Operations.Lists)
        {
            using var response = await frontOffice.Http.GetAsync(new Uri(path, UriKind.Relative), ct);
            response.IsSuccessStatusCode.ShouldBeTrue($"{name}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task A_short_load_test_runs_without_a_failed_request()
    {
        var options = new LoadTestOptions
        {
            Server = new Uri("https://localhost:7360/"),
            Accounts = await EnsureAccountsAsync(2),
            Password = server.Password,
            Users = 3,
            Ramp = TimeSpan.Zero,
            Duration = TimeSpan.FromSeconds(3),
            // Well within one account's share of the server (200 requests in 10 seconds).
            Think = TimeSpan.FromMilliseconds(100),
        };
        using var log = new StringWriter();

        var result = await new LoadRunner(options, _ => server.CreateHandler(), log).RunAsync(TestContext.Current.CancellationToken);

        result.Requests.ShouldBeGreaterThan(20, log.ToString());
        result.Failures.ShouldBe(0, log + Environment.NewLine + Results.Markdown(result));
        result.Lists.ShouldAllBe(l => l.Status == 200);
    }

    /// <summary>
    /// SRS PERF-05 on every pull request: the lists the load test measures, at their largest page, fit in 100 KB as the
    /// server sends them, compressed with Brotli at its fastest level. With 200 synthetic patients the patient list's page
    /// is full; a list with fewer rows is held to the size its rows would take on a full page. This test server is built
    /// without Program.cs's compression, so each page is compressed here as that compression does, and its decision for a
    /// list is checked too. The weekly load test (load.yml) measures the pages the real server sends.
    /// </summary>
    [Fact]
    public async Task The_largest_page_of_each_list_fits_in_100_KB_as_sent()
    {
        var ct = TestContext.Current.CancellationToken;

        // The server's compression (Program.cs), deciding for a list over HTTPS as a workstation asks for it.
        var services = new ServiceCollection().AddLogging().AddHospitalCompression().BuildServiceProvider();
        await using (services)
        {
            var json = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services };
            json.Request.Scheme = "https";
            json.Request.Headers.AcceptEncoding = "br, gzip";
            json.Response.ContentType = "application/json; charset=utf-8";
            var compression = services.GetRequiredService<IResponseCompressionProvider>();
            compression.ShouldCompressResponse(json).ShouldBeTrue();
            compression.GetCompressionProvider(json).ShouldNotBeNull().EncodingName.ShouldBe("br");
            services.GetRequiredService<IOptions<BrotliCompressionProviderOptions>>().Value.Level.ShouldBe(CompressionLevel.Fastest);
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<LoadTestPatients>().AddAsync(ListSize.FullPage, null, ct)).IsSuccess.ShouldBeTrue();
        }

        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        foreach (var (name, path) in Operations.Lists)
        {
            var page = await frontOffice.Http.GetByteArrayAsync(path, ct);
            var rows = LoadRunner.Rows(page, encoding: null);
            using var sent = new MemoryStream();
            await using (var brotli = new BrotliStream(sent, CompressionLevel.Fastest, leaveOpen: true))
            {
                await brotli.WriteAsync(page, ct);
            }

            if (name == "Patients")
            {
                rows.ShouldBe(ListSize.FullPage, "the largest page is full");
            }

            // A page of fewer rows is held to what its rows would take on a full page; a handful of rows says too little.
            if (rows >= 50)
            {
                (sent.Length * ListSize.FullPage / Math.Min(rows, ListSize.FullPage))
                    .ShouldBeLessThanOrEqualTo(ListSize.LimitBytes, $"{name}: {rows} rows, {sent.Length:N0} bytes as sent");
            }
        }
    }

    /// <summary>The load test's accounts, with this run's password, as <c>loadtest accounts</c> makes them.</summary>
    private async Task<IReadOnlyList<string>> EnsureAccountsAsync(int count)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<LoadTestAccounts>().EnsureAsync(count, server.Password, TestContext.Current.CancellationToken);
        return result.Value;
    }
}
