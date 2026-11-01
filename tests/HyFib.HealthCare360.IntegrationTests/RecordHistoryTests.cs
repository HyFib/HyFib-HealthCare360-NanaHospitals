using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A record's History over the real API, from the audit trail of real writes: who created and changed it, when, and each
/// field from what to what. It opens only for staff who may read the record, by the rules that open the record itself, and
/// reading it is in the patient's access log.
/// </summary>
public sealed class RecordHistoryTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patients_history_says_who_registered_and_changed_the_record_and_each_field_from_what_to_what()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var registered = await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}");
        await frontOffice.PutAsync($"/api/v1/patients/{patient.Id}", Edit(registered) with { Occupation = "Teacher", Notes = "Moved to Pune" });

        // A read recorded against the record itself, as a module may record one, is the access log's, not the History's.
        await using (var reading = server.Services.CreateAsyncScope())
        {
            await reading.ServiceProvider.GetRequiredService<IAuditLogger>().WriteAsync(
                new AuditEntry(AuditCategories.PhiAccess, "patient.accessed", "Viewed the record")
                {
                    PatientId = patient.Id,
                    EntityType = nameof(Patient),
                    EntityId = patient.Id.ToString(),
                }, Ct);
        }

        var history = await frontOffice.GetAsync<RecordHistoryDto>($"/api/v1/history/Patient/{patient.Id}");
        history.Entries.Items.ShouldNotContain(e => e.Action == "patient.accessed", "reads are in the access log");
        history.Title.ShouldBe($"patient {registered.Uhid}");
        var me = (await frontOffice.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName;

        // Newest first: the change, then the registration.
        var changed = history.Entries.Items[0];
        (changed.What, changed.By, changed.Action).ShouldBe(("Changed", me, "Patient.update"));
        changed.Changes.ShouldContain(new HistoryChange("Occupation", null, "Teacher"));
        changed.Changes.ShouldContain(new HistoryChange("Notes", null, "Moved to Pune"));
        changed.Changes.ShouldNotContain(c => c.Field == "Last name", "only what changed");

        var created = history.Entries.Items.Single(e => e.Action == "Patient.create");
        (created.What, created.By).ShouldBe(("Created", me));
        created.Changes.ShouldContain(new HistoryChange("Last name", null, registered.LastName));
        created.Changes.ShouldContain(new HistoryChange("Uhid", null, registered.Uhid));

        // Reading it is in the patient's access log.
        await using var scope = server.Services.CreateAsyncScope();
        var reads = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patient.Id && a.Category == AuditCategories.PhiAccess).Select(a => a.Summary).ToListAsync(Ct);
        reads.ShouldContain($"Viewed the history of patient {registered.Uhid}");
    }

    [Fact]
    public async Task A_lab_reports_history_has_each_result_on_it_who_entered_verified_and_corrected_it_and_the_amendment()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-ELECTROLYTES");
        var lines = await LabVerificationTests.EnterAsync(lab, tube, new Dictionary<string, string> { ["NA"] = "138", ["K"] = "4.2", ["CL"] = "101" });
        await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify", new VerifyResultsRequest([.. lines.Select(l => l.ResultId)], null, null, Release: true));
        var order = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{(await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results")).LabOrderId}");
        var reportId = order.ReportId.ShouldNotBeNull();
        var potassium = lines.Single(l => l.TestCode == "K");
        await pathologist.PostAsync($"/api/v1/lab/reports/{reportId}/amend", new AmendReportRequest(
            "Wrong tube read: the analyser printout of this sample says otherwise", [new AmendmentChange(potassium.ResultId, "5.8", null, null, null)], null));

        // A part deleted stays in the record's history, with its deletion. (No screen deletes a result; the rule holds for
        // any part a module ever deletes.)
        var chloride = lines.Single(l => l.TestCode == "CL");
        await using (var deleting = server.Services.CreateAsyncScope())
        {
            var context = deleting.ServiceProvider.GetRequiredService<HospitalDbContext>();
            context.Remove(await context.Set<LabResult>().SingleAsync(r => r.Id == chloride.ResultId, Ct));
            await context.SaveChangesAsync(Ct);
        }

        var history = await pathologist.GetAsync<RecordHistoryDto>($"/api/v1/history/LabReport/{reportId}?pageSize=100");
        var (technician, verifier) = (await NameAsync(lab), await NameAsync(pathologist));

        // Each result's entry, verification and correction are in the report's history, under the result's name.
        var ofPotassium = history.Entries.Items.Where(e => e.Part == potassium.TestName).ToList();
        ofPotassium.ShouldContain(e => e.By == technician && e.Changes.Contains(new HistoryChange("Value text", null, "4.2")), "entered by the technician");
        ofPotassium.ShouldContain(e => e.By == verifier && e.Changes.Any(c => c.Field == "Verified at" && c.From == null), "verified by the pathologist");
        ofPotassium.ShouldContain(e => e.By == verifier && e.Changes.Contains(new HistoryChange("Value text", "4.2", "5.8")), "corrected by the pathologist");
        history.Entries.Items.ShouldContain(e => e.Part == "Amendment 1" && e.What == "Created" && e.By == verifier);
        history.Entries.Items.ShouldContain(e => e.Part == null && e.Action == "lab.report.amended" && e.By == verifier);
        history.Entries.Items.ShouldContain(e => e.Part == chloride.TestName && e.What == "Deleted", "the deleted result's deletion");
        foreach (var other in lines.Where(l => l.TestCode != "K"))
        {
            history.Entries.Items.ShouldContain(e => e.Part == other.TestName && e.By == technician, $"{other.TestName} is on the report too");
        }

        // Every event the trail holds about the report and its parts is in its history, newest first, and nothing else.
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var amendments = (await db.Set<LabReportAmendment>().IgnoreQueryFilters().Where(a => a.LabReportId == reportId).Select(a => a.Id).ToListAsync(Ct))
            .Select(a => a.ToString()).ToList();
        var results = lines.Select(l => l.ResultId.ToString()).ToList();
        var report = reportId.ToString();
        var trail = await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.Category != AuditCategories.PhiAccess && (
                (a.EntityType == nameof(LabReport) && a.EntityId == report)
                || (a.EntityType == nameof(LabResult) && results.Contains(a.EntityId!))
                || (a.EntityType == nameof(LabReportAmendment) && amendments.Contains(a.EntityId!))))
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Select(a => a.Id).ToListAsync(Ct);
        history.Entries.Items.Select(e => e.Id).ShouldBe(trail);
        history.Entries.TotalCount.ShouldBe(trail.Count);

        // The order's history has each test ordered on it and each sample: who collected and received it.
        var ofOrder = await lab.GetAsync<RecordHistoryDto>($"/api/v1/history/LabOrder/{order.Id}?pageSize=100");
        ofOrder.Entries.Items.ShouldContain(e => e.Part == potassium.OrderedTest && e.What == "Created", "the test ordered");
        ofOrder.Entries.Items.ShouldContain(e => e.Part == $"Sample {tube.Barcode}" && e.By == technician
            && e.Changes.Any(c => c.Field == "Received at" && c.From == null), "the sample received, by the technician");
    }

    [Fact]
    public async Task A_bills_history_has_the_approvals_asked_for_on_it_who_asked_who_decided_and_why_and_what_was_made()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var bill = await MakerCheckerTests.FinalizedBillAsync(frontOffice, billing);

        // A credit note asked for and rejected, then one asked for and approved.
        var rejected = await AskForCreditAsync(billing, bill, 250m);
        await accountant.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{rejected.Id}/reject", new RejectApprovalRequest("The rate charged was right", rejected.Version));
        var approved = await AskForCreditAsync(billing, bill, 50m);
        await MakerChecker.ApproveAsync(accountant, approved);
        var credit = (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).CreditNotes.ShouldHaveSingleItem();

        var history = await billing.GetAsync<RecordHistoryDto>($"/api/v1/history/Bill/{bill.Id}?pageSize=100");
        var (maker, checker) = (await NameAsync(billing), await NameAsync(accountant));
        var approvals = history.Entries.Items.Where(e => e.Part == "Approval").ToList();
        approvals.Count(e => e.Action == "approval.requested" && e.By == maker).ShouldBe(2, "both asked for, by the maker");
        approvals.ShouldContain(e => e.Action == "approval.rejected" && e.By == checker && e.What.Contains("The rate charged was right"), "the rejection, by whom and why");
        approvals.ShouldContain(e => e.Action == "approval.approved" && e.By == checker);
        history.Entries.Items.ShouldContain(e => e.Part == $"Credit note {credit.Number}" && e.What == "Created");
        history.Entries.Items.ShouldContain(e => e.Part == null && e.Changes.Any(c =>
            c.Field == "Credit amount" && decimal.Parse(c.From!, CultureInfo.InvariantCulture) == 0m && decimal.Parse(c.To!, CultureInfo.InvariantCulture) == 50m), "the bill's own change");

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var key = bill.Id.ToString();
        var requests = new[] { rejected.Id.ToString(), approved.Id.ToString() };
        var credits = (await db.Set<CreditNote>().IgnoreQueryFilters().Where(c => c.BillId == bill.Id).Select(c => c.Id).ToListAsync(Ct)).Select(c => c.ToString()).ToList();
        var receipts = (await db.Set<Receipt>().IgnoreQueryFilters().Where(r => r.BillId == bill.Id).Select(r => r.Id).ToListAsync(Ct)).Select(r => r.ToString()).ToList();

        // The requests' steps, in words; their rows' own changes say the same field by field and stay in the trail.
        var trail = await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.Category != AuditCategories.PhiAccess && (
                (a.EntityType == nameof(Bill) && a.EntityId == key)
                || (a.EntityType == nameof(ApprovalRequest) && requests.Contains(a.EntityId!) && a.Action.StartsWith("approval."))
                || (a.EntityType == nameof(CreditNote) && credits.Contains(a.EntityId!))
                || (a.EntityType == nameof(Receipt) && receipts.Contains(a.EntityId!))))
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Select(a => a.Id).ToListAsync(Ct);
        history.Entries.Items.Select(e => e.Id).ShouldBe(trail);
    }

    [Fact]
    public async Task A_history_opens_only_for_staff_who_may_read_the_record_by_the_rules_that_open_the_record()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var cssd = await server.SignInAsync(HospitalServer.Users.CssdTechnician);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);

        // Staff who never see patients do not read a patient's history.
        (await RefusedAsync(cssd, $"/api/v1/history/Patient/{patient.Id}", HttpStatusCode.Forbidden)).ShouldBe("history.not_permitted");

        // A record that does not exist, and a kind of record that has no history, are not found.
        await RefusedAsync(frontOffice, $"/api/v1/history/Patient/{Guid.NewGuid()}", HttpStatusCode.NotFound);
        await RefusedAsync(frontOffice, $"/api/v1/history/Nonsense/{patient.Id}", HttpStatusCode.NotFound);

        // A restricted (VIP) patient's history is closed to those the record is closed to.
        var restricted = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: "Meenakshi", MiddleName: null, LastName: "History" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(9000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: true, Notes: null,
            ConfirmedNotDuplicate: true));
        (await RefusedAsync(nurse, $"/api/v1/history/Patient/{restricted.Id}", HttpStatusCode.Forbidden)).ShouldBe("patient.restricted");
    }

    [Fact]
    public async Task The_trail_is_read_by_record_through_its_index_built_without_holding_writes()
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AuditEntityIndex>().EnsureAsync(Ct)).ShouldBeTrue("every partition has its index, attached");

        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off", Ct);
        var plan = string.Join('\n', await db.Database.SqlQueryRaw<string>(
            "EXPLAIN SELECT id FROM audit.audit_events WHERE entity_type = 'Patient' AND entity_id = '00000000-0000-0000-0000-000000000000' ORDER BY occurred_at DESC LIMIT 25")
            .ToListAsync(Ct));
        plan.ShouldContain("_entity_idx", Case.Sensitive, plan);
    }

    private static UpdatePatientRequest Edit(PatientDetail p) => new(
        p.Title, p.FirstName, p.MiddleName, p.LastName, p.Gender, p.DateOfBirth, null, null, p.Phone, p.AlternatePhone,
        p.Email, p.Address, p.BloodGroup, p.MaritalStatus, p.GuardianName, p.GuardianRelation, p.EmergencyContactName,
        p.EmergencyContactPhone, p.EmergencyContactRelation, p.Occupation, p.Religion, p.Nationality, p.PreferredLanguage,
        p.Category, null, p.AbhaAddress, null, p.PhotoFileId, p.IsVip, p.IsDeceased, p.DeceasedAt, p.Notes,
        p.IsActive, p.Version);

    private static async Task<string> NameAsync(ApiClient client) => (await client.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName;

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static async Task<ApprovalRequestDto> AskForCreditAsync(ApiClient maker, BillDetail bill, decimal amount)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{bill.Id}/credit-notes",
            new IssueCreditNoteRequest(CreditNoteKind.CreditNote, amount, "Rate charged above the tariff"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    /// <summary>Is refused with <paramref name="status"/>, and returns the refusal's code.</summary>
    private static async Task<string?> RefusedAsync(ApiClient client, string path, HttpStatusCode status)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
