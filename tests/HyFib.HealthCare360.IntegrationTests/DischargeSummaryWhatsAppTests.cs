using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.DischargeStays;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// With the <c>discharge.summary</c> WhatsApp template switched on (it is seeded "Not in use" until Meta approves it), a
/// discharge with a signed summary sends that template with the summary as its document, and the SMS stays
/// <c>discharge.completed</c>. A discharge without a summary (LAMA never waits for one) gets the text-only
/// <c>discharge.completed</c> on WhatsApp, so a document header never costs a patient their WhatsApp message. The tests
/// switch a hospital-wide template on, so they run with the feature-switch tests, by themselves.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class DischargeSummaryWhatsAppTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_the_summary_template_on_a_planned_discharge_sends_the_signed_summary_on_whatsapp_and_the_sms_stays_the_discharge_message()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        var phone = await PhoneAsync(patient.Id);
        var draft = await doctor.PutAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/by-admission/{stay.Id}", Summary(patient.Id));
        var signed = await doctor.PostAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/{draft.Id}/sign", new SignDischargeSummaryRequest(draft.Version));
        await InitiateAndClearAsync(server, nurse, stay.Id);

        var switchedOn = await SwitchSummaryTemplatesAsync(inUse: true);
        try
        {
            var since = DateTimeOffset.UtcNow.AddSeconds(-1);
            (await nurse.PostAsync<DischargeResult>(Complete(stay.Id), new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home", NotifyPatient: true)))
                .Status.ShouldBe(AdmissionStatus.Discharged);

            var summary = await DeliveriesAsync("discharge.summary", phone, since);
            summary.Select(d => d.Channel).ShouldBe([NotificationChannel.WhatsApp]);
            summary.Single().Document.ShouldBe(new NotificationDocument(ClinicalDocumentKinds.DischargeSummary, signed.Id));

            var completed = await DeliveriesAsync("discharge.completed", phone, since);
            completed.Select(d => d.Channel).ShouldBe([NotificationChannel.Sms]);
            completed.Single().Document.ShouldBeNull("an SMS carries no document");
        }
        finally
        {
            await SwitchSummaryTemplatesAsync(inUse: false, only: switchedOn);
        }

        await CleanBedAsync(nurse, stay.Id);
    }

    [Fact]
    public async Task With_the_summary_template_on_a_lama_discharge_without_a_summary_still_gets_the_discharge_message_on_whatsapp()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        var phone = await PhoneAsync(patient.Id);
        await InitiateAndClearAsync(server, nurse, stay.Id);
        var witness = await server.WitnessAsync(doctor, Users.Nurse, DischargeService.AgainstAdviceWitness.Key, patient.Id);
        var lama = new CompleteDischargeRequest(DischargeType.Lama, null, "Left against advice after the risks were explained", NotifyPatient: true,
            AgainstAdvice: new AgainstAdviceRequest(AgainstAdviceSignatory.Patient, null, null, null, witness.ConfirmationId, null));

        var switchedOn = await SwitchSummaryTemplatesAsync(inUse: true);
        try
        {
            var since = DateTimeOffset.UtcNow.AddSeconds(-1);
            (await doctor.PostAsync<DischargeResult>(Complete(stay.Id), lama)).Status.ShouldBe(AdmissionStatus.Lama);

            var completed = await DeliveriesAsync("discharge.completed", phone, since);
            completed.Select(d => d.Channel).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
            completed.ShouldAllBe(d => d.Document == null);
            (await DeliveriesAsync("discharge.summary", phone, since)).ShouldBeEmpty("there is no signed summary to send");
        }
        finally
        {
            await SwitchSummaryTemplatesAsync(inUse: false, only: switchedOn);
        }

        await CleanBedAsync(nurse, stay.Id);
    }

    /// <summary>
    /// Sets every stored version of the discharge summary's WhatsApp template in use or not; the ids it changed, so that
    /// they alone are put back.
    /// </summary>
    private async Task<List<Guid>> SwitchSummaryTemplatesAsync(bool inUse, IReadOnlyCollection<Guid>? only = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var templates = await db.Set<NotificationTemplate>()
            .Where(t => t.Key == DischargeService.DischargeSummaryTemplate && t.Channel == NotificationChannel.WhatsApp && t.IsActive != inUse)
            .ToListAsync(Ct);
        var changed = templates.Where(t => only is null || only.Contains(t.Id)).ToList();
        changed.ForEach(t => t.IsActive = inUse);
        await db.SaveChangesAsync(Ct);
        return [.. changed.Select(t => t.Id)];
    }

    /// <summary>The messages of <paramref name="key"/> queued to the number since then, on every channel.</summary>
    private async Task<List<NotificationDelivery>> DeliveriesAsync(string key, string phone, DateTimeOffset since)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => (m.Type == NotificationService.SmsType || m.Type == NotificationService.WhatsAppType) && m.CreatedAt >= since)
            .ToListAsync(Ct);
        return messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == key && d.To == phone).ToList();
    }

    private async Task<string> PhoneAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().AsNoTracking()
            .Where(p => p.Id == patientId).Select(p => p.Phone!).SingleAsync(Ct);
    }

    private static SaveDischargeSummaryRequest Summary(Guid patientId) => new(
        patientId, null, null, null, null, "Community-acquired pneumonia", [], "Fever and cough", "Treated with IV antibiotics; afebrile for 48 hours", null, null,
        "Stable, ambulant", "Amoxicillin-clavulanate 625 mg BD x 5 days", "Plenty of fluids", null, null, null);
}
