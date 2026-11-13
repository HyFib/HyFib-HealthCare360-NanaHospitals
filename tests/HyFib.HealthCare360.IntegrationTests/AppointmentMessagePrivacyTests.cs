using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Services;
using HyFib.HealthCare360.Modules.VirtualCare.Services;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// An appointment message names the doctor, whose speciality can reveal the care (a psychiatrist, say), so a record in a
/// protected category gets none of them, and no video-consultation link, by SMS or WhatsApp on a mobile number another
/// patient shares (<c>SharedNumbers</c>). A patient on a number of their own is told as before. Read from the outbox the
/// real API fills, over PostgreSQL.
/// </summary>
public sealed class AppointmentMessagePrivacyTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_protected_record_gets_no_appointment_message_on_a_number_another_patient_shares_while_other_patients_are_told()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var (protectedId, sharedPhone) = await RegisterAsync(frontOffice, null);
        await RegisterAsync(frontOffice, sharedPhone);
        await PutInCategoryAsync(protectedId, SensitiveCategory.Mtp);
        var (controlId, controlPhone) = await RegisterAsync(frontOffice, null);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        var controlSlot = await TeleconsultationJourneyTests.FreeVideoSlotAsync(frontOffice, practitionerId);
        await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            controlId, practitionerId, controlSlot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true, "Follow-up of a rash", Notes: null));
        var protectedSlot = await TeleconsultationJourneyTests.FreeVideoSlotAsync(frontOffice, practitionerId);
        var booked = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            protectedId, practitionerId, protectedSlot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true, "Follow-up of a rash", Notes: null));

        booked.Id.ShouldNotBe(Guid.Empty, "The desk still books the appointment.");
        (await DeliveriesAsync(AppointmentNotifier.ConfirmedTemplate, controlPhone, since)).Select(d => d.Channel)
            .ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
        (await DeliveriesAsync(AppointmentNotifier.ConfirmedTemplate, sharedPhone, since))
            .ShouldBeEmpty("The message names the doctor, and the number is shared.");
        (await DeliveriesAsync(TelehealthLinkSender.Template, sharedPhone, since))
            .ShouldBeEmpty("The video link names the doctor too; the patient joins from the patient portal.");
    }

    /// <summary>The messages of <paramref name="key"/> queued to the number since then, on every channel.</summary>
    private async Task<List<NotificationDelivery>> DeliveriesAsync(string key, string phone, DateTimeOffset since)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => (m.Type == NotificationService.SmsType || m.Type == NotificationService.WhatsAppType) && m.CreatedAt >= since)
            .ToListAsync(Ct);
        return messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == key && d.To == phone).ToList();
    }

    private async Task PutInCategoryAsync(Guid patientId, SensitiveCategory category)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var patient = await db.Set<Patient>().SingleAsync(p => p.Id == patientId, Ct);
        patient.SensitiveCategories |= category;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A new patient on a new number, or on <paramref name="phone"/>; the id and the number (E.164).</summary>
    private static async Task<(Guid PatientId, string Phone)> RegisterAsync(ApiClient frontOffice, string? phone)
    {
        var mobile = phone ?? "9" + Digits(9);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: TestPatients.FirstName("Selvi"), MiddleName: null, LastName: "Privacy" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: mobile, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return (created.Id, Phone.Normalize(mobile)!);
    }

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);
}
