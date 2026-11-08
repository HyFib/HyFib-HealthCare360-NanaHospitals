using System.Net;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Breaches;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Personal data breaches (MAKER-CHECKER.md, slice 14; DPDP Act 2023 and Rules): a breach does not close until its detailed
/// report to the Board is recorded, with its reference and day (<c>privacy.breach_board_report_required</c>); closing it,
/// and taking patients off its notification list, wait for the Data Protection Officer (<c>privacy.breaches.approve</c>),
/// never the one who asked, audited with the patients taken off and why.
/// </summary>
public sealed class BreachApprovalTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_breach_closes_on_its_Board_report_and_when_the_Data_Protection_Officer_agrees()
    {
        var quality = await server.SignInAsync(Users.QualityManager);
        var breach = await RecordAsync(quality);
        var path = $"/api/v1/privacy/breaches/{breach.Id}";

        // Never reported to the Board: refused, whatever note is given.
        await RefusedAsync(quality, $"{path}/close", new CloseBreachRequest("No personal data was involved after all."), "privacy.breach_board_report_required");

        await quality.PutAsync(path, Complete(await quality.GetAsync<BreachDetail>(path)));
        await quality.PostAsync($"{path}/board-informed", new BoardInformedRequest(DateTimeOffset.UtcNow.AddMinutes(-60), null));
        await quality.PostAsync($"{path}/report-submitted", new ReportSubmittedRequest(DateTimeOffset.UtcNow.AddMinutes(-5), "DPB/2026/0911"));
        var asked = await AskAsync(quality, $"{path}/close", new CloseBreachRequest("Board acknowledged the report."));
        asked.Kind.ShouldBe("privacy.breach_close");
        asked.Details.ShouldContain(d => d.Label == "Board report" && d.Value.StartsWith("DPB/2026/0911", StringComparison.Ordinal));
        var waiting = await quality.GetAsync<BreachDetail>(path);
        (waiting.Status, waiting.ClosureAskedAt is null).ShouldBe((BreachStatus.ReportSubmitted, false));

        var admin = await server.SignInAsync(Users.HospitalAdmin);
        await admin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Report seen.", asked.Version));
        var closed = await quality.GetAsync<BreachDetail>(path);
        (closed.Status, closed.ClosingNote).ShouldBe((BreachStatus.Closed, "Board acknowledged the report."));
        (await AuditAsync(breach.Id, "privacy.breach.closed")).ShouldHaveSingleItem().ShouldContain("approved by");
    }

    [Fact]
    public async Task A_Board_report_recorded_without_its_reference_does_not_close_the_breach()
    {
        var quality = await server.SignInAsync(Users.QualityManager);
        var breach = await RecordAsync(quality);
        var path = $"/api/v1/privacy/breaches/{breach.Id}";
        await quality.PutAsync(path, Complete(await quality.GetAsync<BreachDetail>(path)));
        await quality.PostAsync($"{path}/board-informed", new BoardInformedRequest(DateTimeOffset.UtcNow.AddMinutes(-60), null));
        await quality.PostAsync($"{path}/report-submitted", new ReportSubmittedRequest(DateTimeOffset.UtcNow.AddMinutes(-5), null));

        await RefusedAsync(quality, $"{path}/close", new CloseBreachRequest(null), "privacy.breach_board_report_required");
        (await quality.GetAsync<BreachDetail>(path)).Status.ShouldBe(BreachStatus.ReportSubmitted);
    }

    [Fact]
    public async Task Patients_come_off_the_notification_list_only_when_the_Data_Protection_Officer_agrees_and_it_is_audited_with_why()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var breach = await RecordAsync(admin);
        var path = $"/api/v1/privacy/breaches/{breach.Id}";
        var kept = await RegisterPatientAsync();
        var removed = await RegisterPatientAsync();
        await admin.PostAsync<AddAffectedResult>($"{path}/affected", new AddAffectedRequest([kept.Id, removed.Id], null));

        // The one-person removal is gone: the old route answers nothing.
        using (var direct = await admin.SendRawAsync(HttpMethod.Delete, $"{path}/affected/{removed.Id}"))
        {
            direct.IsSuccessStatusCode.ShouldBeFalse();
        }

        using (var noReason = await admin.SendRawAsync(HttpMethod.Post, $"{path}/affected/remove", new RemoveAffectedRequest([removed.Id], " ")))
        {
            noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var asked = await AskAsync(admin, $"{path}/affected/remove", new RemoveAffectedRequest([removed.Id], "Listed by mistake: her record was not in the e-mail."));
        asked.Kind.ShouldBe("privacy.breach_affected_remove");
        asked.Details.ShouldContain(d => d.Label == "Taken off the list" && d.Value.Contains(removed.Uhid, StringComparison.Ordinal));
        (await admin.GetAsync<PagedResult<AffectedPatientItem>>($"{path}/affected")).Items.Count.ShouldBe(2, "Nobody comes off until approved.");
        (await admin.GetAsync<BreachDetail>(path)).RemovalAskedAt.ShouldNotBeNull();

        // A second request for the breach while one waits is refused.
        using (var second = await admin.SendRawAsync(HttpMethod.Post, $"{path}/affected/remove", new RemoveAffectedRequest([kept.Id], "Also by mistake")))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        // The asker does not approve; another holder of the permission (the super administrator) does.
        using (var own = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Mine", asked.Version)))
        {
            (await own.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.self");
        }

        var dpo = await server.SignInAsync(Users.Admin);
        await dpo.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Checked the e-mail.", asked.Version));
        (await admin.GetAsync<PagedResult<AffectedPatientItem>>($"{path}/affected")).Items.ShouldHaveSingleItem().PatientId.ShouldBe(kept.Id);
        var audit = (await AuditAsync(breach.Id, "privacy.breach.affected_removed")).ShouldHaveSingleItem();
        audit.ShouldContain(removed.Uhid);
        audit.ShouldContain("Listed by mistake");
    }

    [Fact]
    public async Task A_patient_already_sent_the_notice_stays_on_the_list()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var breach = await RecordAsync(admin);
        var path = $"/api/v1/privacy/breaches/{breach.Id}";
        var patient = await RegisterPatientAsync();
        await admin.PostAsync<AddAffectedResult>($"{path}/affected", new AddAffectedRequest([patient.Id], null));
        await admin.PutAsync(path, Complete(await admin.GetAsync<BreachDetail>(path)));
        await admin.PostAsync<NotifyAffectedResult>($"{path}/notify");

        await RefusedAsync(admin, $"{path}/affected/remove", new RemoveAffectedRequest([patient.Id], "Changed our mind"), "privacy.breach_already_notified");
    }

    private static async Task<BreachCreated> RecordAsync(ApiClient officer)
    {
        var aware = DateTimeOffset.UtcNow.AddHours(-3);
        return await officer.PostAsync<BreachCreated>("/api/v1/privacy/breaches", new SaveBreachRequest(
            "Discharge summaries e-mailed to the wrong address", "Two discharge summaries were e-mailed to another patient.", aware, aware.AddHours(-1), null,
            [BreachNature.Confidentiality], "Ward e-mail", [BreachDataCategory.Health], 2, null, null, null, null, null, null, null, null));
    }

    private static SaveBreachRequest Complete(BreachDetail detail) => new(
        detail.Title, detail.Description, detail.AwareAt, detail.OccurredFrom, detail.OccurredTo, detail.Nature, detail.Location, detail.DataCategories,
        detail.EstimatedAffected, "Someone may know about your treatment.", "We asked the recipient to delete the e-mail.",
        "No action is needed.", "The address was chosen wrongly.", "A member of staff chose the wrong address.", "Addresses are checked before sending.",
        "Your discharge summary was e-mailed to another patient, who deleted it.", detail.Version);

    private async Task<PatientCreated> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: "Meera", MiddleName: null, LastName: "Breach" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))),
            Gender: Gender.Female, DateOfBirth: new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
    }

    private static async Task RefusedAsync(ApiClient client, string path, object body, string code)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(code);
    }

    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, string path, object body)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path);
    }

    private async Task<List<string>> AuditAsync(Guid breachId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.EntityId == breachId.ToString() && a.Action == action && a.PatientId == null)
            .Select(a => a.Summary!)
            .ToListAsync(Ct);
    }
}
