using System.Text.Json;
using HyFib.HealthCare360.LoadTest;
using HyFib.HealthCare360.Modules.Patients.Seed;
using HyFib.HealthCare360.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

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
        var patients = (await frontOffice.GetAsync<JsonElement>("/api/v1/patients?pageSize=5"))
            .GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
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

    /// <summary>The load test's accounts, with this run's password, as <c>loadtest accounts</c> makes them.</summary>
    private async Task<IReadOnlyList<string>> EnsureAccountsAsync(int count)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<LoadTestAccounts>().EnsureAsync(count, server.Password, TestContext.Current.CancellationToken);
        return result.Value;
    }
}
