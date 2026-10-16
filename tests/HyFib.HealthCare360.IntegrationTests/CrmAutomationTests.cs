using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Immunizations;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Complaints;
using HyFib.HealthCare360.Modules.Engagement.Features.FollowUps;
using HyFib.HealthCare360.Modules.Engagement.Features.Patient360;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The CRM's own follow-ups and complaint escalation over the real API (CRM-02, FB-03): what the doctor and the nurse
/// record starts the follow-ups, the reminder job tells the patient, the next visit or dose settles them, and a
/// complaint left past its deadline is escalated once. The jobs' passes are run directly, as the job would run them.
/// </summary>
public sealed class CrmAutomationTests(HospitalServer server)
{
    /// <summary>India Standard Time, the hospital's calendar (no daylight saving).</summary>
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IndiaOffset).DateTime);

    [Fact]
    public async Task A_consultation_schedules_the_follow_up_and_the_chronic_review_and_the_next_visit_settles_them()
    {
        var patient = await RegisterPatientWithSmsConsentAsync();
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var crm = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // The doctor records type 2 diabetes as a chronic problem and asks to see the patient tomorrow.
        var first = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.Id, VisitId: null, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null,
            ChiefComplaint: "High sugar readings at home"));
        first = await doctor.PutAsync<EncounterDetail>($"/api/v1/emr/encounters/{first.Id}", new UpdateEncounterRequest(
            ChiefComplaint: "High sugar readings at home", HistoryOfPresentIllness: null, Examination: null,
            Assessment: "Type 2 diabetes, poorly controlled.", Plan: "Adjust metformin; review tomorrow with fasting sugar.",
            FollowUpDate: Today.AddDays(1), FollowUpAdvice: "Bring the fasting sugar report.", ReferralDepartmentId: null,
            ReferralPractitionerId: null, ReferralExternal: null, ReferralReason: null, first.Version));
        await doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{first.Id}/diagnoses",
            new AddDiagnosisRequest("E11.9", Title: null, DiagnosisType.Final, IsPrimary: true, OnsetDate: null, DiagnosisStatus.Chronic, Notes: null));
        await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{first.Id}/sign", new SignEncounterRequest(first.Version));

        // The CRM worklist has the review tomorrow and the chronic review three months on.
        var tasks = await FollowUpsAsync(crm, patient.Uhid);
        var review = tasks.Where(t => t.Type == FollowUpType.PostConsultation).ShouldHaveSingleItem();
        review.DueDate.ShouldBe(Today.AddDays(1));
        tasks.Where(t => t.Type == FollowUpType.ChronicReview).ShouldHaveSingleItem().DueDate.ShouldBe(Today.AddMonths(3));
        var detail = await crm.GetAsync<FollowUpDetail>($"/api/v1/crm/follow-ups/{review.Id}");
        detail.Source.ShouldBe(FollowUpSource.System);
        detail.VisitId.ShouldBeNull("This consultation had no visit.");
        detail.Notes.ShouldNotBeNull().ShouldNotContain("diabetes", Case.Insensitive, "The CRM team does not see the chart.");

        // The reminder job texts the patient about tomorrow's review, once; the chronic review is months away.
        await RunUntilAsync((monitor, ct) => monitor.SendFollowUpRemindersAsync(ct),
            async () => (await crm.GetAsync<FollowUpDetail>($"/api/v1/crm/follow-ups/{review.Id}")).ReminderSentAt is not null);
        await RunAsync((monitor, ct) => monitor.SendFollowUpRemindersAsync(ct));
        (await NotificationsAsync(patient.Id, FollowUpReminders.Template)).ShouldBe([NotificationChannel.Sms]);
        (await FollowUpsAsync(crm, patient.Uhid)).Where(t => t.Type == FollowUpType.ChronicReview).ShouldHaveSingleItem().ReminderSentAt.ShouldBeNull();

        // The patient comes back today: the review is done, and the chronic review moves to three months from now.
        var second = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.Id, VisitId: null, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null,
            ChiefComplaint: "Fasting sugar review"));
        await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{second.Id}/sign", new SignEncounterRequest(second.Version));

        tasks = await FollowUpsAsync(crm, patient.Uhid, includeClosed: true);
        var done = tasks.Where(t => t.Type == FollowUpType.PostConsultation).ShouldHaveSingleItem();
        done.Status.ShouldBe(FollowUpStatus.Completed);
        done.Outcome.ShouldBe(FollowUpOutcome.Visited);
        var chronic = tasks.Where(t => t.Type == FollowUpType.ChronicReview).ToList();
        chronic.Count.ShouldBe(2);
        chronic.ShouldContain(t => t.Status == FollowUpStatus.Cancelled, "The earlier review is replaced.");
        chronic.Where(t => t.Status == FollowUpStatus.Open).ShouldHaveSingleItem().DueDate.ShouldBe(Today.AddMonths(3));
    }

    [Fact]
    public async Task An_amended_note_moves_or_removes_its_follow_up()
    {
        var patient = await RegisterPatientWithSmsConsentAsync();
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var crm = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        const string Complaint = "Cough for two weeks";

        // Signed with a review in ten days.
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.Id, VisitId: null, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, Complaint));
        encounter = await doctor.PutAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}", new UpdateEncounterRequest(
            Complaint, HistoryOfPresentIllness: null, Examination: null, Assessment: "Acute bronchitis.", Plan: null, FollowUpDate: Today.AddDays(10),
            FollowUpAdvice: null, ReferralDepartmentId: null, ReferralPractitionerId: null, ReferralExternal: null, ReferralReason: null, encounter.Version));
        encounter = await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}/sign", new SignEncounterRequest(encounter.Version));
        (await FollowUpsAsync(crm, patient.Uhid)).ShouldHaveSingleItem().DueDate.ShouldBe(Today.AddDays(10));

        // The doctor moves the review in an amendment: the follow-up moves with it.
        encounter = await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}/amend", Amendment(Today.AddDays(20), "Review after the course of antibiotics"));
        (await FollowUpsAsync(crm, patient.Uhid)).ShouldHaveSingleItem().DueDate.ShouldBe(Today.AddDays(20));

        // The doctor removes it: the follow-up is cancelled.
        await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}/amend", Amendment(null, "No review needed"));
        (await FollowUpsAsync(crm, patient.Uhid, includeClosed: true)).ShouldHaveSingleItem().Status.ShouldBe(FollowUpStatus.Cancelled);

        AmendEncounterRequest Amendment(DateOnly? followUpDate, string reason) => new(
            reason, Complaint, HistoryOfPresentIllness: null, Examination: null, Assessment: "Acute bronchitis.", Plan: null, followUpDate,
            FollowUpAdvice: null, ReferralDepartmentId: null, ReferralPractitionerId: null, ReferralExternal: null, ReferralReason: null, encounter.Version);
    }

    [Fact]
    public async Task A_vaccine_dose_schedules_the_next_dose_and_the_next_dose_settles_it()
    {
        var patient = await RegisterPatientWithSmsConsentAsync();
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var crm = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var path = $"/api/v1/emr/patients/{patient.Id}/immunizations";

        // The first dose, transcribed from the patient's card, is due again in three days.
        await nurse.PostAsync<ImmunizationDto>(path, new RecordImmunizationRequest(
            EncounterId: null, "Hepatitis B", "HEPB", DoseNumber: 1, Today.AddDays(-28), Route: "IM", Site: null, BatchNumber: null,
            Manufacturer: null, ExpiryDate: null, AdministeredBy: "PHC Tambaram", IsExternal: true, NextDueOn: Today.AddDays(3), Notes: null));
        var first = (await FollowUpsAsync(crm, patient.Uhid)).ShouldHaveSingleItem();
        first.Type.ShouldBe(FollowUpType.Vaccination);
        first.DueDate.ShouldBe(Today.AddDays(3));

        // The second dose, given here today, settles that reminder and starts the one for the third dose.
        var second = await nurse.PostAsync<ImmunizationDto>(path, new RecordImmunizationRequest(
            EncounterId: null, "Hepatitis B", "HEPB", DoseNumber: 2, Today, Route: "IM", Site: "Left deltoid", BatchNumber: "HB26A41",
            Manufacturer: "Serum Institute", ExpiryDate: Today.AddMonths(18), AdministeredBy: null, IsExternal: false,
            NextDueOn: Today.AddMonths(5), Notes: null));
        var tasks = await FollowUpsAsync(crm, patient.Uhid, includeClosed: true);
        tasks.Where(t => t.Id == first.Id).ShouldHaveSingleItem().Outcome.ShouldBe(FollowUpOutcome.Visited);
        var third = tasks.Where(t => t.Status == FollowUpStatus.Open).ShouldHaveSingleItem();
        third.DueDate.ShouldBe(Today.AddMonths(5));

        // Marking the second dose as entered in error cancels the reminder it started.
        await nurse.PostAsync<ImmunizationDto>($"/api/v1/emr/immunizations/{second.Id}/entered-in-error", new EnteredInErrorRequest("Recorded for the wrong child"));
        (await FollowUpsAsync(crm, patient.Uhid, includeClosed: true)).Where(t => t.Id == third.Id).ShouldHaveSingleItem().Status.ShouldBe(FollowUpStatus.Cancelled);
    }

    [Fact]
    public async Task A_complaint_left_past_its_deadline_is_escalated_once()
    {
        var quality = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var id = await quality.PostAsync<Guid>("/api/v1/feedback/complaints", new CreateComplaintRequest(
            ComplaintSource.WalkIn, PatientId: null, DepartmentId: null, "Billing", "Waited 90 minutes at the billing counter",
            "Meena", "9" + Digits(9), ComplaintPriority.Normal, OwnerUserId: null));
        await RunAsync((monitor, ct) => monitor.EscalateOverdueComplaintsAsync(ct));
        (await quality.GetAsync<ComplaintDetail>($"/api/v1/feedback/complaints/{id}")).EscalatedAt.ShouldBeNull("It is within its 72 hours.");

        // Three days pass without a resolution.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var complaint = await db.Set<Complaint>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);
            complaint.DueAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await RunUntilAsync((monitor, ct) => monitor.EscalateOverdueComplaintsAsync(ct),
            async () => (await quality.GetAsync<ComplaintDetail>($"/api/v1/feedback/complaints/{id}")).EscalatedAt is not null);
        var escalated = await quality.GetAsync<ComplaintDetail>($"/api/v1/feedback/complaints/{id}");
        escalated.IsOverdue.ShouldBeTrue();
        var escalatedAt = escalated.EscalatedAt.ShouldNotBeNull();
        var alerts = await EscalationAlertsAsync(id);
        alerts.ShouldBeGreaterThan(0, "Quality managers and hospital administrators hear of it.");

        // The next pass changes nothing: a complaint is escalated once.
        await RunAsync((monitor, ct) => monitor.EscalateOverdueComplaintsAsync(ct));
        (await quality.GetAsync<ComplaintDetail>($"/api/v1/feedback/complaints/{id}")).EscalatedAt.ShouldBe(escalatedAt);
        (await EscalationAlertsAsync(id)).ShouldBe(alerts);
    }

    [Fact]
    public async Task The_communications_history_shows_the_messages_sent_and_the_calls_made_without_their_text()
    {
        var patient = await RegisterPatientWithSmsConsentAsync();
        var crm = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var path = $"/api/v1/crm/patients/{patient.Id}/communications";
        (await crm.GetAsync<PagedResult<CommunicationEntry>>(path)).Items.ShouldBeEmpty();

        // The CRM team texts the patient about a review, then calls and cannot get through.
        var task = await crm.PostAsync<Guid>("/api/v1/crm/follow-ups", new CreateFollowUpRequest(
            patient.Id, VisitId: null, FollowUpType.ChronicReview, Today.AddDays(1), AssigneeUserId: null, FollowUpPriority.Normal, Notes: null));
        await crm.PostAsync($"/api/v1/crm/follow-ups/{task}/remind");
        await crm.PostAsync($"/api/v1/crm/follow-ups/{task}/attempts", new FollowUpAttemptRequest(FollowUpOutcome.NotReachable, Notes: null, NextDueDate: null));

        var history = await crm.GetAsync<PagedResult<CommunicationEntry>>(path);
        history.TotalCount.ShouldBe(2);
        var sms = history.Items.Where(e => e.Kind == CommunicationKind.Message).ShouldHaveSingleItem();
        sms.TemplateKey.ShouldBe(FollowUpReminders.Template);
        sms.Channel.ShouldBe(NotificationChannel.Sms);
        sms.Recipient.ShouldNotBeNull().ShouldMatch(@"^\+91\*{6}\d{4}$", "Only the last four digits of the number show.");
        var call = history.Items.Where(e => e.Kind == CommunicationKind.FollowUpCall).ShouldHaveSingleItem();
        call.FollowUpId.ShouldBe(task);
        call.CallOutcome.ShouldBe(FollowUpOutcome.NotReachable);
        call.CallAttempts.ShouldBe(1);

        // One kind at a time, a page at a time.
        (await crm.GetAsync<PagedResult<CommunicationEntry>>($"{path}?kind=FollowUpCall")).Items.ShouldHaveSingleItem().Id.ShouldBe(task);
        (await crm.GetAsync<PagedResult<CommunicationEntry>>($"{path}?kind=Campaign")).Items.ShouldBeEmpty();
        var first = await crm.GetAsync<PagedResult<CommunicationEntry>>($"{path}?pageSize=1");
        first.Items.ShouldHaveSingleItem();
        first.TotalCount.ShouldBe(2);

        // Reading it is recorded as access to the patient's record.
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                .CountAsync(a => a.PatientId == patient.Id && a.Summary == "Viewed the CRM communications history", TestContext.Current.CancellationToken))
            .ShouldBeGreaterThanOrEqualTo(4);
    }

    private async Task<PatientCreated> RegisterPatientWithSmsConsentAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: "Lata", MiddleName: null, LastName: "Recall" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        await frontOffice.PostAsync($"/api/v1/patients/{patient.Id}/consents", new CaptureConsentRequest(
            ConsentType.SmsWhatsAppUpdates, ConsentVersion: "1.0", Granted: true, WitnessName: null, SignedByRelation: null, SignedByName: null,
            DocumentFileId: null, Notes: null));
        return patient;
    }

    private static async Task<List<FollowUpListItem>> FollowUpsAsync(ApiClient crm, string uhid, bool includeClosed = false) =>
        [.. (await crm.GetAsync<PagedResult<FollowUpListItem>>($"/api/v1/crm/follow-ups?search={uhid}&includeClosed={includeClosed}&pageSize=50")).Items];

    /// <summary>One pass of the Engagement job, in its own scope as the job runs it.</summary>
    private async Task<int> RunAsync(Func<EngagementMonitor, CancellationToken, Task<int>> pass)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await pass(scope.ServiceProvider.GetRequiredService<EngagementMonitor>(), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs passes until <paramref name="done"/>. The server's own job runs too, and a pass that finds the job holding
    /// its lock does nothing, so one pass alone could miss; whichever pass does the work, it is done once.
    /// </summary>
    private async Task RunUntilAsync(Func<EngagementMonitor, CancellationToken, Task<int>> pass, Func<Task<bool>> done)
    {
        for (var attempt = 0; attempt < 5 && !await done(); attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            }

            await RunAsync(pass);
        }

        (await done()).ShouldBeTrue("The job pass did not do its work.");
    }

    private async Task<List<NotificationChannel>> NotificationsAsync(Guid patientId, string template)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking()
            .Where(l => l.PatientId == patientId && l.TemplateKey == template)
            .Select(l => l.Channel)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> EscalationAlertsAsync(Guid complaintId)
    {
        var link = $"/feedback/complaints/{complaintId}";
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .CountAsync(n => n.Link == link && n.Title.Contains("escalated"), TestContext.Current.CancellationToken);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
