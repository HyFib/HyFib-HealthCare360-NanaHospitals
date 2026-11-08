using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Cancelling a visit after the doctor started it (MAKER-CHECKER.md, slice 15): while the doctor has not started a visit
/// the desk cancels it at once; once the consultation is under way, or the visit has an encounter, a note, an order or a
/// prescription, the desk only asks, with a reason, and a second person decides: a front-office supervisor (here the
/// hospital administrator) or the treating doctor, never the one who asked, and not a doctor who does not treat the
/// patient. Until then nothing changes; once approved the token, the unbilled charges and the orders not yet done follow
/// the cancellation as they always did.
/// </summary>
public sealed class VisitCancelApprovalTests(HospitalServer server)
{
    private const string Cbc = "LAB-CBC";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_visit_the_doctor_started_waits_for_the_treating_doctor_and_then_its_token_charges_and_orders_follow()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var otherDoctor = await server.SignInAsync(Users.SecondDoctor);
        var patientId = await RegisterPatientAsync(frontOffice);
        var visit = await CheckInAsync(frontOffice, doctor, patientId);

        // The doctor opens the encounter and orders a blood count.
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, visit.Id, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "Fever"));
        var cbc = (await doctor.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?search={Cbc}")).Single(s => s.Code == Cbc);
        var order = await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(patientId, encounter.Id, VisitId: null, AdmissionId: null,
            OrderPriority.Routine, "Fever", [new OrderLineRequest(cbc.Id, null)], OrderSetId: null, ConfirmDuplicates: true));

        // The desk asks; nothing changes yet, and the request says what will.
        var asked = await AskAsync(frontOffice, visit.Id, "Patient wants to go to another hospital");
        (asked.Kind, asked.SubjectId, asked.Reason).ShouldBe((VisitService.CancelKind, visit.Id, "Patient wants to go to another hospital"));
        asked.Details.Single(d => d.Label == "Status").Value.ShouldBe("In consultation → cancelled");
        asked.Details.Single(d => d.Label == "Clinical record").Value.ShouldBe("1 encounter (not signed), 1 order line");
        var waiting = await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}");
        waiting.Status.ShouldBe(VisitStatus.InProgress);
        waiting.Token.ShouldNotBeNull().Status.ShouldNotBe(TokenStatus.Cancelled);
        (await doctor.GetAsync<OrderDto>($"/api/v1/clinical/orders/{order.Id}")).Items.ShouldHaveSingleItem().Status.ShouldBe(OrderItemStatus.Ordered);
        (await UnbilledChargesAsync(visit.Id)).ShouldBeGreaterThan(0);

        // Asked once only; the one who asked cannot decide, nor can a doctor who does not treat the patient.
        (await RefusalAsync(frontOffice, $"/api/v1/visits/{visit.Id}/cancel", new CancelVisitRequest("Again"))).ShouldBe(
            (HttpStatusCode.Conflict, "approval.already_requested"));
        (await RefusalAsync(frontOffice, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await RefusalAsync(otherDoctor, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe(
            (HttpStatusCode.Forbidden, "approval.not_treating_doctor"));
        (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).Status.ShouldBe(VisitStatus.InProgress, "still waiting");

        // The treating doctor approves: the visit, its token, its unbilled charges and the blood count not yet drawn go.
        var pending = await doctor.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        pending.Status.ShouldBe(ApprovalStatus.Pending);
        await MakerChecker.ApproveAsync(doctor, pending);
        var cancelled = await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}");
        (cancelled.Status, cancelled.CancelReason).ShouldBe((VisitStatus.Cancelled, "Patient wants to go to another hospital"));
        cancelled.Token.ShouldNotBeNull().Status.ShouldBe(TokenStatus.Cancelled);
        var line = (await doctor.GetAsync<OrderDto>($"/api/v1/clinical/orders/{order.Id}")).Items.ShouldHaveSingleItem();
        (line.Status, line.CancelReason).ShouldBe((OrderItemStatus.Cancelled, "Visit cancelled: Patient wants to go to another hospital"));
        (await UnbilledChargesAsync(visit.Id)).ShouldBe(0);

        // Audited with both names.
        var audit = await AuditAsync("patients.visit.cancelled", visit.Id);
        audit.Summary.ShouldNotBeNull().ShouldContain("asked for by");
        audit.Summary.ShouldContain("approved by");
    }

    [Fact]
    public async Task A_supervisor_approves_a_visit_with_a_consultation_under_way_and_one_not_started_is_cancelled_at_once()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var admin = await server.SignInAsync(Users.HospitalAdmin);

        // Not started: at once, as before.
        var waitingVisit = await CheckInAsync(frontOffice, doctor, await RegisterPatientAsync(frontOffice));
        using (var atOnce = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/visits/{waitingVisit.Id}/cancel", new CancelVisitRequest("Checked in by mistake")))
        {
            atOnce.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The doctor started the token (no note yet): it waits, and the front-office supervisor approves.
        var patientId = await RegisterPatientAsync(frontOffice);
        var visit = await CheckInAsync(frontOffice, doctor, patientId);
        await doctor.PostAsync<JsonElement>($"/api/v1/queue/{visit.Token!.Id}/start");
        var asked = await AskAsync(frontOffice, visit.Id, "Patient left without being seen");
        asked.Details.Single(d => d.Label == "Clinical record").Value.ShouldBe("Nothing recorded yet");
        await MakerChecker.ApproveAsync(admin, asked);
        (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).Status.ShouldBe(VisitStatus.Cancelled);
    }

    [Fact]
    public async Task A_visit_completed_while_its_cancellation_waited_is_not_cancelled()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var patientId = await RegisterPatientAsync(frontOffice);
        var visit = await CheckInAsync(frontOffice, doctor, patientId);
        await doctor.PostAsync<JsonElement>($"/api/v1/queue/{visit.Token!.Id}/start");
        var asked = await AskAsync(frontOffice, visit.Id, "Asked by mistake");

        await doctor.PostAsync($"/api/v1/visits/{visit.Id}/complete");
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        (await RefusalAsync(admin, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).Code.ShouldBe("approval.stale");
        var decided = await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        decided.Status.ShouldBe(ApprovalStatus.Failed);
        decided.Outcome.ShouldNotBeNull().ShouldContain("was completed after this was asked for");
        (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).Status.ShouldBe(VisitStatus.Completed);
    }

    private static async Task<ApprovalRequestDto> AskAsync(ApiClient desk, Guid visitId, string reason)
    {
        var path = $"/api/v1/visits/{visitId}/cancel";
        using var response = await desk.SendRawAsync(HttpMethod.Post, path, new CancelVisitRequest(reason));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path);
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private async Task<int> UnbilledChargesAsync(Guid visitId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Charge>().IgnoreQueryFilters().AsNoTracking()
            .CountAsync(c => c.VisitId == visitId && c.Status == ChargeStatus.Unbilled, Ct);
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.Action == action && a.EntityId == entityId.ToString(), Ct);
    }

    private static async Task<VisitDetail> CheckInAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        return await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Fever",
            false, null, null, TokenPriority.Normal, IssueToken: true, NotifyPatient: false));
    }

    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice)
    {
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Karthik"), MiddleName: null, LastName: "Cancel" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null,
            GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null,
            Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null,
            PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
