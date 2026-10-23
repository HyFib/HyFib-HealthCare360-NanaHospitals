using System.Globalization;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Remote monitoring alerts to the care team (SRS RPM-03) over the real API: a reading that breaches the patient's own
/// threshold raises an alert, which the care team's queue lists and the clinicians who handle alerts get in-app, and which
/// goes by SMS and WhatsApp (the <c>rpm.alert</c> template, through the outbox) to each active member of the care team who
/// has a mobile number. A send that fails is kept and tried again until it goes, and the alert stays open meanwhile. The
/// test turns SMS and WhatsApp off, then on to the integrations simulator, and puts them back as they were.
/// </summary>
public sealed class RpmAlertJourneyTests(HospitalServer server)
{
    private const string AlertTemplate = "rpm.alert";

    /// <summary>India Standard Time, the hospital's calendar (no daylight saving).</summary>
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_breach_goes_to_the_care_team_by_sms_and_whatsapp_and_a_failed_send_is_tried_again_while_the_alert_stays_open()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);

        // The care team: a nurse with a mobile number, one who has since left the hospital, and one with no number on file.
        var nursePhone = "+919" + Digits(9);
        var teamNurse = await NurseAsync(hospitalAdmin, nursePhone);
        var leaver = await NurseAsync(hospitalAdmin, "+919" + Digits(9));
        var noNumber = await NurseAsync(hospitalAdmin, phone: null);

        // The doctor enrols the patient in hypertension monitoring, with a tighter systolic limit than the programme's 140.
        var firstName = TestPatients.FirstName("Kamala");
        var patient = await RegisterPatientAsync(frontOffice, firstName);
        var program = (await doctor.GetAsync<List<RpmProgramDto>>("/api/v1/rpm/programs")).Single(p => p.Code == "HTN");
        var enrollmentId = (await doctor.PostAsync<Created>("/api/v1/rpm/enrollments", new EnrollPatientRequest(
            patient.Id, program.Id, [teamNurse, leaver, noNumber], teamNurse, StartDate: null, Goals: "Systolic below 130", Notes: null))).Id;
        var enrollment = await doctor.GetAsync<RpmEnrollmentDetail>($"/api/v1/rpm/enrollments/{enrollmentId}");
        await doctor.PutAsync($"/api/v1/rpm/enrollments/{enrollmentId}/thresholds", new SetThresholdsRequest(
            [new ThresholdInput(RpmReadingType.SystolicBp, Min: 90, Max: 130, CriticalMin: 80, CriticalMax: 180)], enrollment.Version));
        await LeavesAsync(hospitalAdmin, leaver);

        await using var messaging = await Messaging.TurnOffAsync(await server.SignInAsync(HospitalServer.Users.Admin));

        // A nurse records a reading the patient read out on a follow-up call: within the programme's limit, over the patient's.
        var measuredAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ingested = await nurse.PostAsync<IngestResult>($"/api/v1/rpm/enrollments/{enrollmentId}/readings",
            new RecordReadingsRequest([new ManualReading(RpmReadingType.SystolicBp, 136, "mmHg", measuredAt, TextValue: null)]));
        ingested.AlertsRaised.ShouldBe(1);
        var alert = (await doctor.GetAsync<PagedResult<RpmAlertDto>>($"/api/v1/rpm/alerts?enrollmentId={enrollmentId}")).Items.ShouldHaveSingleItem();
        (alert.Severity, alert.Status, alert.Message).ShouldBe((RpmAlertSeverity.Warning, RpmAlertStatus.Open, "Out of range: Systolic BP 136 mmHg"));

        // In-app: the clinicians who handle alerts, the doctor among them, are told at once, with a link to the alert.
        var label = $"{firstName} ({patient.Uhid})";
        var bell = (await doctor.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=50")).Items
            .Where(n => n.Link == $"/rpm/enrollments/{enrollmentId}?alert={alert.Id}").ShouldHaveSingleItem();
        (bell.Title, bell.Body, bell.Severity).ShouldBe(($"Remote monitoring alert: {label}", alert.Message, "warning"));

        // By SMS and WhatsApp: the care team's nurse alone, on both channels, with the rpm.alert template; not the one who
        // left, nor the one with no number. The message names the patient by first name and UHID only.
        var queued = await AlertMessagesAsync(patient.Uhid);
        queued.Select(q => (q.Delivery.Channel, q.Delivery.To))
            .ShouldBe([(NotificationChannel.Sms, nursePhone), (NotificationChannel.WhatsApp, nursePhone)], ignoreOrder: true);
        var when = measuredAt.ToOffset(IndiaOffset).ToString("dd MMM, hh:mm tt", CultureInfo.InvariantCulture);
        foreach (var (message, delivery) in queued)
        {
            delivery.Values.ShouldBe(new Dictionary<string, string> { ["patient"] = label, ["reading"] = alert.Message, ["time"] = when }, ignoreOrder: true);
            delivery.FacilityId.ShouldBe(enrollment.FacilityId);
            (message.ExpiresAt - message.CreatedAt).ShouldBe(TimeSpan.FromHours(6), "an alert is worth sending for 6 hours");
        }

        // SMS and WhatsApp are off, so the first tries fail. The messages are kept and tried again; the alert stays open.
        var ids = queued.Select(q => q.Message.Id).ToList();
        var tried = await EventuallyAsync(async () =>
        {
            var messages = await MessagesAsync(ids);
            return messages.Count == ids.Count && messages.All(m => m.Attempts > 0) ? messages : null;
        }, "The outbox did not try to send the alert.");
        tried.ShouldAllBe(m => m.Status == OutboxStatus.Pending && m.LastError != null && m.NextAttemptAt > m.CreatedAt);
        var logs = await LogsAsync(queued.Select(q => q.Delivery.LogId));
        logs.Count.ShouldBe(2);
        logs.ShouldAllBe(l => l.TemplateKey == AlertTemplate && l.Status == NotificationStatus.Failed && l.Error != null);
        var stillOpen = (await doctor.GetAsync<PagedResult<RpmAlertDto>>($"/api/v1/rpm/alerts?enrollmentId={enrollmentId}")).Items.ShouldHaveSingleItem();
        (stillOpen.Id, stillOpen.Status, stillOpen.AcknowledgedAt).ShouldBe((alert.Id, RpmAlertStatus.Open, (DateTimeOffset?)null));

        // Once they are back (the simulator stands in for the gateways), the next tries deliver both messages.
        await messaging.TurnOnSimulatorAsync();
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var sms = await DeliveredAsync(admin, "Sms", queued.Single(q => q.Delivery.Channel == NotificationChannel.Sms).Delivery.LogId);
        sms.Recipient.ShouldBe(Phone.Mask(nursePhone));
        sms.Body.ShouldBe($"HC360 RPM alert: {label} reported {alert.Message} (outside set range) at {when}.");
        var whatsApp = await DeliveredAsync(admin, "WhatsApp", queued.Single(q => q.Delivery.Channel == NotificationChannel.WhatsApp).Delivery.LogId);
        whatsApp.Recipient.ShouldBe(Phone.Mask(nursePhone));
        whatsApp.Subject.ShouldBe("rpm_alert");
        whatsApp.Body.ShouldBe($"HC360 RPM alert: {label} reported {alert.Message} at {when}. Please review it in HC360.");
        await EventuallyAsync(async () =>
        {
            var sent = await LogsAsync(logs.Select(l => l.Id));
            return sent.Count == logs.Count && sent.All(l => l.Status == NotificationStatus.Sent) ? sent : null;
        }, "The delivered messages were not logged as sent.");
        (await MessagesAsync(ids)).ShouldAllBe(m => m.Status == OutboxStatus.Succeeded);
    }

    /// <summary>A nurse in the practitioner master, as the hospital administrator adds one; her id.</summary>
    private static async Task<Guid> NurseAsync(ApiClient hospitalAdmin, string? phone) =>
        (await hospitalAdmin.PostAsync<Created>("/api/v1/masters/practitioners", Nurse("RPMN" + Digits(8), phone, active: true, version: null))).Id;

    /// <summary>The nurse leaves the hospital: the administrator marks her inactive in the practitioner master.</summary>
    private static async Task LeavesAsync(ApiClient hospitalAdmin, Guid id)
    {
        var nurse = await hospitalAdmin.GetAsync<PractitionerDto>($"/api/v1/masters/practitioners/{id}");
        await hospitalAdmin.PutAsync($"/api/v1/masters/practitioners/{id}", Nurse(nurse.Code, nurse.Phone, active: false, nurse.Version));
    }

    private static SavePractitionerRequest Nurse(string code, string? phone, bool active, uint? version) => new(
        code, "Sr.", "Meena", "Rpm" + Letters(8), Gender.Female, Qualification: "B.Sc. Nursing", RegistrationCouncil: null, RegistrationNumber: null,
        SpecialtyId: null, DepartmentId: null, PractitionerKind.Nurse, phone, Email: null, ConsultationFeeNew: 0, ConsultationFeeFollowUp: 0,
        FollowUpValidityDays: 0, Languages: null, Bio: null, PhotoFileId: null, SignatureFileId: null, AvailableForOnlineBooking: false,
        AvailableForTelehealth: false, FacilityIds: null, active, version);

    private static Task<PatientCreated> RegisterPatientAsync(ApiClient frontOffice, string firstName) =>
        frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: firstName, MiddleName: null, LastName: "Monitor" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));

    /// <summary>The care-team messages queued for the patient's alerts, which name the patient by UHID.</summary>
    private async Task<List<(OutboxMessage Message, NotificationDelivery Delivery)>> AlertMessagesAsync(string uhid)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType || m.Type == NotificationService.WhatsAppType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(Ct);
        return messages.Select(m => (Message: m, Delivery: m.Read<NotificationDelivery>()))
            .Where(m => m.Delivery.TemplateKey == AlertTemplate && m.Delivery.Values.GetValueOrDefault("patient")?.Contains(uhid, StringComparison.Ordinal) == true)
            .ToList();
    }

    private async Task<List<OutboxMessage>> MessagesAsync(IReadOnlyCollection<Guid> ids)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => ids.Contains(m.Id)).ToListAsync(Ct);
    }

    private async Task<List<NotificationLog>> LogsAsync(IEnumerable<Guid> ids)
    {
        var wanted = ids.ToList();
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking()
            .Where(l => wanted.Contains(l.Id)).ToListAsync(Ct);
    }

    /// <summary>The message the simulator received for the notification log <paramref name="logId"/>, once the outbox sent it.</summary>
    private static Task<SimulatorMessageDto> DeliveredAsync(ApiClient admin, string channel, Guid logId) =>
        EventuallyAsync(async () => (await admin.GetAsync<PagedResult<SimulatorMessageDto>>(
                $"/api/v1/admin/integrations/simulator/messages?channel={channel}&search={logId:N}")).Items.FirstOrDefault(),
            $"The {channel} message was not delivered.");

    /// <summary>
    /// Polls until the outbox has done what <paramref name="read"/> waits for: it runs every two seconds, and waits 10 seconds
    /// after a first failure, 20 after a second, and so on, before it tries a message again.
    /// </summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, string failure)
        where T : class
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(2); waited += TimeSpan.FromSeconds(1))
        {
            if (await read() is { } value)
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        throw new ShouldAssertException(failure);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);

    /// <summary>
    /// SMS and WhatsApp, hospital-wide, for the rest of the test: off, then on to the integrations simulator. The settings
    /// are put back as they were afterwards, since the server is shared by every test of the run.
    /// </summary>
    private sealed class Messaging(ApiClient admin, Dictionary<string, JsonNode?> before) : IAsyncDisposable
    {
        private static readonly string[] Keys = ["integrations.sms", "integrations.whatsapp"];

        public static async Task<Messaging> TurnOffAsync(ApiClient admin)
        {
            var before = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var key in Keys)
            {
                before[key] = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
                    .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
            }

            var messaging = new Messaging(admin, before);
            await messaging.SaveAsync(_ => new JsonObject { ["enabled"] = false, ["provider"] = "simulator" });
            return messaging;
        }

        public Task TurnOnSimulatorAsync() => SaveAsync(_ => new JsonObject { ["enabled"] = true, ["provider"] = "simulator" });

        public async ValueTask DisposeAsync() => await SaveAsync(key => before[key]?.DeepClone() ?? new JsonObject { ["enabled"] = false });

        private async Task SaveAsync(Func<string, JsonNode> value)
        {
            foreach (var key in Keys)
            {
                await admin.SaveSettingAsync(key, new SaveSettingRequest(value(key), null, false, null));
            }
        }
    }
}
