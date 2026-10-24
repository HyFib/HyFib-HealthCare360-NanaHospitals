using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Breaches;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A breach that is also a cyber security incident CERT-In lists is reported to CERT-In within 6 hours of the hospital
/// noticing it (Directions of 28 April 2022): the register shows the deadline beside the Board's 72 hours, says when it
/// has passed, and records when CERT-In was told.
/// </summary>
public sealed class BreachCertInTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_ransomware_attack_is_due_at_CERT_In_six_hours_after_it_was_noticed_and_is_recorded_as_reported()
    {
        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var aware = DateTimeOffset.UtcNow.AddHours(-7);
        var created = await officer.PostAsync<BreachCreated>("/api/v1/privacy/breaches", Breach(aware, CertInIncidentType.MaliciousCode));

        var overdue = await officer.GetAsync<BreachDetail>($"/api/v1/privacy/breaches/{created.Id}");
        overdue.CertInType.ShouldBe(CertInIncidentType.MaliciousCode);
        overdue.CertInReportDueAt.ShouldNotBeNull().ShouldBe(aware.AddHours(6), TimeSpan.FromMilliseconds(1), "PostgreSQL keeps microseconds.");
        overdue.CertInReportOverdue.ShouldBeTrue("Seven hours after it was noticed.");
        overdue.ReportOverdue.ShouldBeFalse("The Board's 72 hours have not passed.");
        (await ListedAsync(officer, created.Id)).CertInReportOverdue.ShouldBeTrue();

        using (var early = await officer.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/breaches/{created.Id}/cert-in-reported",
                   new CertInReportedRequest(aware.AddMinutes(-5), null)))
        {
            early.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "CERT-In cannot have been told before the hospital knew.");
        }

        var reportedAt = aware.AddHours(6.5);
        await officer.PostAsync($"/api/v1/privacy/breaches/{created.Id}/cert-in-reported", new CertInReportedRequest(reportedAt, "CERTIn-2026-1234"));
        var reported = await officer.GetAsync<BreachDetail>($"/api/v1/privacy/breaches/{created.Id}");
        reported.CertInReportedAt.ShouldNotBeNull().ShouldBe(reportedAt, TimeSpan.FromMilliseconds(1));
        reported.CertInReference.ShouldBe("CERTIn-2026-1234");
        reported.CertInReportOverdue.ShouldBeFalse();

        using (var again = await officer.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/breaches/{created.Id}/cert-in-reported",
                   new CertInReportedRequest(reportedAt, null)))
        {
            (await CodeAsync(again)).ShouldBe("privacy.breach_cert_in_reported");
        }

        // What CERT-In was told it was stays, whatever the record says later.
        await officer.PutAsync($"/api/v1/privacy/breaches/{created.Id}", Breach(aware, CertInIncidentType.DataLeak) with { Version = reported.Version });
        (await officer.GetAsync<BreachDetail>($"/api/v1/privacy/breaches/{created.Id}")).CertInType.ShouldBe(CertInIncidentType.MaliciousCode);
    }

    [Fact]
    public async Task A_breach_that_is_no_cyber_security_incident_has_no_CERT_In_deadline()
    {
        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var created = await officer.PostAsync<BreachCreated>("/api/v1/privacy/breaches", Breach(DateTimeOffset.UtcNow.AddHours(-8), null));

        var detail = await officer.GetAsync<BreachDetail>($"/api/v1/privacy/breaches/{created.Id}");
        detail.CertInReportDueAt.ShouldBeNull();
        detail.CertInReportOverdue.ShouldBeFalse();
        using var refused = await officer.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/breaches/{created.Id}/cert-in-reported",
            new CertInReportedRequest(DateTimeOffset.UtcNow, null));
        (await CodeAsync(refused)).ShouldBe("privacy.breach_not_cert_in");
    }

    private static SaveBreachRequest Breach(DateTimeOffset aware, CertInIncidentType? type) => new(
        "Ransomware on the radiology workstation", "Files on the reporting workstation were encrypted and a ransom note left.", aware, aware.AddHours(-1),
        null, [BreachNature.Availability], "Radiology reporting room", [BreachDataCategory.Health], 40, null, null, null, null, null, null, null, null, type);

    private static async Task<BreachListItem> ListedAsync(ApiClient officer, Guid id) =>
        (await officer.GetAsync<PagedResult<BreachListItem>>("/api/v1/privacy/breaches?openOnly=true&pageSize=200")).Items.Single(b => b.Id == id);

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }
}
