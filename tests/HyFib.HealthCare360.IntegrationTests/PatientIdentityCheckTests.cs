using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Identity;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.WindowsInterop.Biometrics;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Checking a patient's identity with their Aadhaar fingerprint (SRS INT-BIO, optional patient identity verification),
/// through the real API with the training simulator standing in for UIDAI: off until it is set up, never without the
/// patient's consent or with another number than the record's, kept without the number, and linking the number to a
/// record that had none.
/// </summary>
public sealed class PatientIdentityCheckTests(HospitalServer server)
{
    private static readonly string Matching = RdService.TrainingCapture(matches: true);

    [Fact]
    public async Task Is_off_until_aadhaar_authentication_is_set_up_and_then_keeps_nothing()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await RegisterAsync(isVip: false, aadhaar: null);

        var setup = await frontOffice.GetAsync<AadhaarCaptureSetup>("/api/v1/patients/identity-checks/setup");
        using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, Path(patient), new VerifyIdentityRequest(NewAadhaar(), Matching, true));

        // Never set up, or switched off again by an earlier test in this class.
        setup.Provider.ShouldBeNull();
        setup.Problem.ShouldBeOneOf(
            "Aadhaar authentication is not configured. Set it up in Administration → Integrations.",
            "Aadhaar authentication is disabled.");
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("patient.identity_check_failed");
        (await frontOffice.GetAsync<List<IdentityCheckDto>>(Path(patient))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_matching_fingerprint_verifies_the_patient_and_links_the_number_to_a_record_without_one()
    {
        await WithSimulatorAsync(async () =>
        {
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var patient = await RegisterAsync(isVip: false, aadhaar: null);
            var aadhaar = NewAadhaar();

            var setup = await frontOffice.GetAsync<AadhaarCaptureSetup>("/api/v1/patients/identity-checks/setup");
            var check = await frontOffice.PostAsync<IdentityCheckDto>(Path(patient),
                new VerifyIdentityRequest($"{aadhaar[..4]} {aadhaar[4..8]} {aadhaar[8..]}", Matching, true));

            setup.ShouldBe(new AadhaarCaptureSetup("simulator", null, "P", 2, 1, 15, Training: true));
            check.Outcome.ShouldBe(IdentityCheckOutcome.Verified);
            check.Method.ShouldBe(IdentityCheckMethod.AadhaarFingerprint);
            (check.AadhaarLast4, check.LinkedAadhaar, check.UsedVirtualId, check.Provider).ShouldBe((aadhaar[^4..], true, false, "simulator"));
            check.TransactionId.ShouldNotBeNullOrEmpty();
            check.DeviceProviderId.ShouldBe(RdService.TrainingProviderId);
            check.CheckedByName.ShouldNotBeNullOrEmpty();
            (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).AadhaarLast4.ShouldBe(aadhaar[^4..]);
            (await frontOffice.GetAsync<List<IdentityCheckDto>>(Path(patient))).ShouldHaveSingleItem().Id.ShouldBe(check.Id);

            // The integration log shows the check without the number.
            var admin = await server.SignInAsync(HospitalServer.Users.Admin);
            var logged = (await admin.GetAsync<PagedResult<IntegrationLogDto>>("/api/v1/admin/integrations/logs?integration=aadhaar&pageSize=5")).Items.First();
            (logged.Provider, logged.Operation, logged.Success).ShouldBe(("simulator", "authenticate", true));
            logged.Request.ShouldNotBeNull().ShouldContain("****" + aadhaar[^4..]);
            logged.Request.ShouldNotContain(aadhaar);
        });
    }

    [Fact]
    public async Task A_fingerprint_that_does_not_match_is_recorded_and_links_nothing()
    {
        await WithSimulatorAsync(async () =>
        {
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var patient = await RegisterAsync(isVip: false, aadhaar: null);

            var check = await frontOffice.PostAsync<IdentityCheckDto>(Path(patient),
                new VerifyIdentityRequest(NewAadhaar(), RdService.TrainingCapture(matches: false), true));

            check.Outcome.ShouldBe(IdentityCheckOutcome.NotVerified);
            check.ErrorCode.ShouldBe("300");
            check.LinkedAadhaar.ShouldBeFalse();
            (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).AadhaarLast4.ShouldBeNull();
        });
    }

    [Fact]
    public async Task Another_number_than_the_records_is_refused_before_uidai_is_asked()
    {
        await WithSimulatorAsync(async () =>
        {
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var aadhaar = NewAadhaar();
            var patient = await RegisterAsync(isVip: false, aadhaar);

            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, Path(patient), new VerifyIdentityRequest(NewAadhaar(), Matching, true));
            var verified = await frontOffice.PostAsync<IdentityCheckDto>(Path(patient), new VerifyIdentityRequest(aadhaar, Matching, true));

            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var problem = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            problem.ShouldContain("patient.aadhaar_mismatch");
            problem.ShouldContain($"ends in {aadhaar[^4..]}");
            verified.Outcome.ShouldBe(IdentityCheckOutcome.Verified);
            verified.LinkedAadhaar.ShouldBeFalse("The record had the number already.");
            (await frontOffice.GetAsync<List<IdentityCheckDto>>(Path(patient))).ShouldHaveSingleItem();
        });
    }

    [Fact]
    public async Task A_number_linked_to_another_patient_is_refused()
    {
        await WithSimulatorAsync(async () =>
        {
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var aadhaar = NewAadhaar();
            var holder = await RegisterAsync(isVip: false, aadhaar);
            var patient = await RegisterAsync(isVip: false, aadhaar: null);

            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, Path(patient), new VerifyIdentityRequest(aadhaar, Matching, true));

            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(holder.Uhid);
        });
    }

    [Fact]
    public async Task A_virtual_id_is_checked_but_cannot_be_compared_or_linked()
    {
        await WithSimulatorAsync(async () =>
        {
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var patient = await RegisterAsync(isVip: false, NewAadhaar());

            var check = await frontOffice.PostAsync<IdentityCheckDto>(Path(patient), new VerifyIdentityRequest("9134 5678 9012 3456", Matching, true));

            check.Outcome.ShouldBe(IdentityCheckOutcome.Verified);
            (check.UsedVirtualId, check.LinkedAadhaar, check.AadhaarLast4).ShouldBe((true, false, "3456"));
        });
    }

    [Fact]
    public async Task Nothing_is_asked_without_the_patients_consent()
    {
        await WithSimulatorAsync(async () =>
        {
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var patient = await RegisterAsync(isVip: false, aadhaar: null);

            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, Path(patient), new VerifyIdentityRequest(NewAadhaar(), Matching, false));

            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("consent");
            (await frontOffice.GetAsync<List<IdentityCheckDto>>(Path(patient))).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task A_restricted_record_is_checked_only_by_those_who_may_open_it()
    {
        await WithSimulatorAsync(async () =>
        {
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            var patient = await RegisterAsync(isVip: true, aadhaar: null);
            var aadhaar = NewAadhaar();

            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, Path(patient), new VerifyIdentityRequest(aadhaar, Matching, true));
            using var hidden = await frontOffice.SendRawAsync(HttpMethod.Get, Path(patient));
            var check = await admin.PostAsync<IdentityCheckDto>(Path(patient), new VerifyIdentityRequest(aadhaar, Matching, true));

            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            hidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            check.Outcome.ShouldBe(IdentityCheckOutcome.Verified);
        });
    }

    [Fact]
    public async Task Checks_follow_the_patient_into_a_merged_record()
    {
        await WithSimulatorAsync(async () =>
        {
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            var duplicate = await RegisterAsync(isVip: false, aadhaar: null);
            var surviving = await RegisterAsync(isVip: false, aadhaar: null);
            var aadhaar = NewAadhaar();
            var check = await frontOffice.PostAsync<IdentityCheckDto>(Path(duplicate), new VerifyIdentityRequest(aadhaar, Matching, true));

            await admin.PostAsync($"/api/v1/patients/{duplicate.Id}/merge", new MergePatientRequest(surviving.Id, "Registered twice at the counter"));

            (await frontOffice.GetAsync<List<IdentityCheckDto>>(Path(surviving))).ShouldHaveSingleItem().Id.ShouldBe(check.Id);
            (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{surviving.Id}")).AadhaarLast4.ShouldBe(aadhaar[^4..]);
            using var merged = await frontOffice.SendRawAsync(HttpMethod.Post, Path(duplicate), new VerifyIdentityRequest(aadhaar, Matching, true));
            merged.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        });
    }

    [Fact]
    public async Task The_administrators_test_says_what_uidai_needs_and_checks_the_signing_certificate()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        try
        {
            await SaveAsync(admin, "integrations.aadhaar", """{"enabled":true,"provider":"simulator"}""");
            var simulator = await TestAsync(admin);
            var row = (await admin.GetAsync<List<IntegrationStatusDto>>("/api/v1/admin/integrations")).Single(r => r.Name == "aadhaar");

            await SaveAsync(admin, "integrations.aadhaar", """{"enabled":true,"provider":"uidai"}""");
            var empty = await TestAsync(admin);

            await UseUidaiAsync(admin, "CN=Nana Hospitals AUA");
            var ready = await TestAsync(admin);

            simulator.Success.ShouldBeTrue(simulator.Message);
            row.Provider.ShouldBe("simulator");
            (empty.Success, empty.Message).ShouldBe((false, "Enter the ASA's https address for Aadhaar authentication in Administration → Integrations."));
            ready.Success.ShouldBeTrue(ready.Message);
            ready.Message.ShouldContain("Nana Hospitals AUA");
        }
        finally
        {
            await SwitchOffAsync(admin);
        }
    }

    [Fact]
    public async Task Uidai_is_never_sent_a_training_capture_or_a_check_from_a_browser()
    {
        // The ASA's address cannot resolve (.invalid): a check that reached the network would fail differently.
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        try
        {
            await UseUidaiAsync(admin, "CN=Nana Hospitals AUA");
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var patient = await RegisterAsync(isVip: false, aadhaar: null);

            var setup = await frontOffice.GetAsync<AadhaarCaptureSetup>("/api/v1/patients/identity-checks/setup");
            using var training = await frontOffice.SendRawAsync(HttpMethod.Post, Path(patient), new VerifyIdentityRequest(NewAadhaar(), Matching, true));
            using var browser = await frontOffice.SendRawAsync(HttpMethod.Post, Path(patient), new VerifyIdentityRequest(NewAadhaar(), ReaderCapture, true));

            setup.ShouldBe(new AadhaarCaptureSetup("uidai", null, "P", 2, 1, 15, Training: false));
            training.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await training.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("A training capture cannot be sent to UIDAI");
            browser.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await browser.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("only from HC360 Desktop on an enrolled workstation");
            (await frontOffice.GetAsync<List<IdentityCheckDto>>(Path(patient))).ShouldBeEmpty();
        }
        finally
        {
            await SwitchOffAsync(admin);
        }
    }

    // A capture shaped as a Mantra reader's RD service returns it; its encrypted parts are placeholders.
    private const string ReaderCapture =
        """
        <PidData>
          <Resp errCode="0" errInfo="Capture Success" fCount="1" fType="2" nmPoints="38" qScore="74" />
          <DeviceInfo dpId="MANTRA.MSIPL" rdsId="MANTRA.WIN.001" rdsVer="1.0.8" mi="MFS110" mc="MIIEGDCCAwCgAwIBAgIE" dc="b6a5c1f2-30c4-4f8e-9a0e-1f4a7c9e2d11" />
          <Skey ci="20291231">U0tFWS1lbmNyeXB0ZWQtZm9yLVVJREFJ</Skey>
          <Hmac>SE1BQy1lbmNyeXB0ZWQ=</Hmac>
          <Data type="X">UElELWVuY3J5cHRlZC1mb3ItVUlEQUk=</Data>
        </PidData>
        """;

    private static async Task<IntegrationTestResult> TestAsync(ApiClient admin) =>
        await admin.PostAsync<IntegrationTestResult>("/api/v1/admin/integrations/aadhaar/test", new TestIntegrationRequest(null, null, null, null, null));

    /// <summary>Complete UIDAI settings, with a self-signed signing certificate standing in for the AUA's.</summary>
    private static async Task UseUidaiAsync(ApiClient admin, string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pfx = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "pw"));
        await SaveAsync(admin, "integrations.aadhaar",
            """{"enabled":true,"provider":"uidai","authUrl":"https://asa.example.invalid/2.5/{ac}/{uid0}/{uid1}/{asalk}","auaCode":"HC0000001","subAuaCode":"HC0000001"}""");
        await admin.PutAsync("/api/v1/admin/settings/integrations.aadhaar.secret", new SaveSettingRequest(
            JsonNode.Parse($$"""{"licenceKey":"LK-TEST","asaLicenceKey":"ASA-TEST","signingCertificate":"{{pfx}}","signingCertificatePassword":"pw"}"""), null, true, null));
    }

    private static async Task SwitchOffAsync(ApiClient admin)
    {
        await SaveAsync(admin, "integrations.aadhaar", """{"enabled":false,"provider":"simulator"}""");
        await admin.PutAsync("/api/v1/admin/settings/integrations.aadhaar.secret", new SaveSettingRequest(JsonNode.Parse("{}"), null, true, null));
    }

    private static string Path(PatientCreated patient) => $"/api/v1/patients/{patient.Id}/identity-checks";

    private async Task WithSimulatorAsync(Func<Task> test)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await SaveAsync(admin, "integrations.aadhaar", """{"enabled":true,"provider":"simulator"}""");
        try
        {
            await test();
        }
        finally
        {
            await SaveAsync(admin, "integrations.aadhaar", """{"enabled":false,"provider":"simulator"}""");
        }
    }

    private static Task SaveAsync(ApiClient admin, string key, string json) =>
        admin.PutAsync($"/api/v1/admin/settings/{key}", new SaveSettingRequest(JsonNode.Parse(json), null, false, null));

    private async Task<PatientCreated> RegisterAsync(bool isVip, string? aadhaar)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Arjun", MiddleName: null, LastName: "Identity" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(9000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: aadhaar, PhotoFileId: null, IsVip: isVip, Notes: null,
            ConfirmedNotDuplicate: true));
    }

    // A valid Aadhaar number no other test has used (the database outlives a test run): the first digit 2 to 9 and the one
    // Verhoeff check digit that fits.
    private static string NewAadhaar()
    {
        var start = (char)('2' + Random.Shared.Next(8)) + Digits(10);
        return Enumerable.Range(0, 10).Select(d => start + d).Single(AadhaarNumber.IsAadhaar);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
