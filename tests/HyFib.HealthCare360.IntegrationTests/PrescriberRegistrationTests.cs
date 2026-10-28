using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A prescription carries the prescriber's registration number: every physician shows the number the State Medical
/// Council or the national register gave them on all their prescriptions (Indian Medical Council (Professional Conduct,
/// Etiquette and Ethics) Regulations 2002, 1.4.1). A prescriber whose practitioner record has none cannot sign one, and a
/// signed prescription keeps the number it was signed with.
/// </summary>
public sealed class PrescriberRegistrationTests(HospitalServer server)
{
    private const string Prescriptions = "/api/v1/clinical/prescriptions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_prescription_is_signed_only_by_a_prescriber_whose_registration_number_is_on_record()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));
        var paracetamol = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Paracetamol")).First(d => !d.IsControlled);
        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    paracetamol.Id, paracetamol.Name, paracetamol.GenericName, paracetamol.Strength, paracetamol.DosageForm, Route: "Oral", Dose: "1 tablet",
                    DoseFrequency.TDS, CustomFrequency: null, DosesPerDay: null, 3, Quantity: null, Instructions: "After food", false, null),
            ]));

        // The doctor's practitioner record has lost its registration number: the prescription is not signed.
        var practitionerId = draft.PractitionerId;
        var registration = await RegistrationAsync(practitionerId, null);
        try
        {
            using var refused = await doctor.SendRawAsync(HttpMethod.Post, $"{Prescriptions}/{draft.Id}/sign",
                new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version));
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("code").GetString().ShouldBe("prescription.registration_missing");
            problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("registration number");
        }
        finally
        {
            await RegistrationAsync(practitionerId, registration);
        }

        // With the number back on the record, the same prescription is signed.
        var signed = await doctor.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version));
        signed.Prescription.Status.ShouldBe(PrescriptionStatus.Signed);

        // Every copy carries the number it was signed with, whatever the practitioner record says later: corrected, or
        // cleared.
        var token = await TokenAsync(draft.Id);
        var pharmacist = server.Anonymous(ownAddress: true);
        foreach (var later in new[] { "TNMC 99999", null })
        {
            await RegistrationAsync(practitionerId, later);
            try
            {
                var reprint = (await doctor.GetAsync<PrintableDocumentDto>($"{Prescriptions}/{draft.Id}/document")).Html;
                reprint.ShouldContain(registration!);
                reprint.ShouldNotContain("TNMC 99999");
                var checkedByQr = await pharmacist.GetAsync<PrescriptionVerification>($"/api/v1/public/prescriptions/verify/{token}");
                checkedByQr.RegistrationNumber.ShouldNotBeNull().ShouldStartWith(registration!);
            }
            finally
            {
                await RegistrationAsync(practitionerId, registration);
            }
        }
    }

    private async Task<string> TokenAsync(Guid prescriptionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Prescription>().IgnoreQueryFilters()
            .Where(p => p.Id == prescriptionId).Select(p => p.VerificationToken!).SingleAsync(Ct);
    }

    /// <summary>Sets the practitioner's registration number; returns the one it had.</summary>
    private async Task<string?> RegistrationAsync(Guid practitionerId, string? number)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var practitioner = await db.Set<Practitioner>().SingleAsync(p => p.Id == practitionerId, CancellationToken.None);
        var before = practitioner.RegistrationNumber;
        practitioner.RegistrationNumber = number;
        await db.SaveChangesAsync(CancellationToken.None);
        return before;
    }
}
