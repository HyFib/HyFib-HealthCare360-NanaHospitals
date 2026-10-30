using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Every physician prescribes by generic name, legibly and preferably in capitals (Indian Medical Council (Professional
/// Conduct, Etiquette and Ethics) Regulations 2002, reg. 1.5, as amended in 2016). The drug master names the generic of
/// its drugs, a medicine typed in needs the doctor to name it before the prescription is signed, and the printed copy names
/// each medicine by its generic name in capitals first, with the product the doctor chose after it.
/// </summary>
public sealed class GenericNameTests(HospitalServer server)
{
    private const string Prescriptions = "/api/v1/clinical/prescriptions";
    private const string ParacetamolCode = "DRG-PCM500";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_prescription_names_every_medicine_by_its_generic_name_first_and_in_capitals()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));
        var paracetamol = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Paracetamol")).Single(d => d.Code == ParacetamolCode);

        // The drug master names the generic of its drug, whatever the client sent. A medicine typed in keeps what the doctor
        // wrote: with no generic name, it is saved in the draft but not signed.
        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0, [Line(paracetamol.Id, paracetamol.Name, "Acetaminophen", "500 mg"), Line(null, "Pan 40", null, "40 mg")]));
        draft.Items.Select(i => i.GenericName).ShouldBe(["Paracetamol", null]);

        using (var refused = await doctor.SendRawAsync(HttpMethod.Post, $"{Prescriptions}/{draft.Id}/sign", Sign(draft.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("code").GetString().ShouldBe("prescription.generic_missing");
            problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("Pan 40");
        }

        // The doctor names it; the prescription is signed.
        var named = await doctor.PutAsync<PrescriptionDto>($"{Prescriptions}/{draft.Id}", new UpdatePrescriptionRequest(
            Notes: null, Advice: null, RefillsAllowed: 0,
            [Line(paracetamol.Id, paracetamol.Name, null, "500 mg"), Line(null, "Pan 40", "Pantoprazole", "40 mg")], draft.Version));
        named.Items.Select(i => i.GenericName).ShouldBe(["Paracetamol", "Pantoprazole"]);
        (await doctor.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{draft.Id}/sign", Sign(named.Version)))
            .Prescription.Status.ShouldBe(PrescriptionStatus.Signed);

        // The printed copy names each by its generic name in capitals, then the brand the doctor chose; the master's
        // "Paracetamol 500 mg Tablet" is named once.
        var html = (await doctor.GetAsync<PrintableDocumentDto>($"{Prescriptions}/{draft.Id}/document")).Html;
        html.ShouldContain("<div class=\"drug\">PARACETAMOL 500 mg</div>", Case.Sensitive);
        html.ShouldContain("<div class=\"drug\">PANTOPRAZOLE 40 mg</div>", Case.Sensitive);
        html.ShouldContain("<div class=\"small\">Pan 40 · Tablet</div>", Case.Sensitive);
        html.ShouldNotContain(paracetamol.Name);
        html.ShouldNotContain("Acetaminophen");
    }

    [Fact]
    public async Task A_refill_cannot_name_a_generic_its_prescription_left_out_and_says_to_repeat_the_prescription()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));
        var paracetamol = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Paracetamol")).Single(d => d.Code == ParacetamolCode);
        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 1, [Line(paracetamol.Id, paracetamol.Name, null, "500 mg"), Line(null, "Pan 40", "Pantoprazole", "40 mg")]));
        var source = (await doctor.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{draft.Id}/sign", Sign(draft.Version))).Prescription;

        // As signed before a prescription had to name every generic: neither line names one.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PrescriptionItem>()
                .Where(i => i.PrescriptionId == source.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.GenericName, (string?)null), Ct);
        }

        // The drug master names the paracetamol's again. Nothing names Pan 40's, so the refill is refused and not used up.
        using (var refused = await doctor.SendRawAsync(HttpMethod.Post, $"{Prescriptions}/{source.Id}/refill",
            new RefillPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: false, ConfirmControlledDrugs: false)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("code").GetString().ShouldBe("prescription.generic_missing");
            problem.GetProperty("detail").GetString().ShouldBe(
                $"Pan 40 has no generic name on prescription {source.PrescriptionNumber}, and a refill can't be edited. "
                + "Repeat the prescription as a new draft, enter the generic name and sign that.");
        }

        (await doctor.GetAsync<PrescriptionDto>($"{Prescriptions}/{source.Id}")).RefillsRemaining.ShouldBe(1);
    }

    private static PrescriptionItemRequest Line(Guid? drugItemId, string name, string? generic, string strength) => new(
        drugItemId, name, generic, strength, "Tablet", Route: "Oral", Dose: "1 tablet", DoseFrequency.TDS, CustomFrequency: null, DosesPerDay: null,
        DurationDays: 3, Quantity: null, Instructions: "After food", IsControlledDrug: false, ControlledSchedule: null);

    private static SignPrescriptionRequest Sign(uint version) =>
        new(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, version);
}
