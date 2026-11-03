using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Immunizations;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Gap #25 through the whole server: the vaccine doses staff record reach another provider through the FHIR API, coded
/// with the ABDM guide's vaccine codes, a dose copied from a card marked as such, a dose recorded by mistake retracted,
/// and only under a record-sharing consent that covers vaccines.
/// </summary>
public sealed class ImmunizationSharingTests(HospitalServer server)
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    [Fact]
    public async Task A_provider_reads_the_vaccines_given_here_coded_and_those_from_a_card_as_such()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ist).DateTime);
        var doses = $"/api/v1/emr/patients/{patient}/immunizations";

        // The form offers the vaccines by card code, with their usual route.
        var vaccines = await nurse.GetAsync<List<VaccineChoiceDto>>("/api/v1/emr/vaccines");
        vaccines.ShouldContain(v => v.Code == "BCG" && v.Route == VaccineRoute.Intradermal);
        vaccines.Select(v => v.Code).ShouldBe(VaccineCodes.All.Select(v => v.Code));

        // BCG given here, hepatitis B copied from the card, and an OPV recorded by mistake.
        var bcg = await nurse.PostAsync<ImmunizationDto>(doses, new RecordImmunizationRequest(
            null, "BCG", "bcg", 1, today, "ID", "Left upper arm", "B2611-07", "Serum Institute of India", today.AddMonths(6), null, false, null, null));
        var fromCard = await nurse.PostAsync<ImmunizationDto>(doses, new RecordImmunizationRequest(
            null, "Hepatitis B", "HEPB", 1, today.AddDays(-10), "IM", null, null, null, null, "PHC Thiruvallur", true, null, null));
        var opv = await nurse.PostAsync<ImmunizationDto>(doses, new RecordImmunizationRequest(
            null, "Oral polio vaccine", "OPV", 1, today, "Oral", null, "OPV-26-31", "Bharat Biotech", today.AddMonths(3), null, false, null, null));
        await nurse.PostAsync<ImmunizationDto>($"/api/v1/emr/immunizations/{opv.Id}/entered-in-error", new EnteredInErrorRequest("Recorded for the twin"));

        // The patient shares their vaccines, and nothing else, with the partner for treatment.
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], [SharingPurpose.Treatment], [SharedRecordKind.Immunisations], null, null, null)));

        var search = await FhirAsync(partner, $"Immunization?patient={patient}", "TREAT");
        Matches(search.Body).ShouldBe([bcg.Id.ToString("D"), fromCard.Id.ToString("D")], ignoreOrder: true);
        var given = Resource(search.Body, bcg.Id);
        Code(given.GetProperty("vaccineCode")).ShouldBe(("http://snomed.info/sct", "1861000221106"));
        given.GetProperty("primarySource").GetBoolean().ShouldBeTrue();
        given.GetProperty("lotNumber").GetString().ShouldBe("B2611-07");
        given.GetProperty("performer")[0].GetProperty("actor").GetProperty("display").GetString().ShouldNotBeNullOrWhiteSpace("the nurse who gave it");
        Code(given.GetProperty("route")).ShouldBe(("http://snomed.info/sct", "372464004"));
        var copied = Resource(search.Body, fromCard.Id);
        copied.GetProperty("primarySource").GetBoolean().ShouldBeFalse();
        Code(copied.GetProperty("reportOrigin")).ShouldBe(("http://terminology.hl7.org/CodeSystem/immunization-origin", "record"));
        copied.GetProperty("performer")[0].GetProperty("actor").GetProperty("display").GetString().ShouldBe("PHC Thiruvallur");

        // The dose in error is left out of searches, but read by id it says it was entered in error.
        var retracted = await FhirAsync(partner, $"Immunization/{opv.Id}", "TREAT");
        retracted.Status.ShouldBe(200);
        retracted.Body.GetProperty("status").GetString().ShouldBe("entered-in-error");
        Matches((await FhirAsync(partner, $"Immunization?patient={patient}&status=entered-in-error", "TREAT")).Body).ShouldBe([opv.Id.ToString("D")]);
        Matches((await FhirAsync(partner, $"Immunization?patient={patient}&vaccine-code=http://snomed.info/sct|871822003", "TREAT")).Body)
            .ShouldBe([fromCard.Id.ToString("D")]);

        // The consent covers vaccines only.
        Matches((await FhirAsync(partner, $"AllergyIntolerance?patient={patient}", "TREAT")).Body).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_consent_that_names_other_kinds_of_records_does_not_share_vaccines()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ist).DateTime);
        var dose = await nurse.PostAsync<ImmunizationDto>($"/api/v1/emr/patients/{patient}/immunizations", new RecordImmunizationRequest(
            null, "Tetanus toxoid", "TT", 2, today, "IM", "Left deltoid", "TT-0911", "Biological E", today.AddMonths(8), null, false, null, null));
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], null, [SharedRecordKind.Allergies, SharedRecordKind.LabResults], null, null, null)));

        var search = await FhirAsync(partner, $"Immunization?patient={patient}", null);
        Matches(search.Body).ShouldBeEmpty();
        Withheld(search.Body).ShouldBeTrue();
        (await FhirAsync(partner, $"Immunization/{dose.Id}", null)).Refusal.ShouldBe((403, "consent-required"));
    }

    private static JsonElement Resource(JsonElement bundle, Guid id) =>
        bundle.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).Single(r => r.GetProperty("id").GetString() == id.ToString("D"));

    private static (string? System, string? Code) Code(JsonElement concept)
    {
        var coding = concept.GetProperty("coding")[0];
        return (coding.GetProperty("system").GetString(), coding.GetProperty("code").GetString());
    }
}
