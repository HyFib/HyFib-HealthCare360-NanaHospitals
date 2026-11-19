using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The doctor starts the consultation from the worklist with one call: starting the encounter for a visit starts the visit
/// and puts its queue token in service in the same transaction, so the waiting-area board and the worklist show the patient
/// with the doctor. A token start that races it (the front desk's queue screen, or a staff app still open from before the
/// upgrade, which sent both calls at once) fails neither the encounter nor the token.
/// </summary>
public sealed class ConsultationStartTests(HospitalServer server)
{
    private const string Encounters = "/api/v1/emr/encounters";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(TokenStatus.Waiting)]
    [InlineData(TokenStatus.Called)]
    public async Task Starting_the_encounter_starts_the_visit_and_puts_its_token_in_service(TokenStatus before)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var visit = await CheckInAsync(frontOffice, doctor);
        var token = visit.Token.ShouldNotBeNull();
        if (before == TokenStatus.Called)
        {
            (await doctor.PostAsync<QueueTokenDto>($"/api/v1/queue/{token.Id}/call", new CallTokenRequest("Room 4"))).Status.ShouldBe(TokenStatus.Called);
        }

        using var started = await doctor.SendRawAsync(HttpMethod.Post, Encounters, Start(visit));

        started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync(Ct));
        var seen = await doctor.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}");
        seen.Status.ShouldBe(VisitStatus.InProgress);
        var inService = seen.Token.ShouldNotBeNull();
        inService.Status.ShouldBe(TokenStatus.InService);
        inService.ServiceStartedAt.ShouldNotBeNull();
        (await EncountersOfAsync(visit.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_token_start_racing_the_encounter_start_fails_neither()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var visit = await CheckInAsync(frontOffice, doctor);
        var token = visit.Token.ShouldNotBeNull();

        // Both calls are caught between their reads and their writes, as the worklist's two calls were when they went
        // together: a transaction of the test holds the visit's row, which both write. The token start comes first.
        await using var hold = await HeldTransaction.HoldAsync(server, $"SELECT 1 FROM patients.visits WHERE id = {visit.Id} FOR UPDATE");
        var tokenStart = doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/queue/{token.Id}/start");
        await hold.WaitUntilBlockingAsync(server, 1);
        var encounterStart = doctor.SendRawAsync(HttpMethod.Post, Encounters, Start(visit));
        await hold.WaitUntilBlockingAsync(server, 2);
        await hold.ReleaseAsync();

        // The token start is saved first; the encounter start then finds the visit and the token started, and opens the note.
        using var startedToken = await tokenStart;
        startedToken.StatusCode.ShouldBe(HttpStatusCode.OK, await startedToken.Content.ReadAsStringAsync(Ct));
        using var startedEncounter = await encounterStart;
        startedEncounter.StatusCode.ShouldBe(HttpStatusCode.Created, await startedEncounter.Content.ReadAsStringAsync(Ct));
        var seen = await doctor.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}");
        seen.Status.ShouldBe(VisitStatus.InProgress);
        seen.Token.ShouldNotBeNull().Status.ShouldBe(TokenStatus.InService);
        (await EncountersOfAsync(visit.Id)).ShouldBe(1);
    }

    private static StartEncounterRequest Start(VisitDetail visit) =>
        new(visit.PatientId, visit.Id, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: null);

    /// <summary>The encounters saved for the visit, as the database has them.</summary>
    private async Task<int> EncountersOfAsync(Guid visitId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM clinical.encounters WHERE visit_id = {visitId}""").SingleAsync(Ct);
    }

    /// <summary>Front office: checks a new patient in to see the doctor, with a queue token (waiting).</summary>
    private static async Task<VisitDetail> CheckInAsync(ApiClient frontOffice, ApiClient doctor)
    {
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient.Id, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Fever",
            false, null, null, TokenPriority.Normal, IssueToken: true, NotifyPatient: false));
        visit.Token.ShouldNotBeNull().Status.ShouldBe(TokenStatus.Waiting);
        return visit;
    }
}
