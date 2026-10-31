using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The route a medicine is prescribed by goes to the pharmacy label and the nurse's medication chart, so a route its
/// dosage form contradicts is not signed: in the production-style run, ceftriaxone 1 g (powder for injection) from the drug
/// master was saved with the route Oral, the editor's default. The form is the line's own, read on the server.
/// </summary>
public sealed class RouteSafetyTests(HospitalServer server)
{
    private const string Prescriptions = "/api/v1/clinical/prescriptions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_injection_is_not_signed_by_mouth_and_is_signed_once_its_route_is_iv()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));

        // The name does not say injection; the form does.
        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0, [Line("Oral")]));

        using (var refused = await doctor.SendRawAsync(HttpMethod.Post, $"{Prescriptions}/{draft.Id}/sign", Sign(draft.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("code").GetString().ShouldBe("prescription.safety_block");
            problem.GetRawText().ShouldContain("Cefotaxime 1 g is an injection, but its route is Oral.");
        }

        var check = await doctor.PostAsync<JsonElement>($"{Prescriptions}/{draft.Id}/check", new { });
        check.GetProperty("isBlocked").GetBoolean().ShouldBeTrue();

        var iv = await doctor.PutAsync<PrescriptionDto>($"{Prescriptions}/{draft.Id}", new UpdatePrescriptionRequest(
            Notes: null, Advice: null, RefillsAllowed: 0, [Line("IV")], draft.Version));
        (await doctor.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{draft.Id}/sign", Sign(iv.Version)))
            .Prescription.Status.ShouldBe(PrescriptionStatus.Signed);
    }

    private static PrescriptionItemRequest Line(string route) => new(
        DrugItemId: null, "Cefotaxime 1 g", "Cefotaxime", "1 g", "Injection", Route: route, Dose: "1 g", DoseFrequency.BD, CustomFrequency: null,
        DosesPerDay: null, DurationDays: 3, Quantity: 6, Instructions: null, IsControlledDrug: false, ControlledSchedule: null);

    private static SignPrescriptionRequest Sign(uint version) =>
        new(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, version);
}
