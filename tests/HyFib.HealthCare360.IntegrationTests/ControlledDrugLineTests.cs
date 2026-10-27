using System.Net;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Platform.Persistence;
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

        (await doctor.PostAsync<SafetyCheckResponse>($"{Prescriptions}/{draft.Id}/check")).HasControlledDrugs.ShouldBeTrue();
        using var unconfirmed = await doctor.SendRawAsync(HttpMethod.Post, $"{Prescriptions}/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version));
        unconfirmed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await unconfirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("prescription.controlled_confirmation_required");
    }
}
