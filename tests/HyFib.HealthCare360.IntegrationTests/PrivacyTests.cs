using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Breaches;
using HyFib.HealthCare360.Modules.Privacy.Features.Portal;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Modules.Privacy.Features.Retention;
using HyFib.HealthCare360.Modules.Privacy.Features.Settings;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// DPDP Act 2023 journeys over the real API and PostgreSQL: a patient's requests from the portal answered by the
/// grievance officer, erasure within what the law requires to be kept, a personal data breach from the register to the
/// patient's notice, and the retention schedule with its clean-up.
/// </summary>
public sealed class PrivacyTests(HospitalServer server)
{
    [Fact]
    public async Task A_patient_asks_for_a_correction_in_the_portal_and_the_officer_answers()
    {
        var (patient, phone) = await RegisterPatientAsync();
        var portal = await server.SignInToPortalAsync(phone);

        // The summary is there at once, without a request.
        var summary = await portal.GetAsync<DataSummary>($"/api/v1/portal/privacy/summary?patientId={patient.Id}");
        summary.Person.Uhid.ShouldBe(patient.Uhid);
        summary.Categories.Select(c => c.Key).ShouldContain("registration");
        summary.Consents.ShouldContain(c => c.Type == nameof(ConsentType.SmsWhatsAppUpdates) && c.Granted);

        var created = await portal.PostAsync<PortalRequestCreated>("/api/v1/portal/privacy/requests", new PortalDataRequestCreate(
            patient.Id, DataRequestType.Correction, RequesterRelation.Self, null, null,
            [new CorrectionInput(CorrectableFields.Email, "asha.privacy@example.in")]));
        created.DueAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(1));

