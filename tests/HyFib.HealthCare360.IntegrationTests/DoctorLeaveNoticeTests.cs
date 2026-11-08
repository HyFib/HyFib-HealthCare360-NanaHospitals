using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Features.Availability;
using HyFib.HealthCare360.Modules.Scheduling.Features.Schedules;
using HyFib.HealthCare360.Modules.Scheduling.Services;
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
/// A doctor's leave or block that strands bookings: the staff tell the patients, in one action, by SMS and WhatsApp that
/// the doctor is not available, with a link to choose another time on the portal; the list says who was told and how.
/// Nobody is told twice about the same leave, and nothing is rebooked or cancelled for them. Where SMS needs the
/// template's DLT id and it has none, the patients are told on WhatsApp alone, and the list says why.
/// </summary>
public sealed class DoctorLeaveNoticeTests(HospitalServer server)
{
    private const string PortalBase = "https://portal.leave-test.example.org";

    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_patients_a_block_strands_are_told_once_with_a_link_to_rebook_and_nothing_is_moved()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var doctor = await DoctorAsync();
        await using var portal = await SettingAsync(PortalAddress.BaseUrlSetting, PortalBase);

        var (patient, phone) = await RegisterAsync(frontOffice);
        var slot = await FreeSlotAsync(frontOffice, doctor.Id, daysAhead: 9);
        var appointment = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            patient.Id, doctor.Id, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: false, "Knee pain", Notes: null));

        // The doctor is blocked for the hour of the booking: the save lists it as stranded, nobody told yet.
        var saved = await BlockAsync(admin, doctor.Id, slot.StartsAt);
        try
        {
            saved.AffectedAppointments.ShouldContain(a => a.Id == appointment.Id);
            var before = await frontOffice.GetAsync<StrandedPatients>($"/api/v1/scheduling/exceptions/{saved.Id}/stranded");
            var row = before.Patients.Single(p => p.AppointmentId == appointment.Id);
            (row.ToldAt, row.Notices.Count, row.NotToldReason).ShouldBe(((DateTimeOffset?)null, 0, (string?)null));

            // One action tells them: SMS and WhatsApp queued through the outbox, with the link to choose another time.
            var told = await frontOffice.PostAsync<StrandedPatients>($"/api/v1/scheduling/exceptions/{saved.Id}/notify-patients");
            told.SmsReady.ShouldBeTrue();
            row = told.Patients.Single(p => p.AppointmentId == appointment.Id);
            row.ToldAt.ShouldNotBeNull();
            row.Notices.Select(n => n.Channel).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
            var sms = (await MessagesAsync(phone, NotificationService.SmsType)).ShouldHaveSingleItem();
            sms.Values["link"].ShouldBe($"{PortalBase}/portal/doctors/{doctor.Id}?reschedule={appointment.Id}");
            sms.Values["reference"].ShouldBe(appointment.Number);
            (await MessagesAsync(phone, NotificationService.WhatsAppType)).ShouldHaveSingleItem();

            // Asked again, nobody is told twice; the booking stays as it was for the patient or the desk to move.
            await frontOffice.PostAsync<StrandedPatients>($"/api/v1/scheduling/exceptions/{saved.Id}/notify-patients");
            (await MessagesAsync(phone, NotificationService.SmsType)).Count.ShouldBe(1);
            var kept = await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{appointment.Id}");
            (kept.Status, kept.StartsAt).ShouldBe((AppointmentStatus.Booked, appointment.StartsAt));

            // Staff who may not manage bookings cannot tell the patients.
            var lab = await server.SignInAsync(HospitalServer.Users.Lab);
            using var refused = await lab.SendRawAsync(HttpMethod.Post, $"/api/v1/scheduling/exceptions/{saved.Id}/notify-patients");
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await admin.SendRawAsync(HttpMethod.Delete, $"/api/v1/scheduling/exceptions/{saved.Id}");
            await frontOffice.PostAsync($"/api/v1/scheduling/appointments/{appointment.Id}/cancel", new CancelAppointmentRequest(CancellationReason.Other, "Booked for a test of leave notices", NotifyPatient: false));
        }
    }

    [Fact]
    public async Task Without_a_dlt_template_id_the_patients_are_told_on_whatsapp_alone_and_the_list_says_why()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var doctor = await DoctorAsync();
        var (patient, phone) = await RegisterAsync(frontOffice);
        var slot = await FreeSlotAsync(frontOffice, doctor.Id, daysAhead: 10);
        var appointment = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            patient.Id, doctor.Id, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: false, "Back pain", Notes: null));
        var saved = await BlockAsync(admin, doctor.Id, slot.StartsAt);

        // MSG91 sends only SMS registered on DLT; the seeded template has no DLT id.
        await using (await SettingAsync("integrations.sms", JsonSerializer.SerializeToElement(new { enabled = true, provider = "msg91" })))
        {
            try
            {
                var told = await frontOffice.PostAsync<StrandedPatients>($"/api/v1/scheduling/exceptions/{saved.Id}/notify-patients");
                told.SmsReady.ShouldBeFalse();
                told.SmsNote.ShouldNotBeNull().ShouldContain("no DLT template id");
                told.Patients.Single(p => p.AppointmentId == appointment.Id).Notices.ShouldHaveSingleItem().Channel.ShouldBe(NotificationChannel.WhatsApp);
                (await MessagesAsync(phone, NotificationService.SmsType)).ShouldBeEmpty();
                (await MessagesAsync(phone, NotificationService.WhatsAppType)).ShouldHaveSingleItem();
            }
            finally
            {
                await admin.SendRawAsync(HttpMethod.Delete, $"/api/v1/scheduling/exceptions/{saved.Id}");
                await frontOffice.PostAsync($"/api/v1/scheduling/appointments/{appointment.Id}/cancel", new CancelAppointmentRequest(CancellationReason.Other, "Booked for a test of leave notices", NotifyPatient: false));
            }
        }
    }

    private async Task<PractitionerDto> DoctorAsync()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        return await frontOffice.GetAsync<PractitionerDto>($"/api/v1/masters/practitioners/{practitionerId}");
    }

    /// <summary>A block of the doctor's hour around the booking, in every branch.</summary>
    private static Task<ScheduleSaved> BlockAsync(ApiClient admin, Guid practitionerId, DateTimeOffset startsAt)
    {
        var local = startsAt.ToOffset(IndiaOffset);
        var start = TimeOnly.FromDateTime(local.DateTime);
        return admin.PostAsync<ScheduleSaved>("/api/v1/scheduling/exceptions", new SaveScheduleExceptionRequest(
            practitionerId, ScheduleExceptionType.Block, DateOnly.FromDateTime(local.DateTime), DateOnly.FromDateTime(local.DateTime),
            start, start.AddMinutes(60), "Called to an emergency surgery", AllFacilities: true, DepartmentId: null, SlotMinutes: null,
            CapacityPerSlot: null, Mode: null, Room: null, Version: null));
    }

    /// <summary>The doctor's first free clinic slot from <paramref name="daysAhead"/> days on, away from other tests' days.</summary>
    private static async Task<SlotDto> FreeSlotAsync(ApiClient frontOffice, Guid practitionerId, int daysAhead)
    {
        var from = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IndiaOffset).DateTime).AddDays(daysAhead);
        var availability = await frontOffice.GetAsync<List<PractitionerAvailability>>(
            $"/api/v1/scheduling/slots?practitionerId={practitionerId}&from={from:O}&to={from.AddDays(6):O}&mode=InPerson&includeFull=false");
        return availability.SelectMany(a => a.Slots).Where(s => s.Available > 0).MinBy(s => s.StartsAt)
               ?? throw new InvalidOperationException("The doctor has no free slot that week.");
    }

    private static async Task<(PatientCreated Patient, string Phone)> RegisterAsync(ApiClient frontOffice)
    {
        var phone = "8" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10)));
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: TestPatients.FirstName("Meena"), MiddleName: null,
            LastName: "Leave" + new string([.. Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))]), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null,
            GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null,
            Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null,
            PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return (created, phone);
    }

    // The test server sends no SMS or WhatsApp: the messages wait in the outbox.
    private async Task<List<NotificationDelivery>> MessagesAsync(string phone, string type)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var to = Phone.Normalize(phone);
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == type)
            .OrderByDescending(m => m.CreatedAt)
            .Take(300)
            .ToListAsync(Ct);
        return [.. messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == StrandedPatientNotifier.Template && d.To == to)];
    }

    /// <summary>A setting for this test only, put back as it was afterwards.</summary>
    private async Task<IAsyncDisposable> SettingAsync<T>(string key, T value)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var before = await settings.GetAsync<JsonElement?>(key, cancellationToken: Ct);
        await settings.SetAsync(key, value, cancellationToken: Ct);
        return new Restore(async () =>
        {
            await using var cleanup = server.Services.CreateAsyncScope();
            var restore = cleanup.ServiceProvider.GetRequiredService<ISettingsService>();
            if (before is { } previous)
            {
                await restore.SetAsync(key, previous, cancellationToken: CancellationToken.None);
            }
            else
            {
                await restore.RemoveAsync(key, null, CancellationToken.None);
            }
        });
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
