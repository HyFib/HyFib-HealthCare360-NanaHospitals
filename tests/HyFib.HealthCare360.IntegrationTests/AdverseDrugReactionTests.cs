using System.Net;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Adr;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A suspected adverse drug reaction over the real API: reported by the nurse who saw it, kept to the reporter and to
/// pharmacovigilance, assessed and sent to the ADR monitoring centre, and printed on the PvPI form by initials.
/// </summary>
public sealed class AdverseDrugReactionTests(HospitalServer server)
{
    [Fact]
    public async Task A_reaction_is_reported_by_the_nurse_assessed_and_sent_by_pharmacovigilance()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        var suspect = new AdrDrug(
            "Ceftriaxone 1 g Injection", "Lupin", "CT2407", new DateOnly(2027, 6, 30), "1 g", "IV", "Twice a day", today.AddDays(-2), today,
            "Pneumonia", Suspected: true, AdrActionTaken.Withdrawn, ChallengeResult.Yes, ChallengeResult.NotApplicable);
        var request = new SaveAdrReportRequest(
            patient.Id, null, today, "Generalised urticaria and wheeze 20 minutes after the second dose", today, null,
            AdrSeriousness.RequiredIntervention, AdrOutcome.Recovering, 62m, null, null, [suspect], "Staff nurse, ward 3");

        (await nurse.GetAsync<List<DispensedMedicine>>($"/api/v1/pharmacy/adr/patients/{patient.Id}/medicines")).ShouldBeEmpty();
        var report = await nurse.PostAsync<AdrReportDto>("/api/v1/pharmacy/adr", request);
        (report.Status, report.DueBy, report.Uhid).ShouldBe((AdrReportStatus.Draft, today.AddDays(AdrReport.SeriousDays), patient.Uhid));
        report.PatientInitials.Length.ShouldBeLessThan(8, "the form names the patient by initials");

        // Another doctor reports too, but does not open the nurse's report; pharmacovigilance sees every report.
        using (var notTheirs = await doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/pharmacy/adr/{report.Id}"))
        {
            notTheirs.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await nurse.GetAsync<List<AdrReportListItem>>("/api/v1/pharmacy/adr")).ShouldContain(r => r.Id == report.Id);
        (await doctor.GetAsync<List<AdrReportListItem>>("/api/v1/pharmacy/adr")).ShouldNotContain(r => r.Id == report.Id);
        (await pharmacist.GetAsync<List<AdrReportListItem>>("/api/v1/pharmacy/adr")).Single(r => r.Id == report.Id).Serious.ShouldBeTrue();

        // The nurse corrects her draft; only pharmacovigilance sends it, with the causality.
        var corrected = await nurse.PutAsync<AdrReportDto>($"/api/v1/pharmacy/adr/{report.Id}",
            request with { Outcome = AdrOutcome.Recovered, ReactionStoppedOn = today, Version = report.Version });
        corrected.Outcome.ShouldBe(AdrOutcome.Recovered);
        using (var notTheNurses = await nurse.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/adr/{report.Id}/submit",
                   new SubmitAdrReportRequest(WhoUmcCausality.Probable, today, null, corrected.Version)))
        {
            notTheNurses.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var sent = await pharmacist.PostAsync<AdrReportDto>($"/api/v1/pharmacy/adr/{report.Id}/submit",
            new SubmitAdrReportRequest(WhoUmcCausality.Probable, today, "AMC-TN-2026-1187", corrected.Version));
        (sent.Status, sent.Causality, sent.SubmissionReference).ShouldBe((AdrReportStatus.Submitted, WhoUmcCausality.Probable, "AMC-TN-2026-1187"));
        using (var locked = await nurse.SendRawAsync(HttpMethod.Put, $"/api/v1/pharmacy/adr/{report.Id}", request with { Version = sent.Version }))
        {
            locked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var form = await nurse.GetAsync<PrintableDocumentDto>($"/api/v1/pharmacy/adr/{report.Id}/document");
        form.Html.ShouldContain("Suspected adverse drug reaction reporting form (PvPI)");
        form.Html.ShouldContain("Ceftriaxone 1 g Injection");
        form.Html.ShouldContain("Probable");
    }
}