        (await portal.GetAsync<List<PortalDataRequestItem>>("/api/v1/portal/privacy/requests")).ShouldContain(r => r.Id == created.Id);

        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await officer.GetAsync<PrivacyOverview>("/api/v1/privacy/overview")).Open.ShouldBeGreaterThanOrEqualTo(1);
        var queue = await officer.GetAsync<PagedResult<DataRequestListItem>>($"/api/v1/privacy/requests?openOnly=true&search={created.RequestNumber}");
        var listed = queue.Items.ShouldHaveSingleItem();
        listed.IdentityVerified.ShouldBeTrue("The patient asked for themselves after signing in with a code sent to their number.");
        listed.Uhid.ShouldBe(patient.Uhid);

        await officer.PostAsync($"/api/v1/privacy/requests/{created.Id}/start");
        await officer.PostAsync($"/api/v1/privacy/requests/{created.Id}/corrections",
            new DecideCorrectionsRequest([new CorrectionDecisionInput(0, CorrectionDecision.Accepted, null)]));
        await officer.PostAsync($"/api/v1/privacy/requests/{created.Id}/notes", new DataRequestNoteRequest("Updated in the patient record."));
        await officer.PostAsync($"/api/v1/privacy/requests/{created.Id}/close",
            new CloseDataRequestRequest(DataRequestStatus.Completed, "We have updated your e-mail address."));

        var seen = await portal.GetAsync<PortalDataRequestDetail>($"/api/v1/portal/privacy/requests/{created.Id}");
        seen.Status.ShouldBe(DataRequestStatus.Completed);
        seen.Response.ShouldBe("We have updated your e-mail address.");
        seen.CanWithdraw.ShouldBeFalse();
        seen.Timeline.ShouldNotContain(e => e.Kind == DataRequestEventKind.Note, "Staff notes stay with the staff.");
        seen.Corrections.ShouldHaveSingleItem().Decision.ShouldBe(CorrectionDecision.Accepted);

        (await QueuedTemplatesAsync(phone)).ShouldContain(DataRequestService.ReceivedTemplate);
        (await QueuedTemplatesAsync(phone)).ShouldContain(DataRequestService.ClosedTemplate);
    }

    [Fact]
    public async Task Erasure_removes_what_it_can_and_keeps_what_the_law_requires()
    {
        var (patient, phone) = await RegisterPatientAsync();
        var portal = await server.SignInToPortalAsync(phone);
        await portal.PutAsync("/api/v1/portal/privacy/nominee",
            new PortalNomineeRequest(patient.Id, "Ravi Rao", "Husband", "98" + Digits(8), null));
        var nominee = await portal.GetAsync<JsonElement>($"/api/v1/portal/privacy/nominee?patientId={patient.Id}");
        nominee.GetProperty("nominee").GetProperty("name").GetString().ShouldBe("Ravi Rao");
        var created = await portal.PostAsync<PortalRequestCreated>("/api/v1/portal/privacy/requests", new PortalDataRequestCreate(
            patient.Id, DataRequestType.Erasure, RequesterRelation.Self, null, "Please erase my data.", null));
        var feedbackLink = await InviteToFeedbackAsync(patient.Id);

        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var assessment = await officer.GetAsync<ErasureAssessment>($"/api/v1/privacy/requests/{created.Id}/erasure");
        assessment.Options.Single(o => o.Item == ErasureService.Messages).Available.ShouldBeTrue();
        assessment.Options.Single(o => o.Item == ErasureService.NomineeItem).Available.ShouldBeTrue();
        assessment.Options.Single(o => o.Item == ErasureService.PortalAccount).Available.ShouldBeTrue();
        assessment.Options.Single(o => o.Item == ErasureService.Feedback).Available.ShouldBeTrue("The patient has an unused feedback link.");
        assessment.Holds.ShouldContain(h => h.Key == "audit", "Opening the record left a trace in the audit trail, which is kept.");

        var outcome = await officer.PostAsync<ErasureOutcome>($"/api/v1/privacy/requests/{created.Id}/erasure",
            new EraseDataRequest([ErasureService.Messages, ErasureService.Feedback, ErasureService.NomineeItem, ErasureService.PortalAccount]));
        outcome.Erased.ShouldBe([ErasureService.Messages, ErasureService.Feedback, ErasureService.NomineeItem, ErasureService.PortalAccount]);

        using (var deadLink = await server.Anonymous().SendRawAsync(HttpMethod.Get, $"/api/v1/public/feedback/invitations/{feedbackLink}"))
        {
            deadLink.StatusCode.ShouldBe(HttpStatusCode.NotFound, "A feedback link sent before the erasure can no longer add a response tied to the patient.");
        }
        outcome.KeepUntil.ShouldNotBeNull();

        using (var signedOut = await portal.SendRawAsync(HttpMethod.Get, "/api/v1/portal/privacy/requests"))
        {
            signedOut.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "Closing the portal account ends its sessions.");
        }

        var preferences = await (await server.SignInAsync(HospitalServer.Users.FrontOffice)).GetAsync<JsonElement>($"/api/v1/patients/{patient.Id}/consents");
        preferences.EnumerateArray().Where(c => c.GetProperty("type").GetString() == nameof(ConsentType.SmsWhatsAppUpdates))
            .ShouldContain(c => c.GetProperty("granted").GetBoolean() == false, "The optional-message consent was withdrawn.");

        await officer.PostAsync($"/api/v1/privacy/requests/{created.Id}/close",
            new CloseDataRequestRequest(DataRequestStatus.PartlyCompleted, outcome.Summary));
        var detail = await officer.GetAsync<DataRequestDetail>($"/api/v1/privacy/requests/{created.Id}");
        detail.Status.ShouldBe(DataRequestStatus.PartlyCompleted);
        detail.ErasedItems.ShouldBe([ErasureService.Messages, ErasureService.Feedback, ErasureService.NomineeItem, ErasureService.PortalAccount]);
        detail.RetainedUntil.ShouldBe(outcome.KeepUntil);
    }

    [Fact]
    public async Task A_breach_is_reported_and_the_people_affected_are_told()
    {
        var (patient, phone) = await RegisterPatientAsync();
        var portal = await server.SignInToPortalAsync(phone);
        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var aware = DateTimeOffset.UtcNow.AddHours(-2);

        var created = await officer.PostAsync<JsonElement>("/api/v1/privacy/breaches", new SaveBreachRequest(
            "Report e-mailed to the wrong address", "A lab report was e-mailed to another patient.", aware, aware.AddHours(-1), null,
            [BreachNature.Confidentiality], "Laboratory e-mail", [BreachDataCategory.Health], 1, null, null, null, null, null, null, null));
        var breachId = created.GetGuid();

        var added = await officer.PostAsync<AddAffectedResult>($"/api/v1/privacy/breaches/{breachId}/affected",
            new AddAffectedRequest(null, [patient.Uhid.ToLowerInvariant(), "NOSUCHUHID1"]));
        added.Added.ShouldBe(1);
        added.NotFound.ShouldBe(["NOSUCHUHID1"]);

        using (var incomplete = await officer.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/breaches/{breachId}/notify"))
        {
            incomplete.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "A notice must say what it means for people and what they can do.");
        }

        var detail = await officer.GetAsync<BreachDetail>($"/api/v1/privacy/breaches/{breachId}");
        detail.ReportDueAt.ShouldBe(aware.AddHours(72), TimeSpan.FromSeconds(1));
        await officer.PutAsync($"/api/v1/privacy/breaches/{breachId}", Complete(detail));

        var notified = await officer.PostAsync<NotifyAffectedResult>($"/api/v1/privacy/breaches/{breachId}/notify");
        notified.Queued.ShouldBe(1);
        (await QueuedTemplatesAsync(phone)).ShouldContain(BreachService.NoticeTemplate);

        var notices = await portal.GetAsync<List<PortalBreachNotice>>($"/api/v1/portal/privacy/notices?patientId={patient.Id}");
        notices.ShouldHaveSingleItem().Description.ShouldBe("A lab report was e-mailed to another patient.");

        await officer.PostAsync($"/api/v1/privacy/breaches/{breachId}/board-informed", new BoardInformedRequest(aware.AddMinutes(30), null));
        await officer.PostAsync($"/api/v1/privacy/breaches/{breachId}/report-submitted", new ReportSubmittedRequest(DateTimeOffset.UtcNow, "DPB/TEST/1"));
        var register = await officer.GetAsync<PagedResult<BreachListItem>>("/api/v1/privacy/breaches?openOnly=true");
        register.Items.Single(b => b.Id == breachId).ShouldSatisfyAllConditions(
            b => b.Status.ShouldBe(BreachStatus.ReportSubmitted),
            b => b.AffectedListed.ShouldBe(1),
            b => b.Notified.ShouldBe(1));
        var report = await officer.GetAsync<BreachReport>($"/api/v1/privacy/breaches/{breachId}/report");
        report.Breach.Status.ShouldBe(BreachStatus.ReportSubmitted);
        report.Breach.BoardReference.ShouldBe("DPB/TEST/1");
        report.Breach.Notified.ShouldBe(1);
    }

    [Fact]
    public async Task The_clean_up_removes_only_operational_records_past_their_period()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-400);
        var phone = "+919" + Digits(9);
        Guid sentOld, sentNew, pendingOld, logOld, challengeOld, sessionOld, tokenOld;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var outbox = new[]
            {
                new OutboxMessage { Type = "privacy.test", Status = OutboxStatus.Succeeded, CreatedAt = old, NextAttemptAt = old, ProcessedAt = old },
                new OutboxMessage { Type = "privacy.test", Status = OutboxStatus.Succeeded, CreatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow, ProcessedAt = DateTimeOffset.UtcNow },
                new OutboxMessage { Type = "privacy.test", Status = OutboxStatus.Pending, CreatedAt = old, NextAttemptAt = old },
            };
            var log = new NotificationLog { CreatedAt = old, Channel = NotificationChannel.Sms, TemplateKey = "privacy.test", Recipient = Phone.Mask(phone) };
            var challenge = new AuthChallenge { Kind = AuthChallengeKind.Otp, Phone = phone, CreatedAt = old, ExpiresAt = old.AddMinutes(10) };
            var session = new UserSession { UserId = Guid.CreateVersion7(), CreatedAt = old, LastSeenAt = old, ExpiresAt = old.AddHours(12), RevokedAt = old.AddHours(1) };
            var token = new RefreshToken { SessionId = session.Id, TokenHash = Guid.NewGuid().ToString("N"), CreatedAt = old, ExpiresAt = old.AddDays(1) };
            db.AddRange(outbox);
            db.AddRange(log, challenge, session, token);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            (sentOld, sentNew, pendingOld) = (outbox[0].Id, outbox[1].Id, outbox[2].Id);
            (logOld, challengeOld, sessionOld, tokenOld) = (log.Id, challenge.Id, session.Id, token.Id);
        }

        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var preview = await admin.GetAsync<List<RetentionRunResult>>("/api/v1/privacy/retention/preview");
        preview.Single(r => r.Key == RetentionSchedule.Outbox).Removed.ShouldBeGreaterThanOrEqualTo(1);
        RetentionRun run;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            run = await scope.ServiceProvider.GetRequiredService<RetentionService>()
                .RunAsync(RetentionRunTrigger.Manual, null, TestContext.Current.CancellationToken);
        }

        run.Error.ShouldBeNull();
        run.Results.Select(r => r.Key).ShouldBe([RetentionSchedule.Outbox, RetentionSchedule.MessageLog, RetentionSchedule.IntegrationLog, RetentionSchedule.SignInLog]);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var ct = TestContext.Current.CancellationToken;
            var left = await db.Set<OutboxMessage>().Where(m => m.Id == sentOld || m.Id == sentNew || m.Id == pendingOld).Select(m => m.Id).ToListAsync(ct);
            left.ShouldBe([sentNew, pendingOld], ignoreOrder: true, "A message still waiting to be sent is never removed.");
            (await db.Set<NotificationLog>().AnyAsync(l => l.Id == logOld, ct)).ShouldBeFalse();
            (await db.Set<AuthChallenge>().AnyAsync(c => c.Id == challengeOld, ct)).ShouldBeFalse();
            (await db.Set<UserSession>().AnyAsync(s => s.Id == sessionOld, ct)).ShouldBeFalse();
            (await db.Set<RefreshToken>().AnyAsync(t => t.Id == tokenOld, ct)).ShouldBeFalse();
        }

        var runs = await admin.GetAsync<List<RetentionRunDto>>("/api/v1/privacy/retention/runs");
        runs.ShouldContain(r => r.Id == run.Id && r.TotalRemoved >= 5, "The old message, log entry, code request, session and its token.");

        // Asking for a clean-up from the screen only queues it: the job runs it in the background.
        using var accepted = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/privacy/retention/run");
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Changing_a_medical_records_period_moves_the_dates_of_completed_charts()
    {
        var (patient, _) = await RegisterPatientAsync();
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("No facility.");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid chartId;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var chart = new MedicalRecord { FacilityId = facilityId, PatientId = patient.Id };
            chart.Complete(null, DateTimeOffset.UtcNow, today).IsSuccess.ShouldBeTrue();
            db.Add(chart);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            chartId = chart.Id;
        }

        try
        {
            await admin.PutAsync($"/api/v1/privacy/retention/{RetentionSchedule.OutpatientRecords}", new UpdateRetentionRequest(7));
            (await RetainUntilAsync(chartId)).ShouldBe(today.AddYears(7));

            using var tooShort = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/privacy/retention/{RetentionSchedule.OutpatientRecords}", new UpdateRetentionRequest(2));
            tooShort.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "The SRS keeps out-patient records at least 3 years.");
        }
        finally
        {
            await admin.PutAsync($"/api/v1/privacy/retention/{RetentionSchedule.OutpatientRecords}", new UpdateRetentionRequest(3));
        }

        (await RetainUntilAsync(chartId)).ShouldBe(today.AddYears(3));
    }

    [Fact]
    public async Task The_privacy_notice_names_whom_to_contact_and_only_officers_answer_requests()
    {
        var anonymous = server.Anonymous();
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var notice = await anonymous.GetAsync<PublicPrivacyInfo>("/api/v1/public/privacy");
        notice.Hospital.ShouldNotBeNullOrWhiteSpace();
        notice.Contact.Title.ShouldBe(PrivacySettings.DefaultTitle);

        try
        {
            await admin.PutAsync("/api/v1/privacy/settings",
                new SavePrivacySettingsRequest("Grievance Officer", "Dr. Meera Iyer", "privacy@nana.example.in", null, null, 45));
            var updated = await anonymous.GetAsync<PublicPrivacyInfo>("/api/v1/public/privacy");
            updated.ResponseDays.ShouldBe(45);
            updated.Contact.Name.ShouldBe("Dr. Meera Iyer");
            updated.Contact.Email.ShouldBe("privacy@nana.example.in");
        }
        finally
        {
            await admin.PutAsync("/api/v1/privacy/settings",
                new SavePrivacySettingsRequest("Grievance Officer", null, null, null, null, PrivacySettings.DefaultResponseDays));
        }

        // The front desk records a request for a walk-in patient, but only an officer answers it.
        var (patient, _) = await RegisterPatientAsync();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var logged = await frontOffice.PostAsync<JsonElement>("/api/v1/privacy/requests", new CreateDataRequestRequest(
            DataRequestType.Grievance, DataRequestChannel.FrontDesk, patient.Id, null, null, null, RequesterRelation.Self, null,
            "I still get offers after saying no.", null, IdentityCheck.PhotoId, null));
        var requestId = logged.GetProperty("id").GetGuid();
        using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/requests/{requestId}/close",
            new CloseDataRequestRequest(DataRequestStatus.Completed, "Done."));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Another family's portal account cannot see it.
        var stranger = await server.SignInToPortalAsync("97" + Digits(8));
        using var hidden = await stranger.SendRawAsync(HttpMethod.Get, $"/api/v1/portal/privacy/requests/{requestId}");
        hidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<(PatientCreated Patient, string Phone)> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var phone = "9" + Digits(9);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: "Asha", MiddleName: null, LastName: "Privacy" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        return (patient, phone);
    }

    /// <summary>A feedback link sent to the patient and not yet used; returns the link's token.</summary>
    private async Task<string> InviteToFeedbackAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var form = await db.Set<FeedbackForm>().IgnoreQueryFilters().AsNoTracking().FirstAsync(f => f.IsActive, ct);
        var token = OpaqueToken.New();
        db.Add(new FeedbackInvitation
        {
            FacilityId = form.FacilityId,
            FormId = form.Id,
            PatientId = patientId,
            TokenHash = OpaqueToken.Hash(token),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    private static SaveBreachRequest Complete(BreachDetail detail) => new(
        detail.Title, detail.Description, detail.AwareAt, detail.OccurredFrom, detail.OccurredTo, detail.Nature, detail.Location, detail.DataCategories,
        detail.EstimatedAffected, "Someone may know about your test results.", "We asked the recipient to delete the e-mail, and they confirmed it.",
        "No action is needed; be careful of calls about your results.", "The e-mail address was typed wrongly.", "Addresses are now checked before sending.",
        "A lab report of yours was e-mailed to another patient, who deleted it.", detail.Version);

    private async Task<List<string>> QueuedTemplatesAsync(string phone)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(TestContext.Current.CancellationToken);
        return [.. messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.To == Phone.Normalize(phone)).Select(d => d.TemplateKey)];
    }

    private async Task<DateOnly?> RetainUntilAsync(Guid chartId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<MedicalRecord>().IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == chartId).Select(m => m.RetainUntil).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
