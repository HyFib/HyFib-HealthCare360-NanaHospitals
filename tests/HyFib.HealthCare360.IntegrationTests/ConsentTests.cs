using System.Net;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Patient consents over the real API (EHR-06, DPDP Act 2023): a withdrawal keeps the consent as history with when, by
/// whom and why; a refusal or a withdrawn consent cannot be withdrawn again; and a restricted record's consents stay
/// behind the same guard as the record.
/// </summary>
public sealed class ConsentTests(HospitalServer server)
{
    [Fact]
    public async Task A_withdrawn_consent_stays_in_the_history_with_when_by_whom_and_why()
    {
        var patient = await RegisterPatientAsync(isVip: false);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var path = $"/api/v1/patients/{patient.Id}/consents";

        var given = await frontOffice.PostAsync<Created>(path, new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, "Sister Mary", null, null, null, "Explained in Tamil"));
        var refused = await frontOffice.PostAsync<Created>(path, new CaptureConsentRequest(ConsentType.Research, null, false, null, null, null, null, null));

        await doctor.PostAsync($"/api/v1/patients/consents/{given.Id}/revoke", new RevokeConsentRequest("  Patient asked to stop sharing  "));

        var consents = await doctor.GetAsync<List<ConsentDto>>(path);
        var withdrawn = consents.Single(c => c.Id == given.Id);
        withdrawn.IsActive.ShouldBeFalse();
        withdrawn.Granted.ShouldBeTrue("A withdrawal keeps what the patient once agreed to.");
        withdrawn.RevokedAt.ShouldNotBeNull();
        withdrawn.RevokedReason.ShouldBe("Patient asked to stop sharing");
        withdrawn.CapturedByName.ShouldNotBeNullOrWhiteSpace();
        withdrawn.RevokedByName.ShouldNotBeNullOrWhiteSpace();
        withdrawn.RevokedByName.ShouldNotBe(withdrawn.CapturedByName, "The doctor withdrew what the front office recorded.");
        withdrawn.WitnessName.ShouldBe("Sister Mary");
        consents.Single(c => c.Id == refused.Id).RevokedByName.ShouldBeNull();

        using (var again = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/consents/{given.Id}/revoke", new RevokeConsentRequest("Again")))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("consent.revoked");
        }

        using (var refusal = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/consents/{refused.Id}/revoke", new RevokeConsentRequest("Changed mind")))
        {
            refusal.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refusal.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("consent.not_granted");
        }
    }

    [Fact]
    public async Task A_standing_permission_is_withdrawn_as_a_whole_and_a_procedure_consent_on_its_own()
    {
        var patient = await RegisterPatientAsync(isVip: false);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var path = $"/api/v1/patients/{patient.Id}/consents";
        CaptureConsentRequest Given(ConsentType type, string? notes = null) => new(type, null, true, null, null, null, null, notes);

        // Sharing was agreed twice (a newer form): withdrawing either ends both, so no older copy keeps records shared.
        var firstSharing = await nurse.PostAsync<Created>(path, Given(ConsentType.DataSharing));
        var secondSharing = await nurse.PostAsync<Created>(path, Given(ConsentType.DataSharing));
        // Two operations, two consents: withdrawing one leaves the other.
        var knee = await nurse.PostAsync<Created>(path, Given(ConsentType.HighRiskProcedure, "Right knee replacement"));
        var hip = await nurse.PostAsync<Created>(path, Given(ConsentType.HighRiskProcedure, "Left hip replacement"));
        // Messages were agreed, then refused when asked again: the refusal ends the earlier grant.
        var updates = await nurse.PostAsync<Created>(path, Given(ConsentType.SmsWhatsAppUpdates));
        await nurse.PostAsync<Created>(path, new CaptureConsentRequest(ConsentType.SmsWhatsAppUpdates, null, false, null, null, null, null, null));

        await nurse.PostAsync($"/api/v1/patients/consents/{secondSharing.Id}/revoke", new RevokeConsentRequest("Does not want records shared"));
        await nurse.PostAsync($"/api/v1/patients/consents/{knee.Id}/revoke", new RevokeConsentRequest("Operation postponed by the patient"));

        var consents = (await nurse.GetAsync<List<ConsentDto>>(path)).ToDictionary(c => c.Id);
        consents[firstSharing.Id].IsActive.ShouldBeFalse("A withdrawal of sharing covers every copy.");
        consents[firstSharing.Id].RevokedReason.ShouldBe("Does not want records shared");
        consents[secondSharing.Id].IsActive.ShouldBeFalse();
        consents[knee.Id].IsActive.ShouldBeFalse();
        consents[hip.Id].IsActive.ShouldBeTrue("The hip operation's consent stands.");
        consents[updates.Id].IsActive.ShouldBeFalse();
        consents[updates.Id].RevokedReason.ShouldBe("The patient refused when asked again.");
    }

    [Fact]
    public async Task A_restricted_records_consents_stay_behind_the_same_guard_as_the_record()
    {
        var patient = await RegisterPatientAsync(isVip: true);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var path = $"/api/v1/patients/{patient.Id}/consents";

        using (var list = await nurse.SendRawAsync(HttpMethod.Get, path))
        {
            list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("patient.restricted");
        }

        using (var capture = await nurse.SendRawAsync(HttpMethod.Post, path, new CaptureConsentRequest(ConsentType.Telehealth, null, true, null, null, null, null, null)))
        {
            capture.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The hospital administrator may open restricted records, and records the consent there.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var given = await admin.PostAsync<Created>(path, new CaptureConsentRequest(ConsentType.Telehealth, null, true, null, null, null, null, null));

        using (var revoke = await nurse.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/consents/{given.Id}/revoke", new RevokeConsentRequest("Asked to stop")))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Withdrawing needs the same access as reading.");
        }

        // Registration also records the SMS/WhatsApp updates consent, so the list holds more than this one.
        var consents = await admin.GetAsync<List<ConsentDto>>(path);
        consents.Single(c => c.Id == given.Id).IsActive.ShouldBeTrue("The nurse's withdrawal was refused, so the consent stands.");
    }

    [Fact]
    public async Task An_unknown_patient_has_no_consents()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        using var response = await frontOffice.SendRawAsync(HttpMethod.Get, $"/api/v1/patients/{Guid.CreateVersion7()}/consents");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<PatientCreated> RegisterPatientAsync(bool isVip)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: TestPatients.FirstName("Meena"), MiddleName: null, LastName: "Consent" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: isVip, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);
}
