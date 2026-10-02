using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Features.Availability;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A video consultation over the real API (TEL-01, TEL-04): the front office books it and the booking sends the join
/// link; the doctor starts the call, which opens the telehealth visit, and signs the e-prescription during the call. The
/// patient then hears of the prescription once on each channel: on WhatsApp from Clinical, which tells every out-patient,
/// and by SMS from VirtualCare, which adds it after a video consultation.
/// </summary>
public sealed class TeleconsultationJourneyTests(HospitalServer server)
{
    private const string PrescriptionShared = "prescription.shared";
    private const string ParacetamolCode = "DRG-PCM500";
    private const int CourseDays = 3;

    /// <summary>India Standard Time, the hospital's calendar (no daylight saving).</summary>
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    [Fact]
    public async Task A_video_consultation_prescription_reaches_the_patient_once_on_whatsapp_and_once_by_sms()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();

        // Front office: registers the patient and books a video consultation with the doctor over the phone.
        var patient = await RegisterPatientAsync(frontOffice);
        var slot = await FreeVideoSlotAsync(frontOffice, practitionerId);
        var appointment = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            patient.Id, practitionerId, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true,
            "Cough and cold for a week", Notes: null));
        appointment.IsTelehealth.ShouldBeTrue();

        // The booking created the video session and sent the patient the join link.
        var day = DateOnly.FromDateTime(slot.StartsAt.ToOffset(IndiaOffset).DateTime);
        var session = (await doctor.GetAsync<List<TelehealthSessionListItem>>($"/api/v1/telehealth/sessions?date={day:O}&practitionerId={practitionerId}"))
            .Single(s => s.AppointmentId == appointment.Id);
        session.Status.ShouldBe(TelehealthStatus.Scheduled);
        session.LinkSent.ShouldBeTrue("The join link goes out with the booking.");

        // Doctor: starting the call opens the telehealth visit and puts the appointment in consultation.
        var started = await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}/start");
        started.Status.ShouldBe(TelehealthStatus.InProgress);
        var visitId = started.VisitId.ShouldNotBeNull("Starting the call creates the visit the doctor documents and prescribes against.");
        (await doctor.GetAsync<VisitDetail>($"/api/v1/visits/{visitId}")).Type.ShouldBe(VisitType.Telehealth);
        (await doctor.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{appointment.Id}")).Status.ShouldBe(AppointmentStatus.InConsultation);

        // During the call: opens the encounter from the visit, prescribes paracetamol and signs the e-prescription.
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.Id, visitId, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: null));
        encounter.Type.ShouldBe(EncounterType.Telehealth);
        var drug = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Paracetamol")).Single(d => d.Code == ParacetamolCode);
        var prescription = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patient.Id, encounter.Id, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null,
            Advice: "Steam inhalation twice a day.", RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    drug.Id, drug.Name, drug.GenericName, drug.Strength, drug.DosageForm, Route: "Oral", Dose: "1 tablet", DoseFrequency.TDS,
                    CustomFrequency: null, DosesPerDay: null, CourseDays, Quantity: null, Instructions: "After food", drug.IsControlled,
                    drug.ControlledSchedule),
            ]));
        prescription.VisitId.ShouldBe(visitId, "A prescription written in the encounter belongs to the telehealth visit.");
        var signed = await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{prescription.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: false, ConfirmControlledDrugs: false, prescription.Version));
        signed.Prescription.Status.ShouldBe(PrescriptionStatus.Signed);

        // Ends the call.
        (await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}/end", new EndTelehealthSessionRequest(QualityNotes: null)))
            .Status.ShouldBe(TelehealthStatus.Completed);

        // The patient hears of the prescription once on WhatsApp and once by SMS.
        var notices = await PrescriptionNoticesAsync(patient.Id);
        notices.Count(c => c == NotificationChannel.WhatsApp).ShouldBe(1, "Clinical's WhatsApp message, which VirtualCare must not send again.");
        notices.Count(c => c == NotificationChannel.Sms).ShouldBe(1, "VirtualCare's SMS after a video consultation.");
        notices.Count.ShouldBe(2);
    }

    private static Task<PatientCreated> RegisterPatientAsync(ApiClient frontOffice) =>
        frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: "Lakshmi", MiddleName: null, LastName: "Video" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));

    /// <summary>
    /// The doctor's first free video slot in the coming week that starts late enough to book. Any day will do: unlike a
    /// clinic visit, which is checked in on its day, a video consultation can be started at any time.
    /// </summary>
    private static async Task<SlotDto> FreeVideoSlotAsync(ApiClient frontOffice, Guid practitionerId)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IndiaOffset).DateTime);
        var availability = await frontOffice.GetAsync<List<PractitionerAvailability>>(
            $"/api/v1/scheduling/slots?practitionerId={practitionerId}&from={today:O}&to={today.AddDays(6):O}&mode=Telehealth&includeFull=false");
        var earliest = DateTimeOffset.UtcNow.AddMinutes(2);
        return availability.SelectMany(a => a.Slots).Where(s => s.Available > 0 && s.StartsAt > earliest).MinBy(s => s.StartsAt)
               ?? throw new InvalidOperationException("The doctor has no free video slot in the coming week.");
    }

    /// <summary>The channel of each <c>prescription.shared</c> message queued for the patient.</summary>
    private async Task<List<NotificationChannel>> PrescriptionNoticesAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<NotificationLog>().AsNoTracking()
            .Where(l => l.PatientId == patientId && l.TemplateKey == PrescriptionShared)
            .Select(l => l.Channel)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
