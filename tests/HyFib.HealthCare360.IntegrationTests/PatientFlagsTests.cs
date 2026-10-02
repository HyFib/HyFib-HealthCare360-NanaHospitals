using System.Net;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Flags;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The patient banner's safety flags (EHR-03) over the real API: what the ward records reaches the lab and the pharmacy
/// without opening the chart to them, restricted records stay behind the same guard as the chart, and the billing desk
/// sees no clinical flags.
/// </summary>
public sealed class PatientFlagsTests(HospitalServer server)
{
    [Fact]
    public async Task Allergies_and_alerts_recorded_on_the_ward_reach_the_lab_and_the_pharmacy()
    {
        var patient = await RegisterPatientAsync(isVip: false);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var flagsPath = $"/api/v1/emr/patients/{patient.Id}/flags";

        var before = await lab.GetAsync<PatientFlags>(flagsPath);
        before.AllergyStatus.ShouldBe(AllergyRecordingStatus.NotRecorded, "Nothing recorded is not the same as no known allergies.");
        before.Allergies.ShouldBeEmpty();
        before.Alerts.ShouldBeEmpty();

        await nurse.PostAsync<AllergyStatusDto>($"/api/v1/emr/patients/{patient.Id}/allergies/none");
        (await lab.GetAsync<PatientFlags>(flagsPath)).AllergyStatus.ShouldBe(AllergyRecordingStatus.NoKnownAllergies);

        await nurse.PostAsync<AllergyDto>($"/api/v1/emr/patients/{patient.Id}/allergies",
            new RecordAllergyRequest("Peanuts", AllergyType.Food, "Hives", AllergySeverity.Moderate, null, null));
        await nurse.PostAsync<AllergyDto>($"/api/v1/emr/patients/{patient.Id}/allergies",
            new RecordAllergyRequest("Penicillin", AllergyType.Drug, "Throat swelling", AllergySeverity.Anaphylaxis, null, null));
        await nurse.PostAsync<PatientAlertDto>($"/api/v1/emr/patients/{patient.Id}/alerts",
            new RaiseAlertRequest(PatientAlertType.FallRisk, null, null, null, null));
        await nurse.PostAsync<PatientAlertDto>($"/api/v1/emr/patients/{patient.Id}/alerts",
            new RaiseAlertRequest(PatientAlertType.InfectionControl, null, "MRSA", null, null));

        var flags = await lab.GetAsync<PatientFlags>(flagsPath);
        flags.AllergyStatus.ShouldBe(AllergyRecordingStatus.Recorded, "A recorded allergy ends the \"no known allergies\" statement.");
        flags.Allergies.Select(a => a.Allergen).ShouldBe(["Penicillin", "Peanuts"], "The most severe allergy comes first.");
        flags.Allergies[0].Reaction.ShouldBe("Throat swelling");
        flags.Alerts.Select(a => a.Type).ShouldBe([PatientAlertType.InfectionControl, PatientAlertType.FallRisk], "Critical alerts come first.");
        flags.Alerts[0].Detail.ShouldBe("MRSA");

        using (var chart = await lab.SendRawAsync(HttpMethod.Get, $"/api/v1/emr/patients/{patient.Id}/summary"))
        {
            chart.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "The lab sees the flags, not the chart.");
        }

        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        (await pharmacist.GetAsync<PatientFlags>(flagsPath)).Allergies.Count.ShouldBe(2);

        // The doctor's chart summary and the banner flags tell the same story.
        var summary = await (await server.SignInAsync(HospitalServer.Users.Doctor)).GetAsync<PatientClinicalSummary>($"/api/v1/emr/patients/{patient.Id}/summary");
        summary.AllergyStatus.ShouldBe(flags.AllergyStatus);
        summary.Allergies.Select(a => a.Id).ShouldBe(flags.Allergies.Select(a => a.Id));
        summary.Alerts.Select(a => a.Id).ShouldBe(flags.Alerts.Select(a => a.Id));

        (await AccessesAsync(patient.Id, HospitalServer.Users.Lab)).ShouldContain("Viewed allergies and alerts", "Every read of the flags is in the audit trail.");
    }

    [Fact]
    public async Task The_billing_desk_sees_no_clinical_flags()
    {
        var patient = await RegisterPatientAsync(isVip: false);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        using var response = await billing.SendRawAsync(HttpMethod.Get, $"/api/v1/emr/patients/{patient.Id}/flags");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_restricted_record_keeps_its_flags_behind_the_same_guard_as_the_chart()
    {
        var patient = await RegisterPatientAsync(isVip: true);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var flagsPath = $"/api/v1/emr/patients/{patient.Id}/flags";

        using (var refused = await lab.SendRawAsync(HttpMethod.Get, flagsPath))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("patient.restricted");
        }

        // A doctor's audited emergency access opens the flags, as it opens the chart.
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        await doctor.PostAsync($"/api/v1/patients/{patient.Id}/break-glass", new BreakGlassRequest("Unconscious in casualty, needs the allergy history"));
        (await doctor.GetAsync<PatientFlags>(flagsPath)).AllergyStatus.ShouldBe(AllergyRecordingStatus.NotRecorded);
    }

    [Fact]
    public async Task An_unknown_patient_has_no_flags()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);

        using var response = await lab.SendRawAsync(HttpMethod.Get, $"/api/v1/emr/patients/{Guid.CreateVersion7()}/flags");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<PatientCreated> RegisterPatientAsync(bool isVip)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Kiran", MiddleName: null, LastName: "Flags" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: isVip, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    /// <summary>What the audit trail says <paramref name="userName"/> looked at in this patient's record.</summary>
    private async Task<List<string?>> AccessesAsync(Guid patientId, string userName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.UserName == userName && e.Category == AuditCategories.PhiAccess)
            .Select(e => e.Summary)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
