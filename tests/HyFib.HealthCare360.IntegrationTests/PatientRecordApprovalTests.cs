using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The patient's record needs a second person where one person could put a record under someone else's name or open what
/// the law keeps closed (maker-checker, slice 7), over the real API and PostgreSQL: an existing patient's identity changed
/// after the desk's correction window waits for someone who approves identity changes, while the desk that registered the
/// patient corrects a typo at once within a day and before the patient is seen; a VIP restriction is added at once and
/// lifted only with a second person; the deceased status set or taken back from the registration screen waits, and a
/// certified death is not taken back there at all; and a record leaves a sensitive category only with another doctor's
/// agreement, while it enters one at once. The old values stay in force until then, the approver is never the one who
/// asked, and each change is made only over the record as it was asked.
/// </summary>
public sealed class PatientRecordApprovalTests(HospitalServer server)
{
    private const string Patients = "/api/v1/patients";
    private const string Approvals = "/api/v1/approvals";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ identity

    [Fact]
    public async Task An_existing_patients_identity_change_waits_for_someone_who_approves_identity_and_the_old_identity_stays_until_then()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var mrd = await server.SignInAsync(Users.MrdOfficer);
        var patient = await RegisterAsync(frontOffice);
        await RegisteredLongAgoAsync(patient.Id);
        var before = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        before.IdentityCorrectableUntil.ShouldBeNull("the correction window has passed");
        var edit = Edit(before) with { FirstName = before.FirstName + "x", Gender = Gender.Female, Title = "Ms.", DateOfBirth = before.DateOfBirth!.Value.AddYears(-1), Occupation = "Weaver" };

        // A change that waits needs a reason for the one who decides it.
        (await RefusalAsync(frontOffice, HttpMethod.Put, $"{Patients}/{patient.Id}", edit)).ShouldBe((HttpStatusCode.BadRequest, "validation"));

        var asked = await AcceptedAsync(frontOffice, HttpMethod.Put, $"{Patients}/{patient.Id}", edit with { ChangeReason = "Name and sex as on the Aadhaar card" });
        asked.Kind.ShouldBe(PatientRecordApprovals.IdentityKind);
        asked.Details.Select(d => d.Label).ShouldBe(["Patient", "Name", "Sex", "Date of birth", "What it reaches"]);
        asked.Details.Single(d => d.Label == "Sex").Value.ShouldBe("Male → Female");

        // The rest of the record is saved at once; the identity stays as it was, and the record says what waits.
        var waiting = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        (waiting.FirstName, waiting.Gender, waiting.DateOfBirth, waiting.Occupation).ShouldBe((before.FirstName, Gender.Male, before.DateOfBirth, "Weaver"));
        waiting.PendingChanges.ShouldNotBeNull().ShouldHaveSingleItem().ApprovalId.ShouldBe(asked.Id);
        (await frontOffice.GetAsync<PatientSummary>($"{Patients}/{patient.Id}/summary")).FullName.ShouldBe(before.FullName, "the banner keeps the old name");

