using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The emergency department's bays and its medico-legal paperwork over the real API: the administrator sets up the bays;
/// the nurse puts a patient in one, moves them and frees it; a taken bay is refused; the disposition frees it. The doctor
/// writes the wound certificate of an MLC (the nature of each hurt under BNS 2023), signs it, and it no longer changes;
/// it prints, and the medico-legal report for a court is made from the record, its issue audited.
/// </summary>
public sealed class EmergencyBaysAndMedicoLegalTests(HospitalServer server)
{
    private const string Er = "/api/v1/emergency";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patient_is_put_in_a_bay_moved_and_the_bay_freed_when_they_leave()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var nurse = await server.SignInAsync(Users.Nurse);
        var doctor = await server.SignInAsync(Users.Doctor);
        var suffix = Letters(4).ToUpperInvariant();

        // The bays are a master the administrator keeps; a nurse does not add one.
        (await StatusAsync(nurse, HttpMethod.Post, $"{Er}/bays", new SaveErBayRequest("R" + suffix, ErArea.Resuscitation, 1, true, null))).ShouldBe(HttpStatusCode.Forbidden);
        var resus = await admin.PostAsync<ErBayDto>($"{Er}/bays", new SaveErBayRequest("R" + suffix, ErArea.Resuscitation, 1, true, null));
        var acute = await admin.PostAsync<ErBayDto>($"{Er}/bays", new SaveErBayRequest("a" + suffix, ErArea.Acute, 2, true, null));
        acute.Code.ShouldBe("A" + suffix, "codes are kept as painted, in capitals");
        (await RefusalAsync(admin, HttpMethod.Post, $"{Er}/bays", new SaveErBayRequest("R" + suffix, ErArea.Minor, 3, true, null))).Code.ShouldBe("er.bay_code_taken");

        var (_, first) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var (_, second) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var board = await nurse.GetAsync<ErBoard>($"{Er}/board");
        var one = board.Rows.Single(r => r.VisitId == first.Id);
        var two = board.Rows.Single(r => r.VisitId == second.Id);

        // The front office watches the board but does not move patients.
        (await StatusAsync(frontOffice, HttpMethod.Put, $"{Er}/cases/{one.CaseId}/bay", new AssignBayRequest(resus.Id, one.Version))).ShouldBe(HttpStatusCode.Forbidden);
        var inBay = await nurse.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{one.CaseId}/bay", new AssignBayRequest(resus.Id, one.Version));
        (inBay.BayId, inBay.BayCode).ShouldBe((resus.Id, resus.Code));
        (await nurse.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.CaseId == one.CaseId).BayCode.ShouldBe(resus.Code);

        // The bay holds one patient.
        var taken = await RefusalAsync(nurse, HttpMethod.Put, $"{Er}/cases/{two.CaseId}/bay", new AssignBayRequest(resus.Id, two.Version));
        (taken.Status, taken.Code).ShouldBe((HttpStatusCode.Conflict, "er.bay_taken"));
        var bays = await nurse.GetAsync<ErBays>($"{Er}/bays");
        bays.Bays.Single(b => b.Id == resus.Id).CaseId.ShouldBe(one.CaseId);

        // Moved to the acute bay, the resuscitation bay is free for the second patient.
        var moved = await nurse.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{one.CaseId}/bay", new AssignBayRequest(acute.Id, inBay.Version));
        moved.BayCode.ShouldBe(acute.Code);
        var secondIn = await nurse.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{two.CaseId}/bay", new AssignBayRequest(resus.Id, two.Version));
        secondIn.BayCode.ShouldBe(resus.Code);

