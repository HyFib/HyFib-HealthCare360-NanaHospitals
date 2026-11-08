using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Portal;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Modules.Privacy.Features.Settings;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The DPDP duties the privacy module gained, one rule a test, over the real API and PostgreSQL: consent managers and
/// their artefacts (Act s.6(7) to (9); Rules r4), a parent's or guardian's verifiable consent for a child (s.9; r10 to r12),
/// the Significant Data Fiduciary register (s.10; r13), and the erasure ledger kept outside the database (s.8(7), s.12(3)).
/// </summary>
public sealed class DpdpDutiesTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ consent managers (s.6(7); rule 4)

    [Fact]
    public async Task A_consent_managers_signed_artefact_is_recorded_as_the_patients_consent_and_honoured_until_withdrawn()
    {
        var (patient, phone) = await RegisterPatientAsync(adult: true);
        var (manager, secret) = await RegisterSimulatorAsync();

        var artefactId = "art-" + Letters(10);
        var answer = await SendEventAsync(manager.Code, secret, Granted(artefactId, patient.Uhid, phone, ConsentType.DataSharing, ["lab-reports", "discharge-summary"]));
        answer.StatusCode.ShouldBe(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync(Ct));

        // Recorded as the patient's data-sharing consent, so every check that reads consents honours it.
        var consents = await ConsentsAsync(patient.Id, ConsentType.DataSharing);
        var recorded = consents.ShouldHaveSingleItem();
        (recorded.Granted, recorded.RevokedAt, recorded.ConsentVersion).ShouldBe((true, (DateTimeOffset?)null, $"cm:{manager.Code}"));

        // The patient sees it in the portal with its purpose, data items and validity.
        var portal = await server.SignInToPortalAsync(phone);
        var seen = (await portal.GetAsync<List<ConsentArtefactDto>>($"/api/v1/portal/privacy/consent-artefacts?patientId={patient.Id}")).ShouldHaveSingleItem();
        (seen.ArtefactId, seen.Purpose, seen.InForce).ShouldBe((artefactId, ConsentType.DataSharing, true));
        seen.DataItems.ShouldBe(["lab-reports", "discharge-summary"]);

        // Withdrawn in the portal (s.6(4)): the consent is revoked at once and the withdrawal is sent to the consent manager.
        var withdrawn = await portal.PostAsync<ConsentArtefactDto>($"/api/v1/portal/privacy/consent-artefacts/{seen.Id}/withdraw");
        (withdrawn.Status, withdrawn.WithdrawnVia, withdrawn.WithdrawalPending).ShouldBe((ConsentArtefactStatus.Withdrawn, ConsentWithdrawalSource.Portal, false),
            "the simulator acknowledged the withdrawal");
        (await ConsentsAsync(patient.Id, ConsentType.DataSharing)).ShouldHaveSingleItem().RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_withdrawal_at_the_consent_manager_revokes_the_consent_here()
    {
        var (patient, phone) = await RegisterPatientAsync(adult: true);
        var (manager, secret) = await RegisterSimulatorAsync();
        var artefactId = "art-" + Letters(10);
        (await SendEventAsync(manager.Code, secret, Granted(artefactId, patient.Uhid, phone, ConsentType.Research, ["anonymised-outcomes"]))).EnsureSuccessStatusCode();

        var withdrawal = JsonSerializer.Serialize(new { eventId = Guid.NewGuid().ToString("N"), kind = ConsentManagerProtocol.Withdrawn, artefact = new { id = artefactId } });
        (await SendEventAsync(manager.Code, secret, withdrawal)).EnsureSuccessStatusCode();

        (await ConsentsAsync(patient.Id, ConsentType.Research)).ShouldHaveSingleItem().RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_event_is_refused_without_a_valid_signature_or_with_an_old_timestamp_and_acted_on_once()
    {
        var (patient, phone) = await RegisterPatientAsync(adult: true);
        var (manager, secret) = await RegisterSimulatorAsync();
        var body = Granted("art-" + Letters(10), patient.Uhid, phone, ConsentType.DataSharing, ["lab-reports"]);

        (await SendEventAsync(manager.Code, "not-the-secret", body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendEventAsync(manager.Code, secret, body, DateTimeOffset.UtcNow.AddMinutes(-10))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            "a signed event replayed later than five minutes is refused");
        (await ConsentsAsync(patient.Id, ConsentType.DataSharing)).ShouldBeEmpty("nothing unsigned or stale was recorded");

        (await SendEventAsync(manager.Code, secret, body)).EnsureSuccessStatusCode();
        var again = await SendEventAsync(manager.Code, secret, body);
        again.EnsureSuccessStatusCode();
        (await again.Content.ReadAsStringAsync(Ct)).ShouldContain("duplicate");
        (await ConsentsAsync(patient.Id, ConsentType.DataSharing)).Count.ShouldBe(1, "the same event delivered twice records one consent");
    }

    [Fact]
    public async Task An_artefact_past_its_validity_ends_and_its_consent_is_revoked()
    {
        var (patient, phone) = await RegisterPatientAsync(adult: true);
        var (manager, secret) = await RegisterSimulatorAsync();
        var artefactId = "art-" + Letters(10);
        (await SendEventAsync(manager.Code, secret, Granted(artefactId, patient.Uhid, phone, ConsentType.SmsWhatsAppUpdates, ["appointment-reminders"]))).EnsureSuccessStatusCode();

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Set<ConsentArtefact>().Where(a => a.ArtefactId == artefactId)
                .ExecuteUpdateAsync(a => a.SetProperty(x => x.ValidUntil, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
            await scope.ServiceProvider.GetRequiredService<ConsentManagerService>().SweepAsync(Ct);
        }

        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var artefact = (await admin.GetAsync<List<ConsentArtefactDto>>($"/api/v1/privacy/patients/{patient.Id}/consent-artefacts")).Single(a => a.ArtefactId == artefactId);
        (artefact.Status, artefact.InForce).ShouldBe((ConsentArtefactStatus.Expired, false));
        (await ConsentsAsync(patient.Id, ConsentType.SmsWhatsAppUpdates)).Single(c => c.ConsentVersion.StartsWith("cm:", StringComparison.Ordinal)).RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_artefact_for_a_patient_it_does_not_identify_or_for_health_care_is_refused()
    {
        var (patient, _) = await RegisterPatientAsync(adult: true);
        var (manager, secret) = await RegisterSimulatorAsync();

        var wrongNumber = await SendEventAsync(manager.Code, secret, Granted("art-" + Letters(10), patient.Uhid, "9" + Digits(9), ConsentType.DataSharing, ["lab-reports"]));
        wrongNumber.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "the UHID and the mobile number must both match the patient");

        var treatment = await SendEventAsync(manager.Code, secret, Granted("art-" + Letters(10), patient.Uhid, null, ConsentType.GeneralTreatment, ["all"]));
        treatment.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "health care needs no consent artefact");
    }

    // ------------------------------------------------------------------ children (s.9; rules 10 to 12)

    [Fact]
    public async Task A_childs_data_sharing_waits_for_a_guardians_consent_verified_by_a_code_to_the_guardians_number()
    {
        var (child, _) = await RegisterPatientAsync(adult: false);
        var desk = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        using (var refused = await desk.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/{child.Id}/consents", Consent(ConsentType.DataSharing)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(ChildData.GuardianNeededCode);
        }

        var guardianPhone = "9" + Digits(9);
        var started = await desk.PostAsync<GuardianConsentDto>($"/api/v1/privacy/patients/{child.Id}/guardian-consent", new StartGuardianConsentRequest(
            "Lakshmi Parent", GuardianRelationship.Parent, guardianPhone, true, GuardianVerificationMethod.Otp, null, null));
        started.Status.ShouldBe(GuardianConsentStatus.Pending);

        using (var wrong = await desk.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/patients/{child.Id}/guardian-consent/{started.Id}/verify", new VerifyGuardianCodeRequest("000000")))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var code = await CodeSentToAsync(guardianPhone, GuardianConsentService.CodeTemplate);
        var verified = await desk.PostAsync<GuardianConsentDto>($"/api/v1/privacy/patients/{child.Id}/guardian-consent/{started.Id}/verify", new VerifyGuardianCodeRequest(code));
        (verified.Status, verified.AttemptsLeft).ShouldBe((GuardianConsentStatus.Verified, GuardianConsent.MaxAttempts - 2), "the wrong try counted");

        await desk.PostAsync($"/api/v1/patients/{child.Id}/consents", Consent(ConsentType.DataSharing));
        (await ConsentsAsync(child.Id, ConsentType.DataSharing)).ShouldContain(c => c.Granted && c.RevokedAt == null);
    }

    [Fact]
    public async Task A_guardians_consent_can_be_verified_by_their_identity_document_and_withdrawn()
    {
        var (child, _) = await RegisterPatientAsync(adult: false);
        var desk = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var verified = await desk.PostAsync<GuardianConsentDto>($"/api/v1/privacy/patients/{child.Id}/guardian-consent", new StartGuardianConsentRequest(
            "Arun Guardian", GuardianRelationship.LawfulGuardian, "9" + Digits(9), true, GuardianVerificationMethod.Document, "Court order", "4821"));
        (verified.Status, verified.DocumentLast4).ShouldBe((GuardianConsentStatus.Verified, "4821"));

        await desk.PostAsync($"/api/v1/privacy/patients/{child.Id}/guardian-consent/{verified.Id}/revoke", new RevokeGuardianConsentRequest("The guardian withdrew."));
        var view = await desk.GetAsync<GuardianConsentView>($"/api/v1/privacy/patients/{child.Id}/guardian-consent");
        (view.IsChild, view.Verified).ShouldBe((true, false));
        using var refused = await desk.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/{child.Id}/consents", Consent(ConsentType.Research));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "with the guardian's consent withdrawn, research consent waits again");
    }

    [Fact]
    public async Task A_document_number_is_never_kept_whole_and_an_adult_needs_no_guardian()
    {
        var (child, _) = await RegisterPatientAsync(adult: false);
        var (adult, _) = await RegisterPatientAsync(adult: true);
        var desk = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        using (var whole = await desk.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/patients/{child.Id}/guardian-consent", new StartGuardianConsentRequest(
                   "Arun Guardian", GuardianRelationship.Parent, "9" + Digits(9), true, GuardianVerificationMethod.Document, "Aadhaar", "123412341234")))
        {
            whole.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var notAChild = await desk.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/patients/{adult.Id}/guardian-consent", new StartGuardianConsentRequest(
            "Someone", GuardianRelationship.Parent, "9" + Digits(9), true, GuardianVerificationMethod.Document, "Passport", "Z123"));
        notAChild.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await notAChild.Content.ReadAsStringAsync(Ct)).ShouldContain(GuardianConsentService.NotAChildCode);
    }

    [Fact]
    public async Task No_marketing_reaches_a_child_even_with_a_guardians_consent()
    {
        var (child, phone) = await RegisterPatientAsync(adult: false);
        var desk = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        await desk.PostAsync($"/api/v1/privacy/patients/{child.Id}/guardian-consent", new StartGuardianConsentRequest(
            "Lakshmi Parent", GuardianRelationship.Parent, "9" + Digits(9), true, GuardianVerificationMethod.Document, "Passport", "K921"));

        using (var desk422 = await desk.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/{child.Id}/consents", Consent(ConsentType.MarketingCommunication)))
        {
            desk422.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await desk422.Content.ReadAsStringAsync(Ct)).ShouldContain(ChildData.MarketingRefusedCode);
        }

        var (manager, secret) = await RegisterSimulatorAsync();
        var viaManager = await SendEventAsync(manager.Code, secret, Granted("art-" + Letters(10), child.Uhid, phone, ConsentType.MarketingCommunication, ["offers"], givenByGuardian: true));
        viaManager.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "nor through a consent manager");
        (await ConsentsAsync(child.Id, ConsentType.MarketingCommunication)).ShouldNotContain(c => c.Granted);
    }

    [Fact]
    public async Task Health_offers_cannot_be_switched_on_for_a_child_in_the_portal()
    {
        var (child, phone) = await RegisterPatientAsync(adult: false);
        var portal = await server.SignInToPortalAsync(phone);
        using var refused = await portal.SendRawAsync(HttpMethod.Put, "/api/v1/portal/communication-preferences",
            new { patientId = child.Id, marketing = true });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(ChildData.MarketingRefusedCode);
        (await ConsentsAsync(child.Id, ConsentType.MarketingCommunication)).ShouldNotContain(c => c.Granted);
    }

    [Fact]
    public async Task A_childs_consent_artefact_is_accepted_only_from_a_verified_guardian()
    {
        var (child, phone) = await RegisterPatientAsync(adult: false);
        var (manager, secret) = await RegisterSimulatorAsync();
        var body = Granted("art-" + Letters(10), child.Uhid, phone, ConsentType.DataSharing, ["vaccination-record"], givenByGuardian: true);
        (await SendEventAsync(manager.Code, secret, body)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "no guardian's consent is verified yet");

        var desk = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        await desk.PostAsync($"/api/v1/privacy/patients/{child.Id}/guardian-consent", new StartGuardianConsentRequest(
            "Lakshmi Parent", GuardianRelationship.Parent, "9" + Digits(9), true, GuardianVerificationMethod.Document, "Passport", "K921"));
        (await SendEventAsync(manager.Code, secret, body.Replace("\"eventId\":\"", "\"eventId\":\"2", StringComparison.Ordinal))).EnsureSuccessStatusCode();
    }

    // ------------------------------------------------------------------ Significant Data Fiduciary (s.10; rule 13)

    [Fact]
    public async Task A_dpia_is_approved_by_someone_other_than_who_sent_it_and_a_change_makes_it_a_draft_again()
    {
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var dpia = await maker.PostAsync<DpiaDetail>("/api/v1/privacy/sdf/dpias", Dpia(server.Today.AddMonths(11)));
        dpia.Number.ShouldStartWith("DPIA");
        dpia.HighestRisk.ShouldBe(RiskLevel.High);

        using var asked = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/sdf/dpias/{dpia.Id}/submit", new SubmitDpiaRequest("Reviewed with the IT team.", dpia.Version));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var approval = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        approval.CanDecide.ShouldBeFalse("nobody approves what they sent");
        using (var self = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest("Mine.", approval.Version)))
        {
            self.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await self.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.self");
        }

        (await server.ApproveGrantAsync(approval.Id)).Status.ShouldBe(ApprovalStatus.Approved);
        var approved = await maker.GetAsync<DpiaDetail>($"/api/v1/privacy/sdf/dpias/{dpia.Id}");
        approved.Status.ShouldBe(DpiaStatus.Approved);
        approved.ApprovedByName.ShouldNotBeNullOrEmpty("the approver is named, not the one who sent it");

        var changed = await maker.PutAsync<DpiaDetail>($"/api/v1/privacy/sdf/dpias/{dpia.Id}", Dpia(server.Today.AddMonths(11)) with { Activity = "Lab reports by WhatsApp and e-mail", Version = approved.Version });
        (changed.Status, changed.ApprovedAt).ShouldBe((DpiaStatus.Draft, (DateTimeOffset?)null), "its approval covered what it said before");
    }

    [Fact]
    public async Task A_dpia_changed_while_it_waits_for_approval_is_not_approved()
    {
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var dpia = await maker.PostAsync<DpiaDetail>("/api/v1/privacy/sdf/dpias", Dpia(server.Today.AddMonths(6)));
        using var asked = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/privacy/sdf/dpias/{dpia.Id}/submit", new SubmitDpiaRequest("Please approve.", dpia.Version));
        var approval = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        await maker.PutAsync<DpiaDetail>($"/api/v1/privacy/sdf/dpias/{dpia.Id}", Dpia(server.Today.AddMonths(6)) with { Purpose = "Changed after asking", Version = dpia.Version });

        var checker = await server.SignInAsync(HospitalServer.Users.Admin);
        var waiting = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval.Id}");
        using (var stale = await checker.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest("Approved.", waiting.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await stale.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.stale");
        }

        (await maker.GetAsync<DpiaDetail>($"/api/v1/privacy/sdf/dpias/{dpia.Id}")).Status.ShouldBe(DpiaStatus.Draft);
    }

    [Fact]
    public async Task A_dpia_is_reviewed_within_twelve_months_and_reminders_go_once_before_the_date()
    {
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        using (var tooLate = await maker.SendRawAsync(HttpMethod.Post, "/api/v1/privacy/sdf/dpias", Dpia(server.Today.AddMonths(13))))
        {
            tooLate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var dpia = await maker.PostAsync<DpiaDetail>("/api/v1/privacy/sdf/dpias", Dpia(server.Today.AddDays(5)));
        await using var scope = server.Services.CreateAsyncScope();
        var sdf = scope.ServiceProvider.GetRequiredService<SdfService>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        (await sdf.RemindAsync(notifications, Ct)).ShouldBeGreaterThanOrEqualTo(1);
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Set<Dpia>().AsNoTracking().SingleAsync(d => d.Id == dpia.Id, Ct)).RemindedDaysBefore.ShouldBe(7);
        await sdf.RemindAsync(notifications, Ct);
        (await db.Set<Dpia>().AsNoTracking().SingleAsync(d => d.Id == dpia.Id, Ct)).RemindedDaysBefore.ShouldBe(7, "the 7-day reminder is sent once");
        (await db.Set<UserNotification>().AsNoTracking().CountAsync(n => n.Title.Contains(dpia.Number), Ct)).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task A_significant_data_fiduciary_names_a_dpo_based_in_india_who_is_on_the_privacy_notice()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var current = await admin.GetAsync<SdfSettings>("/api/v1/privacy/sdf/settings");
        using (var noDpo = await admin.SendRawAsync(HttpMethod.Put, "/api/v1/privacy/sdf/settings",
                   new SdfSettings(true, server.Today, new DpoContact("Dr Meena DPO", null, "dpo@hospital.example.in", "9" + Digits(9), null, BasedInIndia: false), current.Version)))
        {
            noDpo.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the DPO must be based in India");
        }

        await admin.PutAsync("/api/v1/privacy/sdf/settings",
            new SdfSettings(true, server.Today, new DpoContact("Dr Meena DPO", "Data Protection Officer", "dpo@hospital.example.in", "9" + Digits(9), "Chennai", true), current.Version));
        var notice = await server.Anonymous().GetAsync<PublicPrivacyInfo>("/api/v1/public/privacy");
        notice.Dpo.ShouldNotBeNull().Name.ShouldBe("Dr Meena DPO");
        notice.BackupWindowDays.ShouldBe(ErasureService.BackupWindowDays(30));

        var audit = await admin.PostAsync<DataAuditDto>("/api/v1/privacy/sdf/audits", new SaveDataAuditRequest(
            server.Today.AddYears(-1), server.Today.AddDays(-1), "Independent Auditors LLP", "All processing of patients' data", null, null, server.Today, null, null, null));
        audit.NextDue.ShouldBe(server.Today.AddDays(-1).AddMonths(12), "the next audit is due twelve months after this one's period");
    }

    // ------------------------------------------------------------------ erasure and backups (s.8(7), s.12(3))

    [Fact]
    public async Task Every_erasure_is_written_to_the_sealed_ledger_and_the_answer_says_how_long_backups_keep_it()
    {
        var (patient, phone, requestId) = await ErasureRequestWithNomineeAsync();
        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var outcome = await officer.PostAsync<ErasureOutcome>($"/api/v1/privacy/requests/{requestId}/erasure", new EraseDataRequest([ErasureService.NomineeItem]));
        outcome.Summary.ShouldContain($"within {ErasureService.BackupWindowDays(30)} days");

        var ledger = server.Services.GetRequiredService<ErasureLedger>();
        var entry = ledger.Read().Entries.Where(e => e.RequestId == requestId).ShouldHaveSingleItem();
        entry.PatientId.ShouldBe(patient.Id);
        entry.Items.ShouldBe([ErasureService.NomineeItem]);
        ledger.Verifies(entry).ShouldBeTrue();
        File.ReadAllText(ledger.Path).ShouldNotContain(phone, Case.Sensitive, "the ledger holds ids, never personal data");

        // A line someone wrote without the audit seal key is never trusted.
        ledger.Verifies(entry with { PatientId = Guid.NewGuid() }).ShouldBeFalse();
    }

    [Fact]
    public async Task A_forged_ledger_line_is_not_replayed()
    {
        var (patient, phone) = await RegisterPatientAsync(adult: true);
        var portal = await server.SignInToPortalAsync(phone);
        await portal.PutAsync("/api/v1/portal/privacy/nominee", new PortalNomineeRequest(patient.Id, "Ravi Rao", "Husband", "98" + Digits(8), null));

        var ledger = server.Services.GetRequiredService<ErasureLedger>();
        var forged = new ErasureLedgerEntry(Guid.NewGuid(), Guid.NewGuid(), "FORGED", patient.Id, [ErasureService.NomineeItem], DateTimeOffset.UtcNow, Mac: new string('A', 64));
        await File.AppendAllTextAsync(ledger.Path, JsonSerializer.Serialize(forged, ApiClient.Json) + Environment.NewLine, Ct);

        await using var scope = server.Services.CreateAsyncScope();
        var outcome = (await scope.ServiceProvider.GetRequiredService<ErasureLedgerReplay>().RunAsync(Ct)).ShouldNotBeNull();
        outcome.Rejected.ShouldBeGreaterThanOrEqualTo(1);
        outcome.Reapplied.ShouldNotContain(r => r.PatientId == patient.Id);
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Nominee>().AnyAsync(n => n.PatientId == patient.Id, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_erasure_missing_from_the_database_is_carried_out_again_once()
    {
        // As after a restore: the ledger has the erasure, the database does not. The restore drill itself (a real backup,
        // pg_restore into a new database) is ErasureRestoreTests.
        var (patient, _, requestId) = await ErasureRequestWithNomineeAsync();
        var ledger = server.Services.GetRequiredService<ErasureLedger>();
        ledger.Append(new ErasureLedgerEntry(Guid.NewGuid(), requestId, "RESTORED", patient.Id, [ErasureService.NomineeItem, ErasureService.Messages], DateTimeOffset.UtcNow));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var outcome = (await scope.ServiceProvider.GetRequiredService<ErasureLedgerReplay>().RunAsync(Ct)).ShouldNotBeNull();
            var replayed = outcome.Reapplied.Where(r => r.RequestId == requestId).ShouldHaveSingleItem();
            replayed.Applied.ShouldBe([ErasureService.NomineeItem, ErasureService.Messages], ignoreOrder: true);
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<Nominee>().AnyAsync(n => n.PatientId == patient.Id, Ct)).ShouldBeFalse();
            (await db.Set<AuditEvent>().AsNoTracking().AnyAsync(a => a.Action == ErasureLedgerReplay.ReappliedAction && a.PatientId == patient.Id, Ct)).ShouldBeTrue();
        }

        await using (var again = server.Services.CreateAsyncScope())
        {
            var second = (await again.ServiceProvider.GetRequiredService<ErasureLedgerReplay>().RunAsync(Ct)).ShouldNotBeNull();
            second.Reapplied.ShouldNotContain(r => r.RequestId == requestId, "an entry is applied once");
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(PatientCreated Patient, string Phone, Guid RequestId)> ErasureRequestWithNomineeAsync()
    {
        var (patient, phone) = await RegisterPatientAsync(adult: true);
        var portal = await server.SignInToPortalAsync(phone);
        await portal.PutAsync("/api/v1/portal/privacy/nominee", new PortalNomineeRequest(patient.Id, "Ravi Rao", "Husband", "98" + Digits(8), null));
        var created = await portal.PostAsync<PortalRequestCreated>("/api/v1/portal/privacy/requests", new PortalDataRequestCreate(
            patient.Id, DataRequestType.Erasure, RequesterRelation.Self, null, "Please erase my nominee.", null));
        return (patient, phone, created.Id);
    }

    private async Task<(ConsentManagerDto Manager, string Secret)> RegisterSimulatorAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var code = "sim-" + Letters(6);
        var created = await admin.PostAsync<ConsentManagerCreated>("/api/v1/privacy/consent-managers",
            new SaveConsentManagerRequest(code, "Consent manager simulator", "DPB-CM-" + Digits(4), null, IsSimulator: true));
        var listed = (await admin.GetAsync<List<ConsentManagerDto>>("/api/v1/privacy/consent-managers")).Single(m => m.Id == created.Id);
        return (listed, created.Secret);
    }

    private static string Granted(string artefactId, string uhid, string? mobile, ConsentType purpose, string[] items, bool givenByGuardian = false) =>
        JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid().ToString("N"),
            kind = ConsentManagerProtocol.Granted,
            artefact = new
            {
                id = artefactId,
                principal = new { uhid, mobile },
                purpose = purpose.ToString(),
                dataItems = items,
                validFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
                validUntil = DateTimeOffset.UtcNow.AddDays(90),
                givenByGuardian,
            },
        });

    /// <summary>A consent manager's call, signed as the protocol says.</summary>
    private async Task<HttpResponseMessage> SendEventAsync(string code, string secret, string json, DateTimeOffset? at = null)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/consent-managers/{code}/events")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        request.Headers.Add(ConsentManagerProtocol.TimestampHeader, timestamp);
        request.Headers.Add(ConsentManagerProtocol.SignatureHeader, ConsentManagerProtocol.Sign(secret, timestamp, body));
        return await server.Anonymous(ownAddress: true).Http.SendAsync(request, Ct);
    }

    // Record sharing is limited to the next month: sharing the whole record with no signed form needs a witness (slice 14),
    // which these tests are not about.
    private CaptureConsentRequest Consent(ConsentType type) => new(type, null, true, null, null, null, null, null,
        type == ConsentType.DataSharing ? new RecordSharingScope(null, null, null, null, null, server.Today.AddMonths(1)) : null);

    private async Task<List<PatientConsent>> ConsentsAsync(Guid patientId, ConsentType type)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PatientConsent>().AsNoTracking()
            .Where(c => c.PatientId == patientId && c.Type == type).ToListAsync(Ct);
    }

    private async Task<string> CodeSentToAsync(string phone, string template)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType).OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync(Ct);
        return messages.Select(m => m.Read<NotificationDelivery>()).First(d => d.To == Phone.Normalize(phone) && d.TemplateKey == template).Values["code"];
    }

    private static SaveDpiaRequest Dpia(DateOnly reviewDue) => new(
        "Lab reports by WhatsApp", "Patients receive their lab reports as PDF documents on WhatsApp.", ["Name", "Mobile number", "Lab results"],
        "Patients, including children", "To give patients their reports quickly.",
        [new DpiaRisk("A report sent to a wrong number", RiskLevel.Medium, RiskLevel.High), new DpiaRisk("The provider keeps copies", RiskLevel.Low, RiskLevel.Medium)],
        [new DpiaMitigation("Numbers are confirmed with a code before use", "IT"), new DpiaMitigation("Data processing agreement with the provider", "Legal")],
        reviewDue);

    private async Task<(PatientCreated Patient, string Phone)> RegisterPatientAsync(bool adult)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var phone = "9" + Digits(9);
        var born = adult ? new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(3650)) : server.Today.AddYears(-9).AddDays(-Random.Shared.Next(700));
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: adult ? "Ms." : "Master", FirstName: adult ? "Devi" : "Kavin", MiddleName: null, LastName: "Dpdp" + Letters(8), Gender: adult ? Gender.Female : Gender.Male,
            DateOfBirth: born, AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: adult ? null : "Lakshmi Parent", GuardianRelation: adult ? null : GuardianRelation.SonOf, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return (patient, phone);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