        // The desk may not decide it; the medical records officer does.
        (await RefusalAsync(frontOffice, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        var request = await mrd.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        (await mrd.PostAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}/approve", new ApproveRequest("Checked against the card", request.Version))).Status
            .ShouldBe(ApprovalStatus.Approved);

        var after = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        (after.FirstName, after.Gender, after.Title, after.DateOfBirth).ShouldBe((edit.FirstName, Gender.Female, "Ms.", edit.DateOfBirth));
        after.PendingChanges.ShouldBeNull("nothing waits any more");
        var audited = await AuditAsync(patient.Id, "patient.identity_changed");
        audited.ShouldContain("asked for by");
        audited.ShouldContain("approved by");
    }

    [Fact]
    public async Task A_registration_typo_is_corrected_at_once_by_the_desk_that_registered_the_patient_within_a_day_before_the_patient_is_seen()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var billing = await server.SignInAsync(Users.Billing);
        var patient = await RegisterAsync(frontOffice);

        // The registering desk corrects the spelling at once.
        var registered = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        registered.IdentityCorrectableUntil.ShouldNotBeNull();
        await NoContentAsync(frontOffice, $"{Patients}/{patient.Id}", Edit(registered) with { LastName = registered.LastName + "a" });
        var corrected = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        corrected.LastName.ShouldBe(registered.LastName + "a");

        // Anyone else asks, even within the day.
        (await billing.GetAsync<PatientDetail>($"{Patients}/{patient.Id}")).IdentityCorrectableUntil.ShouldBeNull();
        var asked = await AcceptedAsync(billing, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(corrected) with { LastName = corrected.LastName + "b", ChangeReason = "Patient's request" });
        await WithdrawAsync(billing, asked);

        // Once the patient has been seen, the registering desk asks too.
        await VisitBegunAsync(patient.Id);
        corrected = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        corrected.IdentityCorrectableUntil.ShouldBeNull("the patient has been seen");
        asked = await AcceptedAsync(frontOffice, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(corrected) with { LastName = corrected.LastName + "c", ChangeReason = "Typo" });
        (await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}")).LastName.ShouldBe(corrected.LastName);
        await WithdrawAsync(frontOffice, asked);
    }

    [Fact]
    public async Task An_identity_change_is_made_only_over_the_identity_it_was_asked_against_and_never_by_the_one_who_asked()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var mrd = await server.SignInAsync(Users.MrdOfficer);
        var patient = await RegisterAsync(frontOffice);
        await RegisteredLongAgoAsync(patient.Id);
        var before = await admin.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");

        // The hospital administrator approves identity changes, but not one they asked for.
        var asked = await AcceptedAsync(admin, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(before) with { FirstName = before.FirstName + "z", ChangeReason = "Spelling" });
        (await RefusalAsync(admin, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));

        // Another change to the identity meanwhile is refused while this one waits.
        var again = await admin.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        (await RefusalAsync(admin, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(again) with { FirstName = before.FirstName + "q", ChangeReason = "Other" }))
            .ShouldBe((HttpStatusCode.Conflict, PatientRecordApprovals.WaitingCode));

