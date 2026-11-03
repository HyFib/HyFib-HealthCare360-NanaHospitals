using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Gap #28 through the whole server: a patient's record-sharing consent says which other providers' accounts may read
/// the record through the FHIR API, for what purpose, which kinds of records, from which dates and until when, and the
/// FHIR API returns exactly that. A consent recorded without a scope still shares the whole record, as before.
/// </summary>
public sealed class ScopedRecordSharingTests(HospitalServer server)
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_provider_reads_only_what_the_patient_agreed_to_share_with_them()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (clinic, clinicId, clinicName) = await PartnerAsync();
        var (insurer, insurerId, _) = await PartnerAsync();
        var hospitalAdmin = await (await server.SignInAsync(HospitalServer.Users.HospitalAdmin)).GetAsync<AuthUser>("/api/v1/auth/me");

        // The consent form offers the other providers' accounts, not the hospital administrator, whose built-in role also reads FHIR.
        var partners = (await frontOffice.GetAsync<List<SharingPartner>>("/api/v1/patients/consents/sharing-partners")).Select(p => p.Id).ToList();
        partners.ShouldContain(clinicId);
        partners.ShouldContain(insurerId);
        partners.ShouldNotContain(hospitalAdmin.Id);

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ist).DateTime);
        var patient = await RegisterAsync(frontOffice);
        var records = await RecordsAsync(patient, hospitalAdmin.FacilityId);

        // The clinic, for treatment, may read the allergies recorded in the last 30 days, until a month from now.
        var consentId = (await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, null, null, null, null, "Shared with the clinic for her follow-up",
            new RecordSharingScope([clinicId], [SharingPurpose.Treatment], [SharedRecordKind.Allergies], today.AddDays(-30), null, today.AddDays(30))))).Id;

        // What it covers, as staff see it.
        var shown = (await frontOffice.GetAsync<List<ConsentDto>>($"/api/v1/patients/{patient}/consents")).Single(c => c.Id == consentId).SharingScope.ShouldNotBeNull();
        shown.ShareWith.ShouldHaveSingleItem().Id.ShouldBe(clinicId);
        shown.ShareWith[0].Name.ShouldBe(clinicName);
        shown.Purposes.ShouldBe([SharingPurpose.Treatment]);
        shown.Kinds.ShouldBe([SharedRecordKind.Allergies]);
        shown.RecordsFrom.ShouldBe(today.AddDays(-30));
        shown.RecordsTo.ShouldBeNull();
        shown.Until.ShouldBe(today.AddDays(30));

        // The clinic, for treatment: the patient and this month's allergy.
        (await FhirAsync(clinic, $"Patient/{patient}", "TREAT")).Status.ShouldBe(200);
        (await FhirAsync(clinic, $"AllergyIntolerance/{records.NewAllergy}", "TREAT")).Status.ShouldBe(200);
        var allergies = await FhirAsync(clinic, $"AllergyIntolerance?patient={patient}", "TREAT");
        Matches(allergies.Body).ShouldBe([records.NewAllergy.ToString("D")]);
        Withheld(allergies.Body).ShouldBeTrue("last year's allergy is left out, and the bundle says some records are withheld");

        // Not last year's allergy, not the vital signs, not for another purpose, not without one.
        (await FhirAsync(clinic, $"AllergyIntolerance/{records.OldAllergy}", "TREAT")).Refusal.ShouldBe((403, "consent-required"), "recorded before the first day");
        Matches((await FhirAsync(clinic, $"Observation?patient={patient}", "TREAT")).Body).ShouldBeEmpty("vital signs are not named");
        (await FhirAsync(clinic, $"Patient/{patient}", "HPAYMT")).Refusal.ShouldBe((403, "consent-required"), "the patient agreed to treatment, not payment");
        (await FhirAsync(clinic, $"Patient/{patient}", null)).Refusal.ShouldBe((403, "consent-required"), "a reader who states no purpose cannot be held to one");
        (await FhirAsync(clinic, $"Patient/{patient}", "SNOOP")).Refusal.ShouldBe((400, "invalid-parameter"));

        // Every read, search and refusal is audited with the purpose the reader stated; a refusal says why.
        var audit = await FhirAuditAsync(patient);
        audit.ShouldContain(e => e.Action == "patient.accessed" && e.EntityId == records.NewAllergy.ToString("D") && Text(e.Details, "purpose") == "TREAT");
        audit.ShouldContain(e => e.Action == "patient.accessed" && Text(e.Details, "interaction") == "search"
            && Text(e.Details, "resourceType") == "AllergyIntolerance" && Text(e.Details, "purpose") == "TREAT" && Number(e.Details, "withheld") == 1);
        audit.ShouldContain(e => e.Action == "fhir.access_denied" && e.EntityId == records.OldAllergy.ToString("D")
            && Text(e.Details, "reason") == "outside-consent" && Text(e.Details, "purpose") == "TREAT");
        audit.ShouldContain(e => e.Action == "fhir.access_denied" && Text(e.Details, "resourceType") == "Patient"
            && Text(e.Details, "reason") == "consent-required" && Text(e.Details, "purpose") == "HPAYMT");
        audit.ShouldContain(e => e.Action == "fhir.access_denied" && Text(e.Details, "resourceType") == "Patient" && Text(e.Details, "purpose") == "not stated");

        // The insurer is not named: nothing, and the patient is not found by a search.
        (await FhirAsync(insurer, $"Patient/{patient}", "TREAT")).Refusal.ShouldBe((403, "consent-required"));
        Matches((await FhirAsync(insurer, $"Patient?_id={patient}", "TREAT")).Body).ShouldBeEmpty();
        Matches((await FhirAsync(clinic, $"Patient?_id={patient}", "TREAT")).Body).ShouldBe([patient.ToString("D")]);

        // Once its last day has passed, the consent lets nobody read (the day moved back here, as no test waits a month).
        await SetUntilAsync(consentId, today.AddDays(-1));
        (await FhirAsync(clinic, $"Patient/{patient}", "TREAT")).Refusal.ShouldBe((403, "consent-required"));
        Matches((await FhirAsync(clinic, $"Patient?_id={patient}", "TREAT")).Body).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_consent_without_a_scope_shares_the_whole_record_until_it_is_withdrawn()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (insurer, _, _) = await PartnerAsync();
        var hospitalAdmin = await (await server.SignInAsync(HospitalServer.Users.HospitalAdmin)).GetAsync<AuthUser>("/api/v1/auth/me");
        var patient = await RegisterAsync(frontOffice);
        var records = await RecordsAsync(patient, hospitalAdmin.FacilityId);
        var consentId = (await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents",
            new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null))).Id;

        (await frontOffice.GetAsync<List<ConsentDto>>($"/api/v1/patients/{patient}/consents")).Single(c => c.Id == consentId).SharingScope.ShouldBeNull();
        (await FhirAsync(insurer, $"Patient/{patient}", null)).Status.ShouldBe(200);
        var allergies = await FhirAsync(insurer, $"AllergyIntolerance?patient={patient}", "HPAYMT");
        Matches(allergies.Body).Count.ShouldBe(2);
        Withheld(allergies.Body).ShouldBeFalse();
        Matches((await FhirAsync(insurer, $"Observation?patient={patient}", null)).Body).ShouldNotBeEmpty();
        (await FhirAsync(insurer, $"AllergyIntolerance/{records.OldAllergy}", null)).Status.ShouldBe(200);

        await frontOffice.PostAsync($"/api/v1/patients/consents/{consentId}/revoke", new RevokeConsentRequest("The patient stopped sharing"));
        (await FhirAsync(insurer, $"Patient/{patient}", null)).Refusal.ShouldBe((403, "consent-required"));
    }

    [Fact]
    public async Task A_scope_is_recorded_only_as_the_patient_can_mean_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (_, clinicId, _) = await PartnerAsync();
        var hospitalAdmin = await (await server.SignInAsync(HospitalServer.Users.HospitalAdmin)).GetAsync<AuthUser>("/api/v1/auth/me");
        var patient = await RegisterAsync(frontOffice);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ist).DateTime);
        var path = $"/api/v1/patients/{patient}/consents";

        async Task<string> RefusedAsync(CaptureConsentRequest request)
        {
            using var response = await frontOffice.SendRawAsync(HttpMethod.Post, path, request);
            ((int)response.StatusCode).ShouldBeInRange(400, 422);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        CaptureConsentRequest Sharing(RecordSharingScope scope) => new(ConsentType.DataSharing, null, true, null, null, null, null, null, scope);

        (await RefusedAsync(Sharing(new RecordSharingScope([hospitalAdmin.Id], null, null, null, null, null)))).ShouldContain("consent.sharing_partner");
        (await RefusedAsync(Sharing(new RecordSharingScope([clinicId], null, null, null, null, today.AddDays(-1))))).ShouldContain("consent.sharing_ended");
        await RefusedAsync(Sharing(new RecordSharingScope(null, null, null, today, today.AddDays(-1), null)));
        await RefusedAsync(new CaptureConsentRequest(ConsentType.Research, null, true, null, null, null, null, null, new RecordSharingScope([clinicId], null, null, null, null, null)));
        await RefusedAsync(new CaptureConsentRequest(ConsentType.DataSharing, null, false, null, null, null, null, null, new RecordSharingScope([clinicId], null, null, null, null, null)));

        // The last day may be today.
        (await frontOffice.PostAsync<Created>(path, Sharing(new RecordSharingScope([clinicId], null, null, null, null, today)))).Id.ShouldNotBe(Guid.Empty);
    }

    /// <summary>Another provider's integration account: its own role with fhir.read only, as the set-up says.</summary>
    private async Task<(ApiClient Client, Guid UserId, string Name)> PartnerAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roleName = "FHIR partner " + suffix;
        await server.CreateRoleAsync(await server.SignInAsync(HospitalServer.Users.Admin),
            new SaveRoleRequest(roleName, "Another provider reading records through the FHIR API (test)", [InteropPermissions.FhirRead], null));
        var userName = "fhir" + suffix;
        await server.CreateStaffAccountAsync(userName, roleName);
        var client = await server.SignInAsync(userName);
        var me = await client.GetAsync<AuthUser>("/api/v1/auth/me");
        return (client, me.Id, me.DisplayName);
    }

    private sealed record Records(Guid OldAllergy, Guid NewAllergy, Guid Vitals);

    /// <summary>An allergy recorded last year and one this week, and today's vital signs, as the clinical screens record them.</summary>
    private async Task<Records> RecordsAsync(Guid patientId, Guid? facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow;
        var old = new Allergy { PatientId = patientId, Allergen = "Penicillin", Type = AllergyType.Drug, Severity = AllergySeverity.Severe, RecordedAt = now.AddYears(-1) };
        var recent = new Allergy { PatientId = patientId, Allergen = "Peanut", Type = AllergyType.Food, Severity = AllergySeverity.Moderate, RecordedAt = now.AddDays(-2) };
        var vitals = new VitalSigns { PatientId = patientId, FacilityId = facilityId, RecordedAt = now.AddHours(-1), Pulse = 82, SystolicBp = 124, DiastolicBp = 78 };
        db.AddRange(old, recent, vitals);
        await db.SaveChangesAsync(Ct);
        return new Records(old.Id, recent.Id, vitals.Id);
    }

    /// <summary>The FHIR API's audit entries about the patient: the action, the record and the details.</summary>
    private async Task<List<(string Action, string? EntityId, JsonElement Details)>> FhirAuditAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Summary != null && a.Summary.StartsWith("FHIR R4"))
            .Select(a => new { a.Action, a.EntityId, a.Details })
            .ToListAsync(Ct);
        return [.. rows.Select(r => (r.Action, r.EntityId, JsonDocument.Parse(r.Details ?? "{}").RootElement.Clone()))];
    }

    private static string? Text(JsonElement details, string name) =>
        details.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement details, string name) =>
        details.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private async Task SetUntilAsync(Guid consentId, DateOnly until)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var consent = await db.Set<PatientConsent>().SingleAsync(c => c.Id == consentId, Ct);
        consent.ShareUntil = until;
        await db.SaveChangesAsync(Ct);
    }

    private sealed record FhirAnswer(int Status, string? Issue, JsonElement Body)
    {
        /// <summary>The status and the refusal's reason, to compare in one go.</summary>
        public (int Status, string? Issue) Refusal => (Status, Issue);
    }

    private static async Task<FhirAnswer> FhirAsync(ApiClient client, string path, string? purpose)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/" + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
        if (purpose is not null)
        {
            request.Headers.Add("X-Purpose-Of-Use", purpose);
        }

        using var response = await client.Http.SendAsync(request, Ct);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
        var issue = body.GetProperty("resourceType").GetString() == "OperationOutcome"
            ? body.GetProperty("issue")[0].GetProperty("details").GetProperty("coding")[0].GetProperty("code").GetString()
            : null;
        return new FhirAnswer((int)response.StatusCode, issue, body);
    }

    /// <summary>The ids of a search bundle's matches.</summary>
    private static List<string> Matches(JsonElement bundle) =>
        bundle.TryGetProperty("entry", out var entries)
            ? [.. entries.EnumerateArray()
                .Where(e => e.GetProperty("search").GetProperty("mode").GetString() == "match")
                .Select(e => e.GetProperty("resource").GetProperty("id").GetString()!)]
            : [];

    /// <summary>Whether the bundle says records were withheld.</summary>
    private static bool Withheld(JsonElement bundle) =>
        bundle.TryGetProperty("entry", out var entries)
        && entries.EnumerateArray().Any(e => e.GetProperty("search").GetProperty("mode").GetString() == "outcome");

    private static async Task<Guid> RegisterAsync(ApiClient frontOffice)
    {
        var phone = "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Sharing", MiddleName: null, LastName: "Scope" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))),
            Gender: Gender.Female, DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(9000)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return patient.Id;
    }

    private sealed record Created(Guid Id);
}
