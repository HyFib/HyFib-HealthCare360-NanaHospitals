using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The route a medicine is prescribed by goes to the pharmacy label and the nurse's medication chart, so a line without
/// one, or with one its dosage form contradicts, is not signed: in the production-style run, ceftriaxone 1 g (powder for
/// injection) from the drug master was saved with the route Oral, the editor's default. The form read is the drug
/// master's for one of its medicines, the line's own for a medicine typed in.
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

    [Fact]
    public async Task A_drug_master_medicine_is_checked_by_the_masters_form_not_the_one_sent()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));
        var ceftriaxone = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Ceftriaxone")).Single(d => d.Code == "DRG-CEFTRI1G");

        // A client sends the master's ceftriaxone (powder for injection) as a tablet taken by mouth.
        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    ceftriaxone.Id, ceftriaxone.Name, ceftriaxone.GenericName, ceftriaxone.Strength, DosageForm: "Tablet", Route: "Oral", Dose: "1 g",
                    DoseFrequency.BD, CustomFrequency: null, DosesPerDay: null, DurationDays: 3, Quantity: 6, Instructions: null,
                    ceftriaxone.IsControlled, ceftriaxone.ControlledSchedule),
            ]));
        draft.Items.ShouldHaveSingleItem().DosageForm.ShouldBe(ceftriaxone.DosageForm);

        await ShouldBeRefusedAsync(doctor, draft, $"{ceftriaxone.Name} is an injection, but its route is Oral.");
    }

    [Fact]
    public async Task A_line_without_a_route_is_not_signed()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));

        // A tablet is not given by an injection's route, but its label and chart still say how it is given.
        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    DrugItemId: null, "Paracetamol 500 mg", "Paracetamol", "500 mg", "Tablet", Route: null, Dose: "1 tablet", DoseFrequency.TDS,
                    CustomFrequency: null, DosesPerDay: null, DurationDays: 3, Quantity: 9, Instructions: null, IsControlledDrug: false,
                    ControlledSchedule: null),
            ]));

        await ShouldBeRefusedAsync(doctor, draft, "Paracetamol 500 mg has no route: choose how it is given.");
    }

    private async Task ShouldBeRefusedAsync(ApiClient doctor, PrescriptionDto draft, string message)
    {
        using var refused = await doctor.SendRawAsync(HttpMethod.Post, $"{Prescriptions}/{draft.Id}/sign",
            Sign(draft.Version) with { ConfirmControlledDrugs = true });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("prescription.safety_block");
        problem.GetRawText().ShouldContain(message);
    }

    private static PrescriptionItemRequest Line(string route) => new(
        DrugItemId: null, "Cefotaxime 1 g", "Cefotaxime", "1 g", "Injection", Route: route, Dose: "1 g", DoseFrequency.BD, CustomFrequency: null,
        DosesPerDay: null, DurationDays: 3, Quantity: 6, Instructions: null, IsControlledDrug: false, ControlledSchedule: null);

    private static SignPrescriptionRequest Sign(uint version) =>
        new(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, version);
}
