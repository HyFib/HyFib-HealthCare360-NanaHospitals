using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A theatre case over the real API for the maker-checker tests of slice 10: a patient at the emergency department, booked
/// into a slot far ahead (where nothing else holds the theatre or the team) with the doctor operating and the second doctor
/// giving the anaesthetic, and the steps of the day of surgery as the theatre team takes them.
/// </summary>
internal sealed class TheatreCases(HospitalServer server)
{
    public const string Bookings = "/api/v1/ot/bookings";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Case(Guid BookingId, Guid PatientId, Guid VisitId, string Path);

    /// <summary>The case booked and the patient received in pre-op; no consent is recorded yet.</summary>
    public async Task<Case> InPreOpAsync(
        Laterality laterality, string procedure = "Inguinal hernia repair", string? level = null, SurgeryPriority priority = SurgeryPriority.Elective)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var c = await BookAsync(patientId, visit.Id, laterality, procedure, level, priority);
        await nurse.PostAsync($"{c.Path}/receive");
        return c;
    }

    /// <summary>
    /// The case booked into a slot and left there, not received in pre-op (a booking a slot is still moved for); no consent
    /// is recorded yet.
    /// </summary>
    public async Task<Case> BookedAsync(string procedure, SurgeryPriority priority = SurgeryPriority.Elective)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        return await BookAsync(patientId, visit.Id, Laterality.NotApplicable, procedure, null, priority);
    }

    /// <summary>
    /// Another operation for the patient of <paramref name="c"/>, booked now into a slot of its own (and left there, not
    /// received in pre-op); no consent is recorded for it.
    /// </summary>
    public Task<Case> AnotherOperationAsync(Case c, string procedure) => BookAsync(c.PatientId, c.VisitId, Laterality.NotApplicable, procedure, null, SurgeryPriority.Elective);

    /// <summary>
    /// The doctor books the operation into a slot, the doctor operating and the second doctor giving a spinal anaesthetic: a
    /// random slot years ahead, where nothing else of the run should be. The surgeon and anaesthetist are the same in every
    /// case, so on the rare clash with another case (of another test, or of the same patient) another slot is tried.
    /// </summary>
    private async Task<Case> BookAsync(Guid patientId, Guid visitId, Laterality laterality, string procedure, string? level, SurgeryPriority priority)
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var surgeonId = (await doctor.GetAsync<AuthUser>("/api/v1/auth/me")).PractitionerId.ShouldNotBeNull();
        var anaesthetistId = (await (await server.SignInAsync(Users.SecondDoctor)).GetAsync<AuthUser>("/api/v1/auth/me")).PractitionerId.ShouldNotBeNull();
        var theatre = (await doctor.GetAsync<List<TheatreDto>>("/api/v1/ot/theatres")).First();
        SurgeryCreated? booking = null;
        for (var attempt = 0; booking is null; attempt++)
        {
            var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(400 + Random.Shared.Next(1500)).AddMinutes(30 * Random.Shared.Next(40)), TimeSpan.Zero);
            using var response = await doctor.SendRawAsync(HttpMethod.Post, Bookings, new CreateSurgeryBookingRequest(
                PatientId: patientId, AdmissionId: null, VisitId: visitId, ProcedureServiceItemId: null, ProcedureCode: null, ProcedureName: procedure,
                Laterality: laterality, Diagnosis: "For the theatre's checks", SurgeonId: surgeonId, AssistantIds: null, AnaesthetistId: anaesthetistId,
                AnaesthesiaType: AnaesthesiaType.Spinal, Priority: priority, PreferredDate: null, TheatreId: theatre.Id,
                ScheduledStart: start, ScheduledEnd: start.AddMinutes(90), Notes: null, SiteLevel: level));
            if (response.StatusCode == HttpStatusCode.Conflict && attempt < 5)
            {
                continue;
            }

            booking = await ApiClient.ReadAsync<SurgeryCreated>(response, HttpMethod.Post, Bookings);
        }

        return new Case(booking.Id, patientId, visitId, $"{Bookings}/{booking.Id}");
    }

    /// <summary>A doctor who may book surgery and operate, but is on no team of these cases (an account with no practitioner record).</summary>
    public async Task<ApiClient> OutsideDoctorAsync()
    {
        var userName = "otdoc" + Guid.NewGuid().ToString("N")[..8];
        await server.CreateStaffAccountAsync(userName, HyFib.HealthCare360.Platform.Security.SystemRoles.Doctor);
        return await server.SignInAsync(userName);
    }

    /// <summary>
    /// The front office records the patient's consent to the operation (a high-risk procedure) and to the anaesthesia, as
    /// the theatre's consent gates ask (<see cref="SurgicalConsent"/>): the two consents' ids.
    /// </summary>
    public async Task<(Guid Procedure, Guid Anaesthesia)> ConsentedAsync(Case c) =>
        (await ConsentAsync(c, ConsentType.HighRiskProcedure), await ConsentAsync(c, ConsentType.Anaesthesia));

    /// <summary>
    /// The front office records one consent of the patient's, given (or, with <paramref name="granted"/> false, refused),
    /// with the signed form's witness named, and the signed form's file when one is given.
    /// </summary>
    public async Task<Guid> ConsentAsync(Case c, ConsentType type, bool granted = true, Guid? documentFileId = null)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        return (await frontOffice.PostAsync<Created>($"/api/v1/patients/{c.PatientId}/consents",
            new CaptureConsentRequest(type, null, granted, "Staff nurse Asha", null, null, documentFileId,
                granted ? "Explained by the surgeon" : "Refuses after the surgeon explained"))).Id;
    }

    /// <summary>
    /// The patient's consents are recorded at the front office (unless they already stand, or were waived in an emergency),
    /// the surgeon marks the site (when one is booked) and the ward nurse completes the pre-op checklist.
    /// </summary>
    public async Task PreOpDoneAsync(Case c)
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var detail = await doctor.GetAsync<SurgeryDetail>(c.Path);
        if (detail.Consent?.Satisfied != true)
        {
            await ConsentedAsync(c);
        }

        if (detail.SiteMarkingRequired)
        {
            await doctor.PutAsync($"{c.Path}/pre-op-checklist",
                new RecordChecklistRequest([new ChecklistInput(SiteMarking.Item, ChecklistItemState.Done, null)], detail.Version));
            detail = await doctor.GetAsync<SurgeryDetail>(c.Path);
        }

        var items = detail.PreOpChecklist.Select(i => new ChecklistInput(i.Key, ChecklistItemState.Done, null)).ToList();
        await nurse.PutAsync($"{c.Path}/pre-op-checklist", new RecordChecklistRequest(items, detail.Version));
    }

    /// <summary>The nurse reads out the WHO Sign-in and Time-out, every item confirmed.</summary>
    public async Task SignedInAndTimedOutAsync(Case c)
    {
        var nurse = await server.SignInAsync(Users.Nurse);
        foreach (var (phase, route) in new[] { (SafetyPhase.SignIn, "sign-in"), (SafetyPhase.TimeOut, "time-out") })
        {
            await nurse.PostAsync($"{c.Path}/safety-checklist/{route}", AllDone(await nurse.GetAsync<SurgeryDetail>(c.Path), phase));
        }
    }

    /// <summary>Booked, pre-op done, signed in, timed out and knife to skin.</summary>
    public async Task<Case> UnderWayAsync(Laterality laterality = Laterality.Right, string procedure = "Inguinal hernia repair")
    {
        var c = await InPreOpAsync(laterality, procedure);
        await PreOpDoneAsync(c);
        await SignedInAndTimedOutAsync(c);
        await (await server.SignInAsync(Users.Doctor)).PostAsync($"{c.Path}/start");
        return c;
    }

    /// <summary>A count made by the nurse and witnessed by the second nurse at her screen, as sent (refused or not).</summary>
    public async Task<HttpResponseMessage> CountAsync(Case c, CountCategory category, CountStage stage, int number)
    {
        var nurse = server.FromOwnAddress(await server.SignInAsync(Users.Nurse));
        var witness = (await server.WitnessAsync(nurse, Users.SecondNurse, OtClinicalService.CountWitness.Key, c.PatientId)).ConfirmationId;
        return await nurse.SendRawAsync(HttpMethod.Post, $"{c.Path}/counts", new RecordSurgicalCountRequest(category, stage, number, witness));
    }

    public async Task CountedAsync(Case c, CountCategory category, CountStage stage, int number)
    {
        using var response = await CountAsync(c, category, stage, number);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
    }

    public static RecordChecklistRequest AllDone(SurgeryDetail detail, SafetyPhase phase) =>
        new(detail.SafetyChecklist.Single(p => p.Phase == phase).Items.Select(i => new ChecklistInput(i.Key, ChecklistItemState.Done, null)).ToList());

    public static UpdateSurgeryPlanRequest Plan(SurgeryDetail d) => new(
        d.ProcedureServiceItemId, d.ProcedureCode, d.ProcedureName, d.Laterality, d.Diagnosis, d.SurgeonId, d.AssistantIds, d.AnaesthetistId,
        d.AnaesthesiaType, d.Priority, d.PreferredDate, d.Notes, d.Version, d.NurseId, d.SiteLevel);

    /// <summary>The newest audit entry of <paramref name="action"/> on the patient: its summary and severity.</summary>
    public async Task<(string Summary, AuditSeverity Severity)> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Action == action)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.Summary, e.Severity })
            .FirstAsync(Ct);
        return (entry.Summary ?? string.Empty, entry.Severity);
    }

    public static async Task<ApprovalRequestDto> AcceptedAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, method, path);
    }

    public static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, HttpMethod method, string path, object? body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        return await RefusalOfAsync(response);
    }

    /// <summary>A body the request's validator refused (400): the fields it named.</summary>
    public static async Task<string[]> InvalidFieldsAsync(ApiClient api, HttpMethod method, string path, object? body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object
            ? [.. errors.EnumerateObject().Select(e => e.Name)]
            : [];
    }

    public static async Task<(HttpStatusCode Status, string? Code)> RefusalOfAsync(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.ShouldBeFalse(await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private sealed record Created(Guid Id);
}