        // Freed while the patient goes for a scan; and the disposition frees the other.
        var released = await nurse.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{two.CaseId}/bay/release", new CaseVersionRequest(secondIn.Version));
        released.BayId.ShouldBeNull();
        var triaged = await nurse.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{one.CaseId}/triage", EmergencyDepartmentTests.Triage(moved.Version));
        await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{one.CaseId}/disposition",
            new DispositionRequest(EmergencyDisposition.Discharged, "Treated and sent home", null, triaged.Version));
        var after = await nurse.GetAsync<ErBays>($"{Er}/bays");
        after.Bays.Where(b => b.Id == resus.Id || b.Id == acute.Id).ShouldAllBe(b => b.CaseId == null);

        // A bay out of use takes no patient.
        await admin.PutAsync<ErBayDto>($"{Er}/bays/{acute.Id}", new SaveErBayRequest(acute.Code, ErArea.Acute, 2, false, after.Bays.Single(b => b.Id == acute.Id).Version));
        var current = await nurse.GetAsync<EmergencyCaseDetail>($"{Er}/cases/{two.CaseId}");
        (await RefusalAsync(nurse, HttpMethod.Put, $"{Er}/cases/{two.CaseId}/bay", new AssignBayRequest(acute.Id, current.Version))).Code.ShouldBe("er.bay_inactive");
    }

    [Fact]
    public async Task The_wound_certificate_is_signed_by_the_examining_doctor_fixed_once_signed_and_the_court_report_made_from_the_record()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var nurse = await server.SignInAsync(Users.Nurse);
        var doctor = await server.SignInAsync(Users.Doctor);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var caseId = (await nurse.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id).CaseId;
        var certificate = $"{Er}/cases/{caseId}/mlc/wound-certificate";

        // No certificate without the MLC entry it belongs to.
        (await RefusalAsync(doctor, HttpMethod.Put, certificate, Certificate(null))).Code.ShouldBe("mlc.missing");
        await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", new SaveMlcRequest(
            MlcBroughtBy.Police, "HC 2231 Murugan", MlcCause.Assault, DateTimeOffset.UtcNow.AddHours(-2), "Guindy bus stand",
            "Lacerated wound on the scalp; swelling of the left forearm", true, false, "Guindy", "TNMC 45678", null));

        // A nurse does not write it; a fracture is never simple hurt.
        (await StatusAsync(nurse, HttpMethod.Put, certificate, Certificate(null))).ShouldBe(HttpStatusCode.Forbidden);
        var simpleFracture = Certificate(null) with { Injuries = [Injuries()[0], Injuries()[1] with { Nature = InjuryNature.Simple, Clause = null }] };
        (await StatusAsync(doctor, HttpMethod.Put, certificate, simpleFracture)).ShouldBe(HttpStatusCode.BadRequest);

        var draft = await doctor.PutAsync<EmergencyCaseDetail>(certificate, Certificate(null));
        var wound = draft.WoundCertificate.ShouldNotBeNull();
        (wound.IsSigned, wound.Injuries.Count).ShouldBe((false, 2));
        var draftCopy = await doctor.GetAsync<PrintableDocumentDto>($"{certificate}/document");
        draftCopy.Html.ShouldContain("Draft: not signed");

        // The draft is not evidence: the court report leaves it out.
        var early = await doctor.PostAsync<PrintableDocumentDto>($"{Er}/cases/{caseId}/mlc/court-report", new CourtReportRequest("the Court of the Judicial Magistrate, Saidapet", "CC 412/2026", null));
        early.Html.ShouldContain("No signed wound certificate is on the record");

        var signed = await doctor.PostAsync<EmergencyCaseDetail>($"{certificate}/sign", new CaseVersionRequest(wound.Version));
        signed.WoundCertificate!.IsSigned.ShouldBeTrue();
        signed.WoundCertificate.DoctorName.ShouldNotBeNullOrWhiteSpace();
        (await RefusalAsync(doctor, HttpMethod.Put, certificate, Certificate(signed.WoundCertificate.Version))).Code.ShouldBe("wound_certificate.signed");

        var copy = await doctor.GetAsync<PrintableDocumentDto>($"{certificate}/document");
        copy.Html.ShouldNotContain("Draft: not signed");
        copy.Html.ShouldContain("Grievous (BNS s.116(g): fracture or dislocation of a bone or tooth)");
        copy.Html.ShouldContain("Simple");
        copy.Html.ShouldContain(signed.WoundCertificate.DoctorName!);

        // The report for the court: the record's facts, the signed injuries, its issue audited with the court's name.
        var mrd = await server.SignInAsync(Users.MrdOfficer);
        var report = await mrd.PostAsync<PrintableDocumentDto>($"{Er}/cases/{caseId}/mlc/court-report",
            new CourtReportRequest("the Court of the Judicial Magistrate, Saidapet", "CC 412/2026", "the requisition of the SHO, Guindy, dated 6 Nov 2026"));
        report.Html.ShouldContain("Medico-legal report");
        report.Html.ShouldContain("CC 412/2026");
        report.Html.ShouldContain("Guindy");
        report.Html.ShouldContain("fracture or dislocation");
        (await AuditAsync(patientId, "Issued the medico-legal report")).ShouldContain("Judicial Magistrate");
    }

    private static SaveWoundCertificateRequest Certificate(uint? version) => new(
        DateTimeOffset.UtcNow.AddMinutes(-30), "A black mole 0.5 cm below the right ear; a healed scar 3 cm on the left shin", "Assaulted with a wooden stick at the bus stand",
        Injuries(), "X-ray of the left forearm done", version);

    private static IReadOnlyList<WoundInjury> Injuries() =>
    [
        new(1, InjuryKind.Laceration, "Scalp, right parietal region, 8 cm above the right ear", "4 x 1 x 0.5 cm", "Margins irregular and bruised, bleeding",
            InjuryWeapon.Blunt, "Fresh, within 6 hours", InjuryNature.Simple, null, null),
        new(2, InjuryKind.Fracture, "Left forearm, middle third", null, "Swelling and tenderness; X-ray: fracture of the shaft of the ulna",
            InjuryWeapon.Blunt, "Fresh, within 6 hours", InjuryNature.Grievous, GrievousClause.FractureOrDislocation, null),
    ];

    private async Task<string> AuditAsync(Guid patientId, string startsWith)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var summaries = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId).Select(e => e.Summary ?? string.Empty).ToListAsync(Ct);
        return summaries.First(s => s.StartsWith(startsWith, StringComparison.Ordinal) || s.Contains(startsWith, StringComparison.Ordinal));
    }

    private static async Task<HttpStatusCode> StatusAsync(ApiClient client, HttpMethod method, string path, object body)
    {
        using var response = await client.SendRawAsync(method, path, body);
        return response.StatusCode;
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient client, HttpMethod method, string path, object body)
    {
        using var response = await client.SendRawAsync(method, path, body);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
}
