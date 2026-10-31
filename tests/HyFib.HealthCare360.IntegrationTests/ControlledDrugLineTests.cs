using System.Net;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Whether a prescription line is a controlled drug (Schedule H1/X, NDPS) comes from the drug master when the line names
/// one, not from what the client sends: a line for morphine sent as not controlled is still controlled, so it needs the
/// prescriber's confirmation to sign and goes into the controlled-drug register.
/// </summary>
public sealed class ControlledDrugLineTests(HospitalServer server)
{
    private const string Prescriptions = "/api/v1/clinical/prescriptions";

    [Fact]
    public async Task A_master_drug_is_controlled_whatever_the_line_says()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));
        var morphine = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Morphine")).Single(d => d.Code == "DRG-MORPH10");
        morphine.IsControlled.ShouldBeTrue();

        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    morphine.Id, morphine.Name, morphine.GenericName, morphine.Strength, morphine.DosageForm, Route: "IV", Dose: "2 mg", DoseFrequency.STAT,
                    CustomFrequency: null, DosesPerDay: null, 1, Quantity: 1, Instructions: null, IsControlledDrug: false, ControlledSchedule: null),
            ]));
        var line = draft.Items.ShouldHaveSingleItem();
        (line.IsControlledDrug, line.ControlledSchedule).ShouldBe((true, "X"));

        using (var unconfirmed = await doctor.SendRawAsync(HttpMethod.Post, $"{Prescriptions}/{draft.Id}/sign",
                   new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version)))
        {
            unconfirmed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await unconfirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("prescription.controlled_confirmation_required");
        }

        // Edited and sent as not controlled again: still controlled.
        var edited = await doctor.PutAsync<PrescriptionDto>($"{Prescriptions}/{draft.Id}", new UpdatePrescriptionRequest(
            Notes: "Breakthrough pain", Advice: null, RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    morphine.Id, morphine.Name, morphine.GenericName, morphine.Strength, morphine.DosageForm, Route: "IV", Dose: "2 mg", DoseFrequency.STAT,
                    CustomFrequency: null, DosesPerDay: null, 1, Quantity: 1, Instructions: null, IsControlledDrug: false, ControlledSchedule: null),
            ],
            draft.Version));
        edited.Items.ShouldHaveSingleItem().IsControlledDrug.ShouldBeTrue();

        var signed = await doctor.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: true, edited.Version));
        signed.Prescription.Status.ShouldBe(PrescriptionStatus.Signed);
    }

    [Fact]
    public async Task A_draft_saved_as_not_controlled_is_judged_again_at_signing()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));
        var morphine = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Morphine")).Single(d => d.Code == "DRG-MORPH10");
        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    morphine.Id, morphine.Name, morphine.GenericName, morphine.Strength, morphine.DosageForm, Route: "IV", Dose: "2 mg", DoseFrequency.STAT,
                    CustomFrequency: null, DosesPerDay: null, 1, Quantity: 1, Instructions: null, IsControlledDrug: true, ControlledSchedule: "X"),
            ]));

        // As a draft saved before the drug master decided would be stored.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PrescriptionItem>()
                .Where(i => i.PrescriptionId == draft.Id)
                .ExecuteUpdateAsync(i => i.SetProperty(x => x.IsControlledDrug, false).SetProperty(x => x.ControlledSchedule, (string?)null), TestContext.Current.CancellationToken);
        }

        var check = await doctor.PostAsync<SafetyCheckResponse>($"{Prescriptions}/{draft.Id}/check");
        check.HasControlledDrugs.ShouldBeTrue();
        // Announced by the schedule the drug master gives it.
        check.Alerts.Single(a => a.Kind == SafetyAlertKind.ControlledDrug).Message.ShouldStartWith($"{morphine.Name} is a Schedule X medicine");
        using var unconfirmed = await doctor.SendRawAsync(HttpMethod.Post, $"{Prescriptions}/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version));
        unconfirmed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await unconfirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("prescription.controlled_confirmation_required");
    }

    [Fact]
    public async Task The_dispensing_queue_carries_each_medicines_schedule_from_the_drug_master()
    {
        // As in the production-style run: ceftriaxone, a Schedule H1 antibiotic, was badged NDPS at the counter because the
        // screen read the line's controlled mark (H1, X or NDPS) as the item's NDPS flag. The queue now carries the drug
        // master's schedule and NDPS flag, so even a dispenser who may not read the catalogue sees them.
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));
        var ceftriaxone = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Ceftriaxone")).Single(d => d.Code == "DRG-CEFTRI1G");
        var tramadol = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Tramadol")).Single(d => d.Code == "DRG-TRAM50");
        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    ceftriaxone.Id, ceftriaxone.Name, ceftriaxone.GenericName, ceftriaxone.Strength, ceftriaxone.DosageForm, Route: "IV", Dose: "1 g",
                    DoseFrequency.STAT, CustomFrequency: null, DosesPerDay: null, 1, Quantity: 1, Instructions: null, IsControlledDrug: true, ControlledSchedule: "H1"),
                new PrescriptionItemRequest(
                    tramadol.Id, tramadol.Name, tramadol.GenericName, tramadol.Strength, tramadol.DosageForm, Route: "Oral", Dose: "1 capsule",
                    DoseFrequency.STAT, CustomFrequency: null, DosesPerDay: null, 1, Quantity: 1, Instructions: null, IsControlledDrug: true, ControlledSchedule: "H1"),
            ]));
        var signed = await doctor.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: true, draft.Version));

        var waiting = (await pharmacist.GetAsync<PagedResult<QueueListItem>>($"/api/v1/pharmacy/queue?search={signed.Prescription.PrescriptionNumber}"))
            .Items.ShouldHaveSingleItem();
        var lines = (await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{waiting.Id}")).Lines;

        // Both lines carry the prescriber's controlled mark; only tramadol is NDPS.
        var cef = lines.Single(l => l.DrugItemId == ceftriaxone.Id);
        (cef.IsControlledDrug, cef.Schedule, cef.IsNdps).ShouldBe((true, DrugSchedule.H1, false));
        var tram = lines.Single(l => l.DrugItemId == tramadol.Id);
        (tram.IsControlledDrug, tram.Schedule, tram.IsNdps).ShouldBe((true, DrugSchedule.H1, true));
    }
}
