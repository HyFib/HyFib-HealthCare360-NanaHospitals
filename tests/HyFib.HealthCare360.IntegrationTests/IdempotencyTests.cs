using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// DED-08: a state-changing request retried with the same <c>Idempotency-Key</c>, as the apps retry after a response
/// lost on the WiFi, gets the first answer back and changes nothing again. Patient registration stands for every unsafe
/// endpoint: the store sits in front of them all (<see cref="IdempotencyMiddleware"/>).
/// </summary>
public sealed class IdempotencyTests(HospitalServer server)
{
    private const string Path = "/api/v1/patients";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_retried_registration_returns_the_first_answer_and_registers_no_one_again()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var key = Guid.NewGuid().ToString();
        var request = Registration("Retry" + Letters(8));

        using var first = await SendAsync(frontOffice, request, key);
        using var retry = await SendAsync(frontOffice, request, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(first.StatusCode);
        retry.Headers.Location.ShouldBe(first.Headers.Location);
        var created = await first.Content.ReadFromJsonAsync<PatientCreated>(ApiClient.Json, Ct);
        (await retry.Content.ReadFromJsonAsync<PatientCreated>(ApiClient.Json, Ct)).ShouldBe(created);
        (await CountAsync(request.LastName)).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_key_with_another_request_is_refused_and_changes_nothing()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var key = Guid.NewGuid().ToString();
        var request = Registration("Reuse" + Letters(8));
        var other = Registration("Other" + Letters(8));

        using var first = await SendAsync(frontOffice, request, key);
        using var reused = await SendAsync(frontOffice, other, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CountAsync(other.LastName)).ShouldBe(0);
    }

    private static async Task<HttpResponseMessage> SendAsync(ApiClient client, RegisterPatientRequest request, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(request, options: ApiClient.Json) };
        message.Headers.Add(IdempotencyMiddleware.HeaderName, key);
        return await client.Http.SendAsync(message, Ct);
    }

    private async Task<int> CountAsync(string? lastName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>()
            .IgnoreQueryFilters()
            .CountAsync(p => p.LastName == lastName, Ct);
    }

    // A name, number and birth date of its own: another test's patient with the same ones would be taken as a duplicate.
    private static RegisterPatientRequest Registration(string lastName) => new(
        Title: "Mr.", FirstName: "Ravi", MiddleName: null, LastName: lastName, Gender: Gender.Male,
        DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 40)), AgeYears: null, AgeMonths: null,
        Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture), AlternatePhone: null, Email: null,
        Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
        EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
        PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false,
        Notes: null, ConfirmedNotDuplicate: true);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
