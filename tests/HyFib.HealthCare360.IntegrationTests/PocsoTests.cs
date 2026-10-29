using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Migrations = HyFib.HealthCare360.Server.Persistence.Migrations;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A sexual assault in the emergency department, over the real API: the victim's medical examination as the Bharatiya
/// Nagarik Suraksha Sanhita 2023, s.184 asks it recorded, and a child's case as the POCSO Act 2012 asks it handled. A
/// 12-year-old girl's MLC entry makes her a child from her date of birth. Her case is reported to the Special Juvenile
/// Police Unit (s.19). A man may not examine her; a woman doctor does, with her mother present (s.27). The examination's
/// report goes to the investigating officer, and the register and the printed intimation carry it all. For a patient
/// whose date of birth is not known, the doctor says whether they are under 18; without the incident's time, a patient
/// is a child who was under 18 on the day the case was identified; and the upgrade marks the cases already entered as a
/// new entry is marked.
/// </summary>
public sealed class PocsoTests(HospitalServer server)
{
    private const string Er = "/api/v1/emergency";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_girl_childs_case_goes_to_the_juvenile_police_unit_and_a_woman_doctor_examines_her_with_her_mother_present()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (manId, _) = await DoctorAsync(admin, Gender.Male, "Ravi");
        var (womanId, womanName) = await DoctorAsync(admin, Gender.Female, "Lakshmi");
        var twelve = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-12);
        var (_, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice, Gender.Female, twelve);
        var caseId = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id).CaseId;

        // Her date of birth makes her a child: a sexual assault on her is a POCSO case.
        var entered = (await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", SexualAssault(null))).Mlc.ShouldNotBeNull();
        (entered.IsChild, entered.IsPocso).ShouldBe((true, true));

        // The report names the Special Juvenile Police Unit or the local police (s.19(1)).
        using (var unnamed = await doctor.SendRawAsync(HttpMethod.Post, $"{Er}/cases/{caseId}/mlc/intimation", Intimation(entered.Version, null)))
        {
            unnamed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await FieldsAsync(unnamed)).ShouldContain("reportedTo");
        }

        var informed = (await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc/intimation",
            Intimation(entered.Version, PocsoReportedTo.SpecialJuvenilePoliceUnit))).Mlc!;
        informed.ReportedTo.ShouldBe(PocsoReportedTo.SpecialJuvenilePoliceUnit);

        // A girl child is examined by a woman doctor (s.27(2)), with someone present (s.27(3)-(4)).
        var started = DateTimeOffset.UtcNow.AddMinutes(-50);
        var ended = DateTimeOffset.UtcNow.AddMinutes(-10);
        using (var byAMan = await doctor.SendRawAsync(HttpMethod.Post, $"{Er}/cases/{caseId}/mlc/examination",
            new MlcExaminationRequest(manId, started, ended, "Her mother, Selvi", ExaminationAttendant.Parent, "Mother, Selvi", informed.Version)))
        {
            (await ProblemCodeAsync(byAMan)).ShouldBe("pocso.woman_doctor");
        }

        using (var nobodyPresent = await doctor.SendRawAsync(HttpMethod.Post, $"{Er}/cases/{caseId}/mlc/examination",
            new MlcExaminationRequest(womanId, started, ended, "Her mother, Selvi", null, null, informed.Version)))
        {
            nobodyPresent.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await FieldsAsync(nobodyPresent)).ShouldContain("attendantName");
        }

        var examined = (await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc/examination",
            new MlcExaminationRequest(womanId, started, ended, "Her mother, Selvi", ExaminationAttendant.Parent, "Mother, Selvi", informed.Version))).Mlc!;
        var examination = examined.Examination.ShouldNotBeNull();
        (examination.ExaminerId, examination.ExaminerName, examination.AttendantKind).ShouldBe((womanId, womanName, (ExaminationAttendant?)ExaminationAttendant.Parent));
        examination.ExaminerRegistration.ShouldNotBeNull().ShouldStartWith("Tamil Nadu Medical Council");
        examination.MissingSafeguards.ShouldBeEmpty();
        examined.ReportDueAt.ShouldNotBeNull().ShouldBe(examination.EndedAt.AddDays(7), TimeSpan.FromSeconds(1));
        examined.ReportOverdue.ShouldBeFalse();

        // Its report goes to the investigating officer (BNSS s.184(6)); the examination then stays as it was.
        var reported = (await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc/report",
            new MlcReportRequest(DateTimeOffset.UtcNow.AddMinutes(-1), "SI Meena, AWPS Velachery", examined.Version))).Mlc!;
        (reported.InvestigatingOfficer, reported.ReportForwardedAt.HasValue).ShouldBe(("SI Meena, AWPS Velachery", true));
        using (var again = await doctor.SendRawAsync(HttpMethod.Post, $"{Er}/cases/{caseId}/mlc/examination",
            new MlcExaminationRequest(womanId, started, ended, "Her mother, Selvi", ExaminationAttendant.Parent, "Mother, Selvi", reported.Version)))
        {
            (await ProblemCodeAsync(again)).ShouldBe("mlc.report_forwarded");
        }

        // The register and the printed intimation carry it.
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var row = (await mrd.GetAsync<List<MlcRegisterRow>>($"{Er}/mlc")).Single(r => r.Number == entered.Number);
        (row.IsPocso, row.ReportForwardedAt.HasValue, row.ReportOverdue).ShouldBe((true, true, false));
        var letter = (await doctor.GetAsync<PrintableDocumentDto>($"{Er}/cases/{caseId}/mlc/document")).Html;
        letter.ShouldContain("Special Juvenile Police Unit");
        letter.ShouldContain("Protection of Children from Sexual Offences Act 2012");
        letter.ShouldContain("Medical examination (BNSS 2023, s.184)");
        letter.ShouldContain(womanName);
        letter.ShouldContain("Mother, Selvi");
        letter.ShouldContain("SI Meena, AWPS Velachery");
    }

    [Fact]
    public async Task Without_a_date_of_birth_the_doctor_says_whether_the_patient_is_a_child_and_an_adults_examination_needs_no_attendant()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (manId, _) = await DoctorAsync(admin, Gender.Male, "Ravi");
        var (womanId, _) = await DoctorAsync(admin, Gender.Female, "Lakshmi");
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice, Gender.Female);
        var caseId = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id).CaseId;

        // Registration asks for a date of birth or an age; a record without one is an older or imported one.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE patients.patients SET date_of_birth = NULL, is_date_of_birth_estimated = false WHERE id = {patientId}", Ct);
        }

        using (var unsaid = await doctor.SendRawAsync(HttpMethod.Put, $"{Er}/cases/{caseId}/mlc", SexualAssault(null)))
        {
            unsaid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await FieldsAsync(unsaid)).ShouldContain("childVictim");
        }

        // An adult, as the doctor says: her case goes to the police station, and a man may examine her with her consent.
        var adult = (await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", SexualAssault(null) with { ChildVictim = false })).Mlc!;
        (adult.IsChild, adult.IsPocso).ShouldBe((false, false));
        var examined = (await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc/examination",
            new MlcExaminationRequest(manId, DateTimeOffset.UtcNow.AddMinutes(-40), DateTimeOffset.UtcNow.AddMinutes(-5), "The patient", null, null, adult.Version))).Mlc!;
        (examined.Examination.ShouldNotBeNull().AttendantName, examined.ReportOverdue).ShouldBe(((string?)null, false));

        // Said to be a child, the same case is a POCSO case. The examination stays as it was done, and shows what it lacks of
        // s.27: on the case, in the register and on the printed intimation, whose report still goes to the officer.
        var child = (await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", SexualAssault(examined.Version) with { ChildVictim = true })).Mlc!;
        (child.IsChild, child.IsPocso).ShouldBe((true, true));
        child.Examination.ShouldNotBeNull().MissingSafeguards.ShouldBe([PocsoSafeguard.WomanDoctor, PocsoSafeguard.PersonPresent]);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        (await mrd.GetAsync<List<MlcRegisterRow>>($"{Er}/mlc")).Single(r => r.Number == child.Number).PocsoSafeguardsMissing.ShouldBeTrue();
        (await doctor.GetAsync<PrintableDocumentDto>($"{Er}/cases/{caseId}/mlc/document")).Html.ShouldContain("Examined before the case was known to be a child");

        // Recorded wrongly, the examination is corrected as a child's is recorded, and then lacks nothing.
        var corrected = (await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc/examination", new MlcExaminationRequest(
            womanId, DateTimeOffset.UtcNow.AddMinutes(-40), DateTimeOffset.UtcNow.AddMinutes(-5), "Her mother, Selvi", ExaminationAttendant.Parent,
            "Mother, Selvi", child.Version))).Mlc!;
        corrected.Examination.ShouldNotBeNull().MissingSafeguards.ShouldBeEmpty();
        (await mrd.GetAsync<List<MlcRegisterRow>>($"{Er}/mlc")).Single(r => r.Number == child.Number).PocsoSafeguardsMissing.ShouldBeFalse();
    }

    [Fact]
    public async Task Without_an_incident_time_a_patient_under_18_on_the_day_the_case_was_identified_is_a_child()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var (_, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice, Gender.Female, today.AddYears(-18));
        var caseId = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id).CaseId;

        // She is 18 today: entered today, with no time for the incident, hers is an adult's case.
        var entered = (await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", SexualAssault(null) with { IncidentAt = null })).Mlc!;
        entered.IsChild.ShouldBeFalse();

        // Had the case been identified ten days ago, when she was 17, a correction today keeps it a child's.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE clinical.mlc_records SET identified_at = identified_at - interval '10 days' WHERE id = {entered.Id}", Ct);
        }

        var version = (await doctor.GetAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}")).Mlc!.Version;
        var corrected = (await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", SexualAssault(version) with { IncidentAt = null })).Mlc!;
        (corrected.IsChild, corrected.IsPocso).ShouldBe((true, true));
    }

    [Fact]
    public async Task The_upgrade_marks_a_childs_sexual_assault_already_in_the_register_as_a_new_entry_is_marked()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var (childCase, child) = await SexualAssaultCaseAsync(frontOffice, doctor, today.AddYears(-12));
        var (adultCase, adult) = await SexualAssaultCaseAsync(frontOffice, doctor, today.AddYears(-30));
        (child.IsChild, adult.IsChild).ShouldBe((true, false));

        // As the entries were before the upgrade, when nothing marked a child; then the upgrade's own statement.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE clinical.mlc_records SET is_child = false WHERE id IN ({child.Id}, {adult.Id})", Ct);
            await db.Database.ExecuteSqlRawAsync(Migrations.SexualAssaultExamination.MarkChildCases, Ct);
        }

        var upgraded = (await doctor.GetAsync<EmergencyCaseDetail>($"{Er}/cases/{childCase}")).Mlc!;
        (upgraded.IsChild, upgraded.IsPocso).ShouldBe((true, true));
        (await doctor.GetAsync<EmergencyCaseDetail>($"{Er}/cases/{adultCase}")).Mlc!.IsChild.ShouldBeFalse();
    }

    /// <summary>A sexual assault entered in the MLC register for a new emergency patient born on <paramref name="dateOfBirth"/>.</summary>
    private static async Task<(Guid CaseId, MlcDto Mlc)> SexualAssaultCaseAsync(ApiClient frontOffice, ApiClient doctor, DateOnly dateOfBirth)
    {
        var (_, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice, Gender.Female, dateOfBirth);
        var caseId = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id).CaseId;
        return (caseId, (await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", SexualAssault(null))).Mlc!);
    }

    /// <summary>A doctor added to the practitioner master by the hospital administrator, with a council registration.</summary>
    private static async Task<(Guid Id, string Name)> DoctorAsync(ApiClient admin, Gender gender, string firstName)
    {
        var lastName = "Pocso" + Letters(6);
        var created = await admin.PostAsync<JsonElement>("/api/v1/masters/practitioners", new SavePractitionerRequest(
            Code: $"PX{Letters(8)}", Title: "Dr.", FirstName: firstName, LastName: lastName, Gender: gender, Qualification: "MD (Forensic Medicine)",
            RegistrationCouncil: "Tamil Nadu Medical Council", RegistrationNumber: Digits(6), HprId: null, SpecialtyId: null, DepartmentId: null,
            Kind: PractitionerKind.Consultant, Phone: null, Email: null, ConsultationFeeNew: 0m, ConsultationFeeFollowUp: 0m, FollowUpValidityDays: 0,
            Languages: null, Bio: null, PhotoFileId: null, SignatureFileId: null, AvailableForOnlineBooking: false, AvailableForTelehealth: false,
            FacilityIds: null, IsActive: true, Version: null));
        return (created.GetProperty("id").GetGuid(), $"Dr. {firstName} {lastName}");
    }

    private static SaveMlcRequest SexualAssault(uint? version) => new(
        MlcBroughtBy.Relative, "Mother, Selvi", MlcCause.SexualAssault, DateTimeOffset.UtcNow.AddHours(-3), "Near the school, Velachery",
        "Abrasions over both forearms; examination findings in the examination report", true, false, "Velachery", "TNMC 45678", version);

    private static MlcIntimationRequest Intimation(uint version, PocsoReportedTo? reportedTo) => new(
        DateTimeOffset.UtcNow.AddMinutes(-1), MlcIntimationMode.Phone, "SI Meena", "GD 12/2026", version, reportedTo);

    private static async Task<IReadOnlyCollection<string>> FieldsAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("errors", out var errors) ? [.. errors.EnumerateObject().Select(e => e.Name)] : [];
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => Random.Shared.Next(10)));
}