        // The name changed by other means after it was asked: the approval changes nothing.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().Where(p => p.Id == patient.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.MiddleName, "Kumar"), Ct);
        }

        var request = await mrd.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        (await RefusalAsync(mrd, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, request.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        (await mrd.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Failed);
        (await admin.GetAsync<PatientDetail>($"{Patients}/{patient.Id}")).FirstName.ShouldBe(before.FirstName);
    }

    [Fact]
    public async Task A_new_aadhaar_or_abha_number_on_an_existing_patient_waits_even_when_it_ends_in_the_same_digits()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var patient = await RegisterAsync(frontOffice);
        await RegisteredLongAgoAsync(patient.Id);
        var before = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        var abha = "91" + Digits(12);

        var asked = await AcceptedAsync(frontOffice, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(before) with { AbhaNumber = abha, ChangeReason = "ABHA card shown" });
        asked.Details.Single(d => d.Label == "ABHA number").Value.ShouldStartWith("none → ");
        (await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}")).AbhaNumberMasked.ShouldBeNull("the number is linked only once approved");
        await server.ApproveRecordChangeAsync(asked.Id);
        var linked = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        linked.AbhaNumberMasked.ShouldNotBeNull().ShouldEndWith(abha[^4..]);
        (await AuditAsync(patient.Id, PatientRegistrationService.AbhaChangedAction)).ShouldContain("with approval");

        // Another number that ends in the same four digits is a change too.
        var other = "92" + Digits(8) + abha[^4..];
        var replaced = await AcceptedAsync(frontOffice, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(linked) with { AbhaNumber = other, ChangeReason = "Wrong card" });
        replaced.Details.ShouldContain(d => d.Label == "ABHA number" && d.Value.Contains("another number", StringComparison.Ordinal));
        await WithdrawAsync(frontOffice, replaced);
    }

    // ------------------------------------------------------------------ restriction

    [Fact]
    public async Task Restricting_a_record_is_made_at_once_and_lifting_it_waits_for_someone_else_who_opens_restricted_records()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var mrd = await server.SignInAsync(Users.MrdOfficer);
        var patient = await RegisterAsync(frontOffice);
        await RegisteredLongAgoAsync(patient.Id);

        var open = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        await NoContentAsync(frontOffice, $"{Patients}/{patient.Id}", Edit(open) with { IsVip = true });
        var restricted = await admin.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        restricted.IsVip.ShouldBeTrue();

        var asked = await AcceptedAsync(admin, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(restricted) with { IsVip = false, ChangeReason = "Discharged; the family no longer asks for it" });
        asked.Kind.ShouldBe(PatientRecordApprovals.RestrictionKind);
        (await admin.GetAsync<PatientDetail>($"{Patients}/{patient.Id}")).IsVip.ShouldBeTrue("the record stays restricted until it is approved");
        using (var closed = await frontOffice.SendRawAsync(HttpMethod.Get, $"{Patients}/{patient.Id}", null))
        {
            closed.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the desk still may not open it");
        }

        (await RefusalAsync(admin, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await RefusalAsync(mrd, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"), "only those who open restricted records decide");
        await server.ApproveGrantAsync(asked.Id);
        (await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}")).IsVip.ShouldBeFalse();
        (await AuditAsync(patient.Id, "patient.restriction_lifted")).ShouldContain("approved by");
    }

    // ------------------------------------------------------------------ deceased

    [Fact]
    public async Task The_deceased_status_set_or_taken_back_at_the_desk_waits_and_a_certified_death_is_not_taken_back_there()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var patient = await RegisterAsync(frontOffice);
        var alive = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");

        // Marked deceased from the registration screen: it waits, even within the correction window.
        var died = DateTimeOffset.UtcNow.AddHours(-3);
        var marked = await AcceptedAsync(frontOffice, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(alive) with { IsDeceased = true, DeceasedAt = died, ChangeReason = "Relative reported the death at home" });
        marked.Kind.ShouldBe(PatientRecordApprovals.DeceasedKind);
        (await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}")).IsDeceased.ShouldBeFalse();
        await server.ApproveRecordChangeAsync(marked.Id);
        var deceased = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        deceased.IsDeceased.ShouldBeTrue();

        // Taken back: it waits too.
        var undone = await AcceptedAsync(frontOffice, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(deceased) with { IsDeceased = false, DeceasedAt = null, ChangeReason = "Wrong patient" });
        (await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}")).IsDeceased.ShouldBeTrue("the mark stays until it is approved");
        await server.ApproveRecordChangeAsync(undone.Id);
        var back = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        back.IsDeceased.ShouldBeFalse();

        // A death a doctor certified is the certificate's to take back.
        await doctor.PostAsync<JsonElement>("/api/v1/mrd/deaths", BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-1)));
        var certified = await frontOffice.GetAsync<PatientDetail>($"{Patients}/{patient.Id}");
        certified.IsDeceased.ShouldBeTrue();
        (await RefusalAsync(frontOffice, HttpMethod.Put, $"{Patients}/{patient.Id}", Edit(certified) with { IsDeceased = false, DeceasedAt = null, ChangeReason = "Wrong patient" }))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, PatientRecordApprovals.DeathCertifiedCode));
    }

    // ------------------------------------------------------------------ sensitive categories

    [Fact]
    public async Task A_record_enters_a_sensitive_category_at_once_and_leaves_it_only_when_another_doctor_agrees()
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var secondDoctor = await server.SignInAsync(Users.SecondDoctor);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var patient = await RegisterAsync(frontOffice);
        var path = $"{Patients}/{patient.Id}/sensitive-categories";

        var none = await doctor.GetAsync<SensitiveCategoriesDto>(path);
        var added = await doctor.PutAsync<SensitiveCategoriesDto>(path, new SetSensitiveCategoriesRequest([SensitiveCategory.Hiv, SensitiveCategory.MentalHealth], "Diagnosed today", none.Version));
        added.Categories.ShouldBe([SensitiveCategory.Hiv, SensitiveCategory.MentalHealth]);

        var asked = await AcceptedAsync(doctor, HttpMethod.Put, path, new SetSensitiveCategoriesRequest([SensitiveCategory.MentalHealth], "HIV test was a false positive", added.Version));
        asked.Kind.ShouldBe(PatientRecordApprovals.SensitiveKind);
        var waiting = await doctor.GetAsync<SensitiveCategoriesDto>(path);
        waiting.Categories.ShouldBe([SensitiveCategory.Hiv, SensitiveCategory.MentalHealth], "the record stays in the category until it is approved");
        waiting.WaitingRemoval.ShouldBe([SensitiveCategory.Hiv]);
        waiting.Waiting.ShouldNotBeNull().ApprovalId.ShouldBe(asked.Id);

        // Saving the categories again as they stand asks for nothing more.
        (await doctor.PutAsync<SensitiveCategoriesDto>(path, new SetSensitiveCategoriesRequest([SensitiveCategory.Hiv, SensitiveCategory.MentalHealth], "Saved again as they stand", waiting.Version)))
            .WaitingRemoval.ShouldBe([SensitiveCategory.Hiv]);

        (await RefusalAsync(doctor, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        var request = await secondDoctor.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        (await secondDoctor.PostAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}/approve", new ApproveRequest("Agreed: repeat test negative", request.Version))).Status
            .ShouldBe(ApprovalStatus.Approved);
        var after = await doctor.GetAsync<SensitiveCategoriesDto>(path);
        after.Categories.ShouldBe([SensitiveCategory.MentalHealth]);
        after.WaitingRemoval.ShouldBeNull();
        (await AuditAsync(patient.Id, "patient.sensitive_changed")).ShouldContain("approved by");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<PatientCreated> RegisterAsync(ApiClient frontOffice) =>
        await frontOffice.PostAsync<PatientCreated>(Patients, new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Senthil"), MiddleName: null, LastName: "Record" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));

    /// <summary>The record was entered two days ago: the desk's correction window has passed.</summary>
    private async Task RegisteredLongAgoAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().Where(p => p.Id == patientId)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.CreatedAt, x => x.CreatedAt.AddDays(-2)), Ct);
    }

    /// <summary>The patient has been seen: an OP visit with the doctor is under way.</summary>
    private async Task VisitBegunAsync(Guid patientId)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var departments = await frontOffice.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments");
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, departments.Single(d => d.Code == "GM").Id, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null,
            "Fever", false, null, null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Visit>().IgnoreQueryFilters().Where(v => v.Id == visit.Id)
            .ExecuteUpdateAsync(v => v.SetProperty(x => x.Status, VisitStatus.InProgress), Ct);
    }

    private async Task<string> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Action == action)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.Summary ?? string.Empty)
            .FirstAsync(Ct);
    }

    private static async Task<ApprovalRequestDto> AcceptedAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, method, path);
    }

    private static async Task NoContentAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Put, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task WithdrawAsync(ApiClient maker, ApprovalRequestDto asked)
    {
        var request = await maker.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        await maker.PostAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}/withdraw", new WithdrawApprovalRequest(request.Version));
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.IsSuccessStatusCode.ShouldBeFalse(await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    internal static UpdatePatientRequest Edit(PatientDetail p) => new(
        p.Title, p.FirstName, p.MiddleName, p.LastName, p.Gender, p.DateOfBirth, null, null, p.Phone, p.AlternatePhone,
        p.Email, p.Address, p.BloodGroup, p.MaritalStatus, p.GuardianName, p.GuardianRelation, p.EmergencyContactName,
        p.EmergencyContactPhone, p.EmergencyContactRelation, p.Occupation, p.Religion, p.Nationality, p.PreferredLanguage,
        p.Category, null, p.AbhaAddress, null, p.PhotoFileId, p.IsVip, p.IsDeceased, p.DeceasedAt, p.Notes,
        p.IsActive, p.Version);

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
