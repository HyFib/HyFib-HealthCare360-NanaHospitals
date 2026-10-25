using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The emergency department over the real API. The front office checks a road-accident patient in as an emergency; the
/// case is on the ER board at once. The nurse triages on the ESI (the vitals reach the chart); the doctor sees the
/// patient, enters the case in the MLC register, which marks the visit, records the police intimation and prints it;
/// medical records read the register; the patient is discharged and leaves the board, and the visit closes.
/// </summary>
public sealed class EmergencyDepartmentTests(HospitalServer server)
{
    private const string Er = "/api/v1/emergency";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_emergency_patient_is_triaged_seen_reported_to_the_police_and_discharged()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (patientId, visit) = await EmergencyVisitAsync(frontOffice);

        // On the board at once, untriaged.
        var row = (await nurse.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id);
        (row.PatientId, row.Level, row.IsMlc).ShouldBe((patientId, (int?)null, false));
        var caseId = row.CaseId;

        // The front office may watch the board but not triage.
        using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, $"{Er}/cases/{caseId}/triage", Triage(row.Version));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Many resources and a fast pulse: the algorithm says level 2; the nurse keeps 3 only with a reason.
        var preview = await nurse.PostAsync<TriagePreview>($"{Er}/cases/{caseId}/triage/preview", new TriagePreviewRequest(
            false, false, false, false, 2, 118, 18, 95, 128, 82, 37.1m, 15, 5));
        (preview.Level, preview.DangerZoneVitals).ShouldBe((2, true));
        using var noReason = await nurse.SendRawAsync(HttpMethod.Post, $"{Er}/cases/{caseId}/triage", Triage(row.Version) with { Level = 3 });
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var triaged = await nurse.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/triage", Triage(row.Version));
        triaged.Level.ShouldBe(2);
        triaged.Triages.ShouldHaveSingleItem().Reasons.ShouldContain("D: pulse 118/min (above 100), in the danger zone; consider level 2.");
        var vitals = (await doctor.GetAsync<PagedResult<VitalsDto>>($"/api/v1/emr/patients/{patientId}/vitals")).Items;
        vitals.ShouldContain(v => v.Pulse == 118 && v.SpO2 == 95, "The triage vitals are on the chart.");

        // The doctor sees the patient: door-to-doctor is measured, the visit starts.
        var seen = await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/seen", new CaseVersionRequest(triaged.Version));
        seen.DoctorSeenAt.ShouldNotBeNull();
        var onBoard = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.CaseId == caseId);
        (onBoard.Level, onBoard.DoorToTriageMinutes.HasValue, onBoard.DoorToDoctorMinutes.HasValue).ShouldBe((2, true, true));

        // A nurse cannot open the MLC; the doctor enters it, and the visit carries its number.
        using var nurseMlc = await nurse.SendRawAsync(HttpMethod.Put, $"{Er}/cases/{caseId}/mlc", Mlc(null));
        nurseMlc.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var withMlc = await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", Mlc(null));
        var mlc = withMlc.Mlc.ShouldNotBeNull();
        mlc.Number.ShouldNotBeNullOrWhiteSpace();
        (mlc.Cause, mlc.PoliceStation, mlc.DoctorRegistrationNumber).ShouldBe((MlcCause.RoadTrafficAccident, "Adyar", "TNMC 45678"));
        var marked = await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}");
        (marked.IsMlc, marked.MlcNumber).ShouldBe((true, mlc.Number));
        using var twice = await doctor.SendRawAsync(HttpMethod.Put, $"{Er}/cases/{caseId}/mlc", Mlc(null));
        twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The police are informed; the facts are then fixed, and the intimation prints.
        var informed = await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc/intimation", new MlcIntimationRequest(
            DateTimeOffset.UtcNow.AddMinutes(-1), MlcIntimationMode.Phone, "HC 1234 Ravi", "GD 45/2026", mlc.Version));
        informed.Mlc!.ReceivedBy.ShouldBe("HC 1234 Ravi");
        using var fixedFacts = await doctor.SendRawAsync(HttpMethod.Put, $"{Er}/cases/{caseId}/mlc", Mlc(informed.Mlc.Version));
        (await ProblemCodeAsync(fixedFacts)).ShouldBe("mlc.intimated");
        var letter = await doctor.GetAsync<PrintableDocumentDto>($"{Er}/cases/{caseId}/mlc/document");
        letter.Html.ShouldContain(mlc.Number);
        letter.Html.ShouldContain("Adyar");

        // Medical records read the register.
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var entry = (await mrd.GetAsync<List<MlcRegisterRow>>($"{Er}/mlc")).Single(r => r.Number == mlc.Number);
        (entry.CaseId, entry.IntimationOverdue).ShouldBe((caseId, false));

        // Discharged: off the board, and the visit closes.
        var discharged = await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/disposition",
            new DispositionRequest(EmergencyDisposition.Discharged, "Wound sutured; tetanus toxoid given; review in 5 days", null, informed.Version));
        discharged.Disposition.ShouldBe(EmergencyDisposition.Discharged);
        (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.ShouldNotContain(r => r.CaseId == caseId);
        (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).Status.ShouldBe(VisitStatus.Completed);
    }

    private async Task<(Guid PatientId, VisitDetail Visit)> EmergencyVisitAsync(ApiClient frontOffice)
    {
        var departments = await frontOffice.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments");
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Senthil"), MiddleName: null, LastName: "Casualty" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1985, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            created.Id, VisitType.Emergency, departments.Single(d => d.Code == "EMER").Id, null, null, PayerType.Self, null, ReferralSource.Self, null,
            "Fall from two-wheeler", false, null, null, TokenPriority.Emergency, IssueToken: false, NotifyPatient: false));
        return (created.Id, visit);
    }

    private static TriageRequest Triage(uint version) => new(
        "Fall from two-wheeler, forearm laceration, head knock", ArrivalMode.GovernmentAmbulance, false, false, false, false, 2,
        118, 18, 95, 128, 82, 37.1m, 15, 5, null, null, version);

    private static SaveMlcRequest Mlc(uint? version) => new(
        MlcBroughtBy.Ambulance, "108 ambulance, EMT Priya", MlcCause.RoadTrafficAccident, DateTimeOffset.UtcNow.AddHours(-1), "OMR, Thoraipakkam",
        "Lacerated wound 4 x 1 cm, left forearm, simple; abrasion over left knee", true, false, "Adyar", "TNMC 45678", version);

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
}
