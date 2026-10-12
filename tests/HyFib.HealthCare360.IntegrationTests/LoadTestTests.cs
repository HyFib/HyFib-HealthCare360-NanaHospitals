using System.Text.Json;
using HyFib.HealthCare360.LoadTest;
using HyFib.HealthCare360.Modules.Patients.Seed;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The load test's pieces against the whole server: the synthetic patients it searches among, every step its virtual
/// users take (docs/testing/LOAD-TESTS.md), and a short run of the load test itself.
/// </summary>
public sealed class LoadTestTests(HospitalServer server)
{
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

    /// <summary>The accounts the load test workflow (.github/workflows/load.yml) shares between its virtual users.</summary>
    [Theory]
    [InlineData(HospitalServer.Users.FrontOffice)]
    [InlineData(HospitalServer.Users.HospitalAdmin)]
    public async Task Each_step_of_the_load_test_is_one_the_account_may_take(string account)
    {
        var ct = TestContext.Current.CancellationToken;
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
            Accounts = [HospitalServer.Users.FrontOffice],
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
}
