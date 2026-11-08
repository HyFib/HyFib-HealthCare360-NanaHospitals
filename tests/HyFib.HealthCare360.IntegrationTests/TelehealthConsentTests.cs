using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Features.Portal;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A video consultation starts only with the patient's consent to be seen by video on record (Telemedicine Practice
/// Guidelines 2020): the doctor can neither start the call nor admit the patient from the waiting room without it. The
/// consent is taken by voice at the start of the call and recorded by the doctor, at the desk, or on the portal when the
/// patient books, which will not book a video consultation without it. A withdrawn consent stops the next call.
/// </summary>
public sealed class TelehealthConsentTests(HospitalServer server)
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_consent_the_doctor_cannot_start_or_admit_and_the_consent_given_by_voice_lets_the_call_start()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var session = await BookByPhoneAsync(frontOffice, doctor, patient.Id);

        // Nothing on record: the session says so, and neither starting nor admitting goes ahead.
        (await doctor.GetAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}")).TelehealthConsentGivenAt.ShouldBeNull();
        (await RefusalAsync(doctor, $"/api/v1/telehealth/sessions/{session.Id}/start")).ShouldBe(
            (HttpStatusCode.UnprocessableEntity, TelehealthConsents.RequiredCode));
        await using (var hub = await LongPollingHub.ConnectAsync(doctor, TelehealthHub.Path))
        {
            (await hub.InvokeAsync("JoinAsClinician", session.Id)).ShouldBeNull();
            (await hub.InvokeAsync("Admit", session.Id, null)).ShouldNotBeNull().ShouldContain("has not consented to a video consultation");
        }

        (await doctor.GetAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}")).Status.ShouldBe(TelehealthStatus.Scheduled);

        // The doctor notes what the patient was told and that they agreed; it goes on the patient's consents as the doctor's.
        (await RefusalAsync(doctor, $"/api/v1/telehealth/sessions/{session.Id}/telehealth-consent", new TelehealthConsentRequest(" ")))
            .Status.ShouldBe(HttpStatusCode.BadRequest);
        var recorded = await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}/telehealth-consent",
            new TelehealthConsentRequest("Explained the video consultation and its limits; the patient agreed."));
        recorded.TelehealthConsentGivenAt.ShouldNotBeNull();
        var consent = (await ConsentsAsync(frontOffice, patient.Id)).ShouldHaveSingleItem();
        (consent.Type, consent.Granted, consent.IsActive).ShouldBe((ConsentType.Telehealth, true, true));
        consent.Notes.ShouldNotBeNull().ShouldContain("Given by voice to the doctor at the start of the video consultation");
        consent.Notes.ShouldContain("the patient agreed");
        consent.CapturedByName.ShouldNotBeNullOrWhiteSpace();

        // Recording it again adds nothing; the call now starts.
        await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}/telehealth-consent",
            new TelehealthConsentRequest("Asked again."));
        (await ConsentsAsync(frontOffice, patient.Id)).Count.ShouldBe(1);
        await using (var hub = await LongPollingHub.ConnectAsync(doctor, TelehealthHub.Path))
        {
            (await hub.InvokeAsync("JoinAsClinician", session.Id)).ShouldBeNull();
            (await hub.InvokeAsync("Admit", session.Id, null)).ShouldNotBeNull().ShouldContain("Nobody is waiting to be admitted.", Case.Sensitive, "the consent check is passed");
        }

        var started = await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}/start");
        started.Status.ShouldBe(TelehealthStatus.InProgress);

        // Withdrawn at the desk: the call under way goes on, the next one does not start.
        await frontOffice.PostAsync($"/api/v1/patients/consents/{consent.Id}/revoke", new RevokeConsentRequest("Prefers to come in person"));
        (await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}/start")).Status.ShouldBe(TelehealthStatus.InProgress);
        var next = await BookByPhoneAsync(frontOffice, doctor, patient.Id, after: session.ScheduledStartAt);
        (await RefusalAsync(doctor, $"/api/v1/telehealth/sessions/{next.Id}/start")).Code.ShouldBe(TelehealthConsents.RequiredCode);

        // Taken at the desk instead: the next call starts.
        await frontOffice.PostAsync<JsonElement>($"/api/v1/patients/{patient.Id}/consents",
            new CaptureConsentRequest(ConsentType.Telehealth, null, true, null, null, null, null, "Agreed at the desk"));
        (await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{next.Id}/start")).Status.ShouldBe(TelehealthStatus.InProgress);

        foreach (var id in new[] { session.Id, next.Id })
        {
            await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{id}/end", new EndTelehealthSessionRequest(QualityNotes: null));
        }
    }

    [Fact]
    public async Task The_portal_books_a_video_consultation_only_with_the_patients_consent_and_records_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var phone = (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).Phone.ShouldNotBeNull();
        var portal = await server.SignInToPortalAsync(phone);
        var slot = await TeleconsultationJourneyTests.FreeVideoSlotAsync(frontOffice, practitionerId);

        // Without the patient's agreement the video consultation is not booked.
        var request = new PortalBookAppointmentRequest(
            patient.Id, practitionerId, slot.StartsAt, AppointmentVisitType.New, IsTelehealth: true, "Rash on the arm", PayOnline: false);
        using (var refused = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/portal/appointments", request))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("Agree to be seen by video");
        }

        (await ConsentsAsync(frontOffice, patient.Id)).ShouldBeEmpty();

        var booked = await portal.PostAsync<PortalBookingResult>("/api/v1/portal/appointments", request with { TelehealthConsent = true });
        var consent = (await ConsentsAsync(frontOffice, patient.Id)).ShouldHaveSingleItem();
        (consent.Type, consent.IsActive).ShouldBe((ConsentType.Telehealth, true));
        consent.Notes.ShouldBe($"Given by the patient on the portal when booking video consultation {booked.Appointment.Number}.");

        // The doctor may start the call the portal booked.
        var session = await SessionOfAsync(doctor, practitionerId, booked.Appointment.Id, slot.StartsAt);
        session.TelehealthConsentGivenAt.ShouldNotBeNull();
        (await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}/start")).Status.ShouldBe(TelehealthStatus.InProgress);
        await doctor.PostAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{session.Id}/end", new EndTelehealthSessionRequest(QualityNotes: null));
    }

    /// <summary>The front office books a video consultation over the phone (no consent taken); the session the booking made.</summary>
    private static async Task<TelehealthSessionDetail> BookByPhoneAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId, DateTimeOffset? after = null)
    {
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var slot = await TeleconsultationJourneyTests.FreeVideoSlotAsync(frontOffice, practitionerId);
        if (after is { } taken && slot.StartsAt <= taken)
        {
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IndiaOffset).DateTime);
            var availability = await frontOffice.GetAsync<List<Modules.Scheduling.Features.Availability.PractitionerAvailability>>(
                $"/api/v1/scheduling/slots?practitionerId={practitionerId}&from={today:O}&to={today.AddDays(6):O}&mode=Telehealth&includeFull=false");
            slot = availability.SelectMany(a => a.Slots).Where(s => s.Available > 0 && s.StartsAt > taken).MinBy(s => s.StartsAt).ShouldNotBeNull();
        }

        var appointment = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            patientId, practitionerId, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: true, "Skin rash", Notes: null));
        return await SessionOfAsync(doctor, practitionerId, appointment.Id, slot.StartsAt);
    }

    private static async Task<TelehealthSessionDetail> SessionOfAsync(ApiClient doctor, Guid practitionerId, Guid appointmentId, DateTimeOffset startsAt)
    {
        var day = DateOnly.FromDateTime(startsAt.ToOffset(IndiaOffset).DateTime);
        var item = (await doctor.GetAsync<List<TelehealthSessionListItem>>($"/api/v1/telehealth/sessions?date={day:O}&practitionerId={practitionerId}"))
            .Single(s => s.AppointmentId == appointmentId);
        return await doctor.GetAsync<TelehealthSessionDetail>($"/api/v1/telehealth/sessions/{item.Id}");
    }

    private static async Task<List<ConsentDto>> ConsentsAsync(ApiClient api, Guid patientId) =>
        [.. (await api.GetAsync<List<ConsentDto>>($"/api/v1/patients/{patientId}/consents")).Where(c => c.Type == ConsentType.Telehealth)];

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object? body = null)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.IsSuccessStatusCode.ShouldBeFalse();
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }
}
