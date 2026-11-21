using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The blood bank's second officer (maker-checker, MAKER-CHECKER.md slice 3), as in the production-style run: a donation's
/// screening results release nothing until another officer verifies them; a unit does not go out on one officer's grouping
/// and cross-match; a patient grouped otherwise than on an earlier request is not given blood until another officer groups
/// a new sample; a patient with no group on record is not given group-specific blood on one sample (the two-sample rule)
/// until another officer groups a second, separately drawn sample that agrees, unless the doctor overrides it with a reason;
/// and uncross-matched blood is released on the treating doctor's own sign-in, not on a name the blood bank types. A blood
/// bank with one officer on duty switches the second officer off (a second administrator approving), and what that officer
/// releases alone is listed for the medical officer's review.
/// </summary>
public sealed class BloodBankVerificationTests(HospitalServer server)
{
    private const string Bank = "/api/v1/blood-bank";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Units_stay_in_quarantine_until_an_officer_other_than_the_one_who_entered_the_results_verifies_them()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var (donationId, unitId) = await CollectAndEnterAsync(santosh, BloodGroup.APositive, BloodComponentType.PRBC);
        var patient = await RegisterPatientAsync();
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Routine);
        await santosh.PostAsync($"{Bank}/requests/{request}/grouping", HospitalServer.BloodSample(BloodGroup.APositive));
        await server.ConfirmGroupOnSecondSampleAsync(request, BloodGroup.APositive);

        // Entered, all five markers non-reactive: the unit stays in quarantine, awaiting verification, and is not offered,
        // reserved or issued.
        var entered = await santosh.GetAsync<DonationDetail>($"{Bank}/donations/{donationId}");
        (entered.AwaitingVerification, entered.LabResultsByName, entered.VerifiedAt, entered.SeparateVerifier)
            .ShouldBe((true, "Santosh Gaikwad", (DateTimeOffset?)null, true));
        entered.Components.Single().Status.ShouldBe(BloodUnitStatus.Quarantine);
        entered.Components.Single().AwaitingVerification.ShouldBeTrue();
        (await santosh.GetAsync<List<CompatibleUnitDto>>($"{Bank}/requests/{request}/compatible-units")).ShouldNotContain(u => u.Id == unitId);
        (await RefusalAsync(priya, $"{Bank}/requests/{request}/crossmatches",
            new RecordCrossMatchRequest(unitId, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null))).ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.unit_unavailable"));
        var awaiting = $"{Bank}/donations?awaitingVerification=true&search={entered.BagNumber}";
        (await priya.GetAsync<PagedResult<DonationListItem>>(awaiting)).Items.Single().AwaitingVerification.ShouldBeTrue();

        // The officer who entered them may not verify them, nor may anyone else correct them before they are verified.
        (await RefusalAsync(santosh, $"{Bank}/donations/{donationId}/lab-results/verify", new VerifyLabResultsRequest(entered.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "bloodbank.verify_own_results"));
        (await RefusalAsync(priya, $"{Bank}/donations/{donationId}/lab-results", Clear(BloodGroup.ANegative, entered.Version), HttpMethod.Put))
            .ShouldBe((HttpStatusCode.Forbidden, "bloodbank.results_entered_by_other"));

        // Another officer verifies them: the unit is in stock, and the audit trail names both officers.
        await priya.PostAsync($"{Bank}/donations/{donationId}/lab-results/verify", new VerifyLabResultsRequest(entered.Version));
        var verified = await priya.GetAsync<DonationDetail>($"{Bank}/donations/{donationId}");
        (verified.AwaitingVerification, verified.VerifiedByName, verified.VerifiedByEnterer).ShouldBe((false, "Priya Nair", false));
        verified.Components.Single().Status.ShouldBe(BloodUnitStatus.Available);
        (await priya.GetAsync<PagedResult<DonationListItem>>(awaiting)).Items.ShouldBeEmpty();
        var audit = (await AuditAsync(nameof(Donation), donationId)).Single(e => e.Action == "bloodbank.results_verified");
        audit.Summary.ShouldNotBeNull().ShouldContain("entered by Santosh Gaikwad, verified by Priya Nair");
        (audit.Severity, audit.UserName).ShouldBe((AuditSeverity.Info, HospitalServer.Users.SecondBloodBankOfficer));

        // Verified results are final, and verifying twice changes nothing.
        (await RefusalAsync(santosh, $"{Bank}/donations/{donationId}/lab-results", Clear(BloodGroup.ANegative, verified.Version), HttpMethod.Put))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.results_final"));
        (await RefusalAsync(priya, $"{Bank}/donations/{donationId}/lab-results/verify", new VerifyLabResultsRequest(verified.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.already_verified"));

        // Now issuable: Santosh grouped the patient's first sample and Priya the second; Priya cross-matches, and Santosh issues.
        await server.CrossMatchAsSecondOfficerAsync(request, unitId);
        var issue = await santosh.PostAsync<BloodIssueDto>($"{Bank}/requests/{request}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha"));
        (issue.IssuedByName, issue.IssuedOnOwnChecks).ShouldBe(("Santosh Gaikwad", false));
    }

    [Fact]
    public async Task A_unit_is_not_issued_by_the_officer_who_both_grouped_the_patient_and_cross_matched_it()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var (donationId, unitId) = await CollectAndEnterAsync(santosh, BloodGroup.OPositive, BloodComponentType.PRBC);
        await server.VerifyDonationAsync(donationId);
        var patient = await RegisterPatientAsync();
        await GroupedBeforeAsync(santosh, patient.Id, BloodGroup.OPositive);
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Urgent);

        // The request shows who grouped and who cross-matched, by account. The patient's group is on record from an earlier
        // request, so this sample needs no second one.
        await santosh.PostAsync($"{Bank}/requests/{request}/grouping", HospitalServer.BloodSample(BloodGroup.OPositive, drawnMinutesAgo: 30));
        await santosh.PostAsync<CrossMatchDto>($"{Bank}/requests/{request}/crossmatches",
            new RecordCrossMatchRequest(unitId, CrossmatchMethod.AntiHumanGlobulin, CrossmatchResult.Compatible, null));
        var santoshId = (await santosh.GetAsync<AuthUser>("/api/v1/auth/me")).Id;
        var detail = await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{request}");
        (detail.SecondSampleRequired, detail.AwaitingSecondSample, detail.GroupingHeld).ShouldBe((false, false, false));
        (detail.GroupedByUserId, detail.GroupedByName).ShouldBe(((Guid?)santoshId, "Santosh Gaikwad"));
        (detail.CrossMatches.Single().PerformedByUserId, detail.CrossMatches.Single().PerformedByName).ShouldBe(((Guid?)santoshId, "Santosh Gaikwad"));

        // Grouped, cross-matched and issued by one officer: refused, and the unit stays reserved for the patient.
        (await RefusalAsync(santosh, $"{Bank}/requests/{request}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha")))
            .ShouldBe((HttpStatusCode.Forbidden, IssueSeparation.OwnChecks));
        (await santosh.GetAsync<BloodUnitDto>($"{Bank}/units/by-number/{detail.CrossMatches.Single().UnitNumber}")).Status.ShouldBe(BloodUnitStatus.Reserved);

        // Another officer issues it.
        var issue = await priya.PostAsync<BloodIssueDto>($"{Bank}/requests/{request}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha"));
        (issue.IssuedByName, issue.IssuedOnOwnChecks).ShouldBe(("Priya Nair", false));
    }

    [Fact]
    public async Task With_one_officer_on_duty_the_officer_verifies_and_issues_alone_and_both_are_listed_for_the_medical_officers_review()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        await using var oneOfficer = await SecondOfficerOffAsync();
        var (donationId, unitId) = await CollectAndEnterAsync(santosh, BloodGroup.BPositive, BloodComponentType.PRBC);

        // The night shift's one officer verifies their own results: released, flagged and listed for review, at Warning.
        var entered = await santosh.GetAsync<DonationDetail>($"{Bank}/donations/{donationId}");
        entered.SeparateVerifier.ShouldBeFalse();
        await santosh.PostAsync($"{Bank}/donations/{donationId}/lab-results/verify", new VerifyLabResultsRequest(entered.Version));
        var verified = await santosh.GetAsync<DonationDetail>($"{Bank}/donations/{donationId}");
        (verified.VerifiedByName, verified.VerifiedByEnterer, verified.Components.Single().Status).ShouldBe(("Santosh Gaikwad", true, BloodUnitStatus.Available));
        (await santosh.GetAsync<PagedResult<DonationListItem>>($"{Bank}/donations?oneOfficer=true&search={verified.BagNumber}")).Items
            .Single().VerifiedByEnterer.ShouldBeTrue();
        (await AuditAsync(nameof(Donation), donationId)).ShouldContain(e => e.Action == "bloodbank.results_verified" && e.Severity == AuditSeverity.Warning
                                                                         && e.Summary!.Contains("for the medical officer's review"));

        // The same officer groups, cross-matches and issues: allowed, flagged on the issue, listed and audited at Warning. The
        // patient has no group on record, so the officer groups a second, separately drawn sample too, audited at Warning.
        var patient = await RegisterPatientAsync();
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Urgent);
        await santosh.PostAsync($"{Bank}/requests/{request}/grouping", HospitalServer.BloodSample(BloodGroup.BPositive));
        await santosh.PostAsync($"{Bank}/requests/{request}/grouping",
            HospitalServer.BloodSample(BloodGroup.BPositive, drawnMinutesAgo: 10, drawnBy: "Sr. Leela Thomas", newSample: true));
        (await AuditAsync(nameof(BloodRequest), request)).ShouldContain(e => e.Action == "bloodbank.group_confirmed" && e.Severity == AuditSeverity.Warning
                                                                           && e.Summary!.Contains("one officer grouped both"));
        await santosh.PostAsync<CrossMatchDto>($"{Bank}/requests/{request}/crossmatches",
            new RecordCrossMatchRequest(unitId, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null));
        var issue = await santosh.PostAsync<BloodIssueDto>($"{Bank}/requests/{request}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha"));
        issue.IssuedOnOwnChecks.ShouldBeTrue();
        (await santosh.GetAsync<PagedResult<BloodRequestListItem>>($"{Bank}/requests?oneOfficer=true&search={patient.Uhid}")).Items
            .ShouldContain(r => r.Id == request);
        (await AuditAsync(nameof(BloodIssue), issue.Id)).ShouldContain(e => e.Action == "bloodbank.issued_on_own_checks" && e.Severity == AuditSeverity.Warning);
    }

    [Fact]
    public async Task A_patient_grouped_otherwise_than_on_an_earlier_request_gets_no_blood_until_another_officer_groups_a_new_sample()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var (donationId, unitId) = await CollectAndEnterAsync(santosh, BloodGroup.APositive, BloodComponentType.PRBC);
        await server.VerifyDonationAsync(donationId);
        var patient = await RegisterPatientAsync();

        // Grouped A+ on an earlier request.
        var earlier = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Routine);
        await santosh.PostAsync($"{Bank}/requests/{earlier}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 120));

        // Now the sample groups B+: recorded, but the request is held, at Warning, and nothing is cross-matched or issued on it.
        // The disagreement is the hold; the request does not wait for a second sample as well.
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Urgent);
        await santosh.PostAsync($"{Bank}/requests/{request}/grouping", HospitalServer.BloodSample(BloodGroup.BPositive));
        var held = await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{request}");
        (held.GroupingHeld, held.PatientBloodGroup, held.EarlierGroup).ShouldBe((true, BloodGroup.BPositive, (BloodGroup?)BloodGroup.APositive));
        (held.SecondSampleRequired, held.AwaitingSecondSample).ShouldBe((false, false));
        (await AuditAsync(nameof(BloodRequest), request)).ShouldContain(e => e.Action == "bloodbank.grouping_history_discrepancy" && e.Severity == AuditSeverity.Warning);
        (await RefusalAsync(priya, $"{Bank}/requests/{request}/crossmatches",
            new RecordCrossMatchRequest(unitId, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null))).ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_history_discrepancy"));
        (await RefusalAsync(priya, $"{Bank}/requests/{request}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_history_discrepancy"));

        // The same sample grouped again A+ is still the same-request discrepancy; the way out is a new sample, grouped by
        // another officer than the one who grouped the first.
        var newSample = HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 10, drawnBy: "Sr. Leela Thomas", newSample: true);
        (await RefusalAsync(priya, $"{Bank}/requests/{request}/grouping", HospitalServer.BloodSample(BloodGroup.APositive)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await RefusalAsync(santosh, $"{Bank}/requests/{request}/grouping", newSample))
            .ShouldBe((HttpStatusCode.Forbidden, "bloodbank.regroup_own_sample"));
        await priya.PostAsync($"{Bank}/requests/{request}/grouping", newSample);
        var regrouped = await priya.GetAsync<BloodRequestDetail>($"{Bank}/requests/{request}");
        (regrouped.GroupingHeld, regrouped.PatientBloodGroup, regrouped.FirstSampleGroup, regrouped.GroupedByName)
            .ShouldBe((false, BloodGroup.APositive, (BloodGroup?)BloodGroup.BPositive, "Priya Nair"));

        // Priya grouped the new sample and cross-matches; Santosh issues.
        await priya.PostAsync<CrossMatchDto>($"{Bank}/requests/{request}/crossmatches",
            new RecordCrossMatchRequest(unitId, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null));
        var issue = await santosh.PostAsync<BloodIssueDto>($"{Bank}/requests/{request}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha"));
        issue.UnitBloodGroup.ShouldBe(BloodGroup.APositive);
    }

    [Fact]
    public async Task Uncross_matched_blood_goes_out_only_on_the_release_the_doctor_authorises_from_their_own_sign_in()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (donationId, unitId) = await CollectAndEnterAsync(santosh, BloodGroup.ONegative, BloodComponentType.PRBC);
        await server.VerifyDonationAsync(donationId);
        var patient = await RegisterPatientAsync();
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Emergency);
        const string reason = "Haemorrhagic shock after a road accident, BP 70/40, no time to group and cross-match";

        // Not on the blood bank's word (it may ask for blood but is no doctor), nor a nurse's; and nothing goes uncross-matched before.
        (await RefusalAsync(santosh, $"{Bank}/requests/{request}/emergency-release", new EmergencyReleaseRequest(reason)))
            .ShouldBe((HttpStatusCode.Forbidden, "bloodbank.doctor_required"));
        (await RefusalAsync(nurse, $"{Bank}/requests/{request}/emergency-release", new EmergencyReleaseRequest(reason)))
            .ShouldBe((HttpStatusCode.Forbidden, "bloodbank.doctor_required"));
        (await RefusalAsync(santosh, $"{Bank}/requests/{request}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.crossmatch_required"));

        // The doctor authorises it from their own sign-in: their account and practitioner record are on the request.
        await doctor.PostAsync($"{Bank}/requests/{request}/emergency-release", new EmergencyReleaseRequest(reason));
        var me = await doctor.GetAsync<AuthUser>("/api/v1/auth/me");
        var authorised = await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{request}");
        (authorised.EmergencyReleaseByUserId, authorised.EmergencyReleaseAuthorisedBy, authorised.EmergencyReleaseReason)
            .ShouldBe(((Guid?)me.Id, await PractitionerNameAsync(me.PractitionerId!.Value), reason));
        authorised.EmergencyReleaseAt.ShouldNotBeNull();

        // Then the blood bank issues O Rh(D)-negative red cells uncross-matched, and the audit trail names both people.
        var issue = await santosh.PostAsync<BloodIssueDto>($"{Bank}/requests/{request}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha"));
        (issue.IsEmergencyRelease, issue.IssuedOnOwnChecks).ShouldBe((true, false));
        (await AuditAsync(nameof(BloodRequest), request)).Single(e => e.Action == "bloodbank.emergency_release_authorised")
            .ShouldSatisfyAllConditions(
                e => e.UserName.ShouldBe(HospitalServer.Users.Doctor),
                e => e.Summary.ShouldNotBeNull().ShouldContain($"authorised by {authorised.EmergencyReleaseAuthorisedBy}"));
        (await AuditAsync(nameof(BloodIssue), issue.Id)).Single(e => e.Action == "bloodbank.emergency_release").Summary.ShouldNotBeNull()
            .ShouldContain($"by Santosh Gaikwad, on the release {authorised.EmergencyReleaseAuthorisedBy} authorised");
    }

    [Fact]
    public async Task A_patient_never_grouped_before_gets_group_specific_blood_only_once_a_second_separately_drawn_sample_agrees()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (donationId, unitId) = await CollectAndEnterAsync(santosh, BloodGroup.APositive, BloodComponentType.PRBC);
        await server.VerifyDonationAsync(donationId);
        var patient = await RegisterPatientAsync();
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Routine);
        var path = $"{Bank}/requests/{request}";

        // The first sample, drawn by the ward nurse 45 minutes ago, groups A+: recorded with its draw, apart from when the blood
        // bank received it, and the request waits for a second sample.
        var first = HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 45, drawnBy: "Sr. Kavya Menon");
        await santosh.PostAsync($"{path}/grouping", first);
        var waiting = await santosh.GetAsync<BloodRequestDetail>(path);
        (waiting.SecondSampleRequired, waiting.AwaitingSecondSample, waiting.GroupingHeld, waiting.PatientBloodGroup)
            .ShouldBe((true, true, true, BloodGroup.APositive));
        (waiting.SampleDrawnAt, waiting.SampleDrawnBy).ShouldBe((first.SampleDrawnAt, "Sr. Kavya Menon"));
        waiting.SampleReceivedAt.ShouldNotBeNull().ShouldBeGreaterThan(first.SampleDrawnAt!.Value);
        (await santosh.GetAsync<PagedResult<BloodRequestListItem>>($"{Bank}/requests?search={patient.Uhid}")).Items.Single(r => r.Id == request)
            .AwaitingSecondSample.ShouldBeTrue();
        (await AuditAsync(nameof(BloodRequest), request)).ShouldContain(e => e.Action == "bloodbank.second_sample_required" && e.Summary!.Contains("Sr. Kavya Menon"));

        // Nothing group-specific goes on the one sample: no units offered, no cross-match, no issue.
        (await RefusalAsync(priya, $"{path}/compatible-units", null, HttpMethod.Get)).ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.second_sample_required"));
        (await RefusalAsync(priya, $"{path}/crossmatches", new RecordCrossMatchRequest(unitId, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.second_sample_required"));
        (await RefusalAsync(priya, $"{path}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.second_sample_required"));

        // On a routine request nothing clinical stops the blood bank waiting for the second sample: the doctor's override of the
        // rule is the emergency one, refused here.
        (await RefusalAsync(doctor, $"{path}/single-sample-override", new SingleSampleOverrideRequest("Symptomatic anaemia, theatre list tomorrow")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.not_emergency"));

        // The first sample grouped again, not as a new one, confirms nothing: refused, pointing to a new sample.
        (await RefusalAsync(priya, $"{path}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 10, drawnBy: "Sr. Leela Thomas")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.group_as_new_sample"));

        // The second sample says when it was drawn and by whom; it is not grouped by Santosh, who grouped the first, nor is it
        // the first tube again.
        var second = HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 10, drawnBy: "Sr. Leela Thomas", newSample: true);
        (await RefusalAsync(priya, $"{path}/grouping", second with { SampleDrawnBy = null })).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await RefusalAsync(santosh, $"{path}/grouping", second)).ShouldBe((HttpStatusCode.Forbidden, "bloodbank.regroup_own_sample"));
        (await RefusalAsync(priya, $"{path}/grouping", first with { NewSample = true })).ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.sample_not_separate"));
        (await priya.GetAsync<BloodRequestDetail>(path)).ShouldSatisfyAllConditions(
            d => d.AwaitingSecondSample.ShouldBeTrue(), d => d.DisagreeingSampleGroup.ShouldBeNull());

        // Priya groups the second sample A+, as the first: the group is confirmed, and the first sample's draw is kept.
        await priya.PostAsync($"{path}/grouping", second);
        var confirmed = await priya.GetAsync<BloodRequestDetail>(path);
        (confirmed.AwaitingSecondSample, confirmed.GroupingHeld, confirmed.PatientBloodGroup, confirmed.GroupedByName)
            .ShouldBe((false, false, BloodGroup.APositive, "Priya Nair"));
        (confirmed.SampleDrawnAt, confirmed.SampleDrawnBy, confirmed.FirstSampleDrawnAt, confirmed.FirstSampleDrawnBy)
            .ShouldBe((second.SampleDrawnAt, "Sr. Leela Thomas", first.SampleDrawnAt, "Sr. Kavya Menon"));
        (await AuditAsync(nameof(BloodRequest), request)).ShouldContain(e => e.Action == "bloodbank.group_confirmed" && e.Severity == AuditSeverity.Info);

        // Santosh cross-matches, and Priya, who grouped the sample the request now goes on, issues.
        await santosh.PostAsync<CrossMatchDto>($"{path}/crossmatches", new RecordCrossMatchRequest(unitId, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null));
        var issue = await priya.PostAsync<BloodIssueDto>($"{path}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha"));
        (issue.UnitBloodGroup, issue.IsEmergencyRelease, issue.IssuedOnOwnChecks).ShouldBe((BloodGroup.APositive, false, false));

        // The patient's group is now on record from two samples: a later request grouped on the second sample's tube again
        // needs no further sample, the first sample having been drawn at another time.
        var later = await AskAsync(patient.Id, BloodComponentType.FFP, BloodRequestUrgency.Routine);
        await santosh.PostAsync($"{Bank}/requests/{later}/grouping", second with { NewSample = false });
        (await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{later}")).ShouldSatisfyAllConditions(
            d => d.AwaitingSecondSample.ShouldBeFalse(), d => d.GroupingHeld.ShouldBeFalse(), d => d.PatientBloodGroup.ShouldBe(BloodGroup.APositive));
    }

    [Fact]
    public async Task In_an_emergency_the_release_is_not_slowed_and_group_specific_blood_on_one_sample_is_the_doctors_override()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (oNegativeDonation, oNegative) = await CollectAndEnterAsync(santosh, BloodGroup.ONegative, BloodComponentType.PRBC);
        var (aPositiveDonation, aPositive) = await CollectAndEnterAsync(santosh, BloodGroup.APositive, BloodComponentType.PRBC);
        var (anotherDonation, anotherAPositive) = await CollectAndEnterAsync(santosh, BloodGroup.APositive, BloodComponentType.PRBC);
        await server.VerifyDonationAsync(oNegativeDonation);
        await server.VerifyDonationAsync(aPositiveDonation);
        await server.VerifyDonationAsync(anotherDonation);
        var patient = await RegisterPatientAsync();
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Emergency, units: 3);
        var path = $"{Bank}/requests/{request}";
        await server.AuthoriseEmergencyReleaseAsync(request, "Haemorrhagic shock after a road accident, BP 70/40");
        await santosh.PostAsync($"{path}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 15, drawnBy: "Sr. Kavya Menon"));
        (await santosh.GetAsync<BloodRequestDetail>(path)).AwaitingSecondSample.ShouldBeTrue();

        // The emergency release is not slowed: O Rh(D)-negative red cells go uncross-matched while the second sample is awaited.
        // Group-specific A+ waits: not cross-matched, and not issued as an uncross-matched release.
        (await priya.PostAsync<BloodIssueDto>($"{path}/issues", new IssueBloodRequest(oNegative, "Staff nurse Asha"))).IsEmergencyRelease.ShouldBeTrue();
        (await RefusalAsync(priya, $"{path}/crossmatches", new RecordCrossMatchRequest(aPositive, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.second_sample_required"));
        (await RefusalAsync(priya, $"{path}/issues", new IssueBloodRequest(aPositive, "Staff nurse Asha")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.crossmatch_required"));

        // Group-specific blood on the one sample is the treating doctor's override, from their own sign-in, with a clinical
        // reason of at least 10 characters: not the blood bank's, nor a nurse's.
        const string why = "O-negative stock used up, ongoing haemorrhage, Hb 5.1 g/dl";
        (await RefusalAsync(santosh, $"{path}/single-sample-override", new SingleSampleOverrideRequest(why))).ShouldBe((HttpStatusCode.Forbidden, "bloodbank.doctor_required"));
        (await RefusalAsync(nurse, $"{path}/single-sample-override", new SingleSampleOverrideRequest(why))).ShouldBe((HttpStatusCode.Forbidden, "bloodbank.doctor_required"));
        (await RefusalAsync(doctor, $"{path}/single-sample-override", new SingleSampleOverrideRequest("Bleeding"))).Status.ShouldBe(HttpStatusCode.BadRequest);
        await doctor.PostAsync($"{path}/single-sample-override", new SingleSampleOverrideRequest(why));
        var me = await doctor.GetAsync<AuthUser>("/api/v1/auth/me");
        var overridden = await santosh.GetAsync<BloodRequestDetail>(path);
        (overridden.SingleSampleOverrideByUserId, overridden.SingleSampleOverrideBy, overridden.SingleSampleOverrideReason)
            .ShouldBe(((Guid?)me.Id, await PractitionerNameAsync(me.PractitionerId!.Value), why));
        (overridden.GroupingHeld, overridden.AwaitingSecondSample).ShouldBe((false, true), "the second sample is still grouped when it comes");
        (await AuditAsync(nameof(BloodRequest), request)).Single(e => e.Action == "bloodbank.single_sample_override")
            .ShouldSatisfyAllConditions(
                e => e.Severity.ShouldBe(AuditSeverity.Critical),
                e => e.UserName.ShouldBe(HospitalServer.Users.Doctor),
                e => e.Summary.ShouldNotBeNull().ShouldContain(why));

        // Then A+ is cross-matched and issued on the one sample, and the unit is audited at Critical too. A second A+ unit is
        // cross-matched for later.
        await priya.PostAsync<CrossMatchDto>($"{path}/crossmatches", new RecordCrossMatchRequest(aPositive, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null));
        await priya.PostAsync<CrossMatchDto>($"{path}/crossmatches", new RecordCrossMatchRequest(anotherAPositive, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null));
        var issue = await santosh.PostAsync<BloodIssueDto>($"{path}/issues", new IssueBloodRequest(aPositive, "Staff nurse Asha"));
        (issue.UnitBloodGroup, issue.IsEmergencyRelease).ShouldBe((BloodGroup.APositive, false));
        (await AuditAsync(nameof(BloodIssue), issue.Id)).Single(e => e.Action == "bloodbank.single_sample_issue")
            .ShouldSatisfyAllConditions(e => e.Severity.ShouldBe(AuditSeverity.Critical), e => e.Summary.ShouldNotBeNull().ShouldContain(why));

        // The second sample, drawn by another nurse, groups B+: one of the two tubes holds another patient's blood. Refused as
        // the discrepancy it is, and recorded: the override no longer goes, so the A+ unit already cross-matched on the first
        // sample does not go out, nothing more is cross-matched, and no doctor authorises blood on either sample again.
        (await RefusalAsync(priya, $"{path}/grouping",
                HospitalServer.BloodSample(BloodGroup.BPositive, drawnMinutesAgo: 5, drawnBy: "Sr. Leela Thomas", newSample: true)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await santosh.GetAsync<BloodRequestDetail>(path)).ShouldSatisfyAllConditions(
            d => d.DisagreeingSampleGroup.ShouldBe((BloodGroup?)BloodGroup.BPositive),
            d => d.DisagreeingSampleAt.ShouldNotBeNull(),
            d => (d.DisagreeingSampleDrawnBy, d.DisagreeingSampleRequestNumber).ShouldBe(("Sr. Leela Thomas", (string?)null)),
            d => d.DisagreeingSampleDrawnAt.ShouldNotBeNull(),
            d => d.UnitsReserved.ShouldBe(0, "the A+ unit cross-matched on the first sample went back to stock"),
            d => (d.GroupingHeld, d.AwaitingSecondSample, d.PatientBloodGroup).ShouldBe((true, true, BloodGroup.APositive)));
        (await UnitStatusAsync(anotherAPositive)).ShouldBe(BloodUnitStatus.Available);
        (await AuditAsync(nameof(BloodRequest), request)).Single(e => e.Action == "bloodbank.second_sample_discrepancy")
            .ShouldSatisfyAllConditions(
                e => e.Severity.ShouldBe(AuditSeverity.Critical),
                e => e.Summary.ShouldNotBeNull().ShouldContain("possible wrong blood in tube"),
                e => e.Summary.ShouldNotBeNull().ShouldContain("no longer goes"));
        (await RefusalAsync(santosh, $"{path}/issues", new IssueBloodRequest(anotherAPositive, "Staff nurse Asha")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.crossmatch_required"));
        (await RefusalAsync(priya, $"{path}/crossmatches", new RecordCrossMatchRequest(anotherAPositive, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await RefusalAsync(doctor, $"{path}/single-sample-override", new SingleSampleOverrideRequest(why)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
    }

    [Fact]
    public async Task Where_the_transfusion_committee_has_switched_the_two_sample_rule_off_one_sample_is_enough()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var (donationId, unitId) = await CollectAndEnterAsync(santosh, BloodGroup.BPositive, BloodComponentType.PRBC);
        await server.VerifyDonationAsync(donationId);
        await using var oneSample = await TwoSampleRuleOffAsync();
        var patient = await RegisterPatientAsync();
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Routine);

        await santosh.PostAsync($"{Bank}/requests/{request}/grouping", HospitalServer.BloodSample(BloodGroup.BPositive));
        var grouped = await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{request}");
        (grouped.SecondSampleRequired, grouped.AwaitingSecondSample, grouped.GroupingHeld).ShouldBe((false, false, false));
        (await server.CrossMatchAsSecondOfficerAsync(request, unitId)).Result.ShouldBe(CrossmatchResult.Compatible);
    }

    [Fact]
    public async Task A_sample_grouped_for_another_request_that_disagrees_holds_the_request_resting_on_one_sample_and_ends_its_override()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (donationId, aPositive) = await CollectAndEnterAsync(santosh, BloodGroup.APositive, BloodComponentType.PRBC);
        await server.VerifyDonationAsync(donationId);
        var patient = await RegisterPatientAsync();
        var prbc = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Emergency);
        var ffp = await AskAsync(patient.Id, BloodComponentType.FFP, BloodRequestUrgency.Emergency);
        var prbcPath = $"{Bank}/requests/{prbc}";

        // The PRBC request's one sample groups A+; the doctor authorises blood on it, and an A+ unit is cross-matched and reserved.
        await santosh.PostAsync($"{prbcPath}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 60, drawnBy: "Sr. Kavya Menon"));
        const string why = "O-negative stock used up, ongoing haemorrhage, Hb 5.1 g/dl";
        await doctor.PostAsync($"{prbcPath}/single-sample-override", new SingleSampleOverrideRequest(why));
        await priya.PostAsync<CrossMatchDto>($"{prbcPath}/crossmatches", new RecordCrossMatchRequest(aPositive, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null));
        (await santosh.GetAsync<BloodRequestDetail>(prbcPath)).ShouldSatisfyAllConditions(d => d.GroupingHeld.ShouldBeFalse(), d => d.UnitsReserved.ShouldBe(1));

        // The FFP request's own sample, drawn separately 40 minutes later by another nurse, groups O+: one of the two tubes holds
        // another patient's blood. The FFP request is held against the A+ on record, and the PRBC request is held too.
        var ffpSample = HospitalServer.BloodSample(BloodGroup.OPositive, drawnMinutesAgo: 20, drawnBy: "Sr. Leela Thomas");
        await priya.PostAsync($"{Bank}/requests/{ffp}/grouping", ffpSample);
        var ffpDetail = await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{ffp}");
        (ffpDetail.GroupingHeld, ffpDetail.EarlierGroup).ShouldBe((true, (BloodGroup?)BloodGroup.APositive));
        (await santosh.GetAsync<BloodRequestDetail>(prbcPath)).ShouldSatisfyAllConditions(
            d => (d.GroupingHeld, d.AwaitingSecondSample, d.PatientBloodGroup).ShouldBe((true, true, BloodGroup.APositive)),
            d => (d.DisagreeingSampleGroup, d.DisagreeingSampleRequestNumber, d.DisagreeingSampleDrawnBy)
                .ShouldBe(((BloodGroup?)BloodGroup.OPositive, ffpDetail.RequestNumber, "Sr. Leela Thomas")),
            d => d.DisagreeingSampleDrawnAt.ShouldBe(ffpSample.SampleDrawnAt),
            d => d.UnitsReserved.ShouldBe(0, "the A+ unit cross-matched on the first sample went back to stock"));
        (await UnitStatusAsync(aPositive)).ShouldBe(BloodUnitStatus.Available);
        (await AuditAsync(nameof(BloodRequest), prbc)).Single(e => e.Action == "bloodbank.second_sample_discrepancy").ShouldSatisfyAllConditions(
            e => e.Severity.ShouldBe(AuditSeverity.Critical, "blood had been authorised on the one sample"),
            e => e.Summary.ShouldNotBeNull().ShouldContain(ffpDetail.RequestNumber),
            e => e.Summary.ShouldNotBeNull().ShouldContain("possible wrong blood in tube"),
            e => e.Summary.ShouldNotBeNull().ShouldContain("no longer goes"),
            e => e.Summary.ShouldNotBeNull().ShouldContain("went back to stock"));

        // The blood bank and the requesting doctor are told, critically.
        var link = $"/blood-bank/requests/{prbc}";
        foreach (var told in new[] { await UserIdAsync(santosh), await UserIdAsync(doctor) })
        {
            (await NoticesAsync(told)).ShouldContain(n => n.Title == "Blood samples disagree" && n.Link == link && n.Severity == "critical");
        }

        // Nothing group-specific goes on the PRBC request: no units offered, no cross-match, no issue, no override again.
        (await RefusalAsync(priya, $"{prbcPath}/compatible-units", null, HttpMethod.Get)).ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await RefusalAsync(priya, $"{prbcPath}/crossmatches", new RecordCrossMatchRequest(aPositive, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await RefusalAsync(santosh, $"{prbcPath}/issues", new IssueBloodRequest(aPositive, "Staff nurse Asha")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await RefusalAsync(doctor, $"{prbcPath}/single-sample-override", new SingleSampleOverrideRequest(why)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));

        // Two of three: a further sample on the PRBC request, drawn at another time than both tubes and grouped by another
        // officer than its first, that agrees with its first decides it; the FFP request still waits for its own new sample.
        await priya.PostAsync($"{prbcPath}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 5, drawnBy: "Sr. Asha Mathew", newSample: true));
        (await santosh.GetAsync<BloodRequestDetail>(prbcPath)).ShouldSatisfyAllConditions(
            d => (d.GroupingHeld, d.AwaitingSecondSample).ShouldBe((false, false)),
            d => d.DisagreeingSampleGroup.ShouldBe((BloodGroup?)BloodGroup.OPositive, "the disagreeing sample stays on the record"));
        (await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{ffp}")).GroupingHeld.ShouldBeTrue();
    }

    [Fact]
    public async Task A_request_held_by_a_sample_grouped_for_another_request_is_refused_the_doctors_override()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await RegisterPatientAsync();
        var prbc = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Emergency);
        var ffp = await AskAsync(patient.Id, BloodComponentType.FFP, BloodRequestUrgency.Emergency);

        // The PRBC request waits for a second sample; before any doctor authorises blood on its one sample, the FFP request's
        // sample groups otherwise. Held, audited at Warning (no blood went on the one sample), and the doctor is refused.
        await santosh.PostAsync($"{Bank}/requests/{prbc}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 60, drawnBy: "Sr. Kavya Menon"));
        await santosh.PostAsync($"{Bank}/requests/{ffp}/grouping", HospitalServer.BloodSample(BloodGroup.BPositive, drawnMinutesAgo: 20, drawnBy: "Sr. Leela Thomas"));
        (await AuditAsync(nameof(BloodRequest), prbc)).Single(e => e.Action == "bloodbank.second_sample_discrepancy").Severity.ShouldBe(AuditSeverity.Warning);
        (await RefusalAsync(doctor, $"{Bank}/requests/{prbc}/single-sample-override", new SingleSampleOverrideRequest("O-negative stock used up, ongoing haemorrhage")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{prbc}")).ShouldSatisfyAllConditions(
            d => d.SingleSampleOverrideBy.ShouldBeNull(), d => d.DisagreeingSampleGroup.ShouldBe((BloodGroup?)BloodGroup.BPositive));

        // A later request of the patient, whose one sample rests on nothing the record holds (both groupings are disputed), is
        // held at once by the samples that disagree with it.
        var later = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Emergency);
        await santosh.PostAsync($"{Bank}/requests/{later}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 10, drawnBy: "Sr. Asha Mathew"));
        (await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{later}")).ShouldSatisfyAllConditions(
            d => (d.AwaitingSecondSample, d.GroupingHeld).ShouldBe((true, true)),
            d => d.DisagreeingSampleGroup.ShouldBe((BloodGroup?)BloodGroup.BPositive));
        (await RefusalAsync(doctor, $"{Bank}/requests/{later}/single-sample-override", new SingleSampleOverrideRequest("O-negative stock used up, ongoing haemorrhage")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
    }

    [Fact]
    public async Task Two_first_groupings_of_a_patient_saved_at_the_same_moment_see_each_other()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await RegisterPatientAsync();
        var prbc = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Emergency);
        var ffp = await AskAsync(patient.Id, BloodComponentType.FFP, BloodRequestUrgency.Emergency);

        // The test holds the patient's grouping lock, as a grouping being saved would: both groupings, sent at once, wait for it.
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var holding = await db.Database.BeginTransactionAsync(Ct);
        var key = PatientGroupingLockKey(patient.Id);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", Ct);
        var first = santosh.SendRawAsync(HttpMethod.Post, $"{Bank}/requests/{prbc}/grouping",
            HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 60, drawnBy: "Sr. Kavya Menon"));
        var second = priya.SendRawAsync(HttpMethod.Post, $"{Bank}/requests/{ffp}/grouping",
            HospitalServer.BloodSample(BloodGroup.OPositive, drawnMinutesAgo: 20, drawnBy: "Sr. Leela Thomas"));
        await WaitForLockWaitersAsync(key, 2);
        await holding.CommitAsync(Ct);
        using (var a = await first)
        {
            a.EnsureSuccessStatusCode();
        }

        using (var b = await second)
        {
            b.EnsureSuccessStatusCode();
        }

        // Whichever went first, the other saw it: one is held against the other's group, the other records its sample as
        // disagreeing. Neither waits for a second sample unheld, and the doctor overrides neither.
        var prbcDetail = await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{prbc}");
        var ffpDetail = await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{ffp}");
        (prbcDetail.GroupingHeld, ffpDetail.GroupingHeld).ShouldBe((true, true));
        (prbcDetail.EarlierGroup ?? prbcDetail.DisagreeingSampleGroup).ShouldBe((BloodGroup?)BloodGroup.OPositive);
        (ffpDetail.EarlierGroup ?? ffpDetail.DisagreeingSampleGroup).ShouldBe((BloodGroup?)BloodGroup.APositive);
        foreach (var request in new[] { prbc, ffp })
        {
            (await RefusalAsync(doctor, $"{Bank}/requests/{request}/single-sample-override", new SingleSampleOverrideRequest("O-negative stock used up, ongoing haemorrhage")))
                .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        // The saved rows say it, not only the detail view (which reads the disagreement live): the request that went first rests
        // on one sample, and holds the other's sample as its disagreeing one, with the audit; the other is held against its group.
        var rows = await db.Set<BloodRequest>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.Id == prbc || r.Id == ffp).ToListAsync(Ct);
        var disputed = rows.Single(r => r.DisagreeingSampleGroup != null);
        var heldAgainst = rows.Single(r => r.Id != disputed.Id);
        (disputed.SecondSampleRequired, disputed.DisagreeingSampleRequestNumber).ShouldBe((true, heldAgainst.RequestNumber));
        (heldAgainst.EarlierGroup, heldAgainst.DisagreeingSampleGroup).ShouldBe((disputed.PatientBloodGroup, (BloodGroup?)null));
        (await AuditAsync(nameof(BloodRequest), disputed.Id)).ShouldContain(e => e.Action == "bloodbank.second_sample_discrepancy" && e.Severity == AuditSeverity.Warning);
        (await AuditAsync(nameof(BloodRequest), heldAgainst.Id)).ShouldContain(e => e.Action == "bloodbank.grouping_history_discrepancy");
    }

    [Fact]
    public async Task A_blood_bank_medical_officer_who_grouped_the_one_sample_and_authorised_blood_on_it_does_not_issue_it()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var medicalOfficer = await server.SignInAsync(HospitalServer.Users.BloodBankMedicalOfficer);
        var (donationId, aPositive) = await CollectAndEnterAsync(santosh, BloodGroup.APositive, BloodComponentType.PRBC);
        await server.VerifyDonationAsync(donationId);
        var patient = await RegisterPatientAsync();
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Emergency);
        var path = $"{Bank}/requests/{request}";

        // The medical officer groups the one sample and, signed in as the doctor, authorises blood on it; another officer cross-matches.
        await medicalOfficer.PostAsync($"{path}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 60, drawnBy: "Sr. Kavya Menon"));
        await medicalOfficer.PostAsync($"{path}/single-sample-override", new SingleSampleOverrideRequest("O-negative stock used up, ongoing haemorrhage"));
        await priya.PostAsync<CrossMatchDto>($"{path}/crossmatches", new RecordCrossMatchRequest(aPositive, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null));

        // Grouping, authorising and issuing are one person's word: refused. Another officer issues it.
        (await RefusalAsync(medicalOfficer, $"{path}/issues", new IssueBloodRequest(aPositive, "Staff nurse Asha")))
            .ShouldBe((HttpStatusCode.Forbidden, IssueSeparation.OwnChecks));
        (await priya.PostAsync<BloodIssueDto>($"{path}/issues", new IssueBloodRequest(aPositive, "Staff nurse Asha"))).UnitNumber.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Where_a_second_officer_is_asked_for_a_group_the_same_officer_found_on_another_request_does_not_confirm_this_one()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var patient = await RegisterPatientAsync();
        var first = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Routine);
        var second = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Routine);
        var third = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Routine);

        // Santosh groups two separately drawn samples A+ on two requests: R2 is not confirmed by R1, the same officer's.
        await santosh.PostAsync($"{Bank}/requests/{first}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 90, drawnBy: "Sr. Kavya Menon"));
        await santosh.PostAsync($"{Bank}/requests/{second}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 30, drawnBy: "Sr. Leela Thomas"));
        (await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{second}")).AwaitingSecondSample.ShouldBeTrue();

        // Another officer's grouping of a third separately drawn sample is confirmed by the earlier ones, whoever made them.
        await priya.PostAsync($"{Bank}/requests/{third}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 10, drawnBy: "Sr. Asha Mathew"));
        (await priya.GetAsync<BloodRequestDetail>($"{Bank}/requests/{third}")).AwaitingSecondSample.ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_sample_that_disagrees_is_audited_and_compared_with_the_patients_other_requests()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var priya = await server.SignInAsync(HospitalServer.Users.SecondBloodBankOfficer);
        var patient = await RegisterPatientAsync();
        var resting = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Routine);
        var confirmed = await AskAsync(patient.Id, BloodComponentType.PlateletConcentrate, BloodRequestUrgency.Routine);

        // The patient has an A+ request resting on one sample. A platelet request (the rule does not cover it) is grouped A+
        // too, and a new sample sent to it, which waits for nothing, groups B+: refused, but compared with the first request.
        await santosh.PostAsync($"{Bank}/requests/{resting}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 90, drawnBy: "Sr. Kavya Menon"));
        await santosh.PostAsync($"{Bank}/requests/{confirmed}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 80, drawnBy: "Sr. Leela Thomas"));
        (await RefusalAsync(priya, $"{Bank}/requests/{confirmed}/grouping", HospitalServer.BloodSample(BloodGroup.BPositive, drawnMinutesAgo: 20, drawnBy: "Sr. Asha Mathew", newSample: true)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.regroup_not_needed"));

        (await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{resting}")).ShouldSatisfyAllConditions(
            d => d.GroupingHeld.ShouldBeTrue(),
            d => d.DisagreeingSampleGroup.ShouldBe((BloodGroup?)BloodGroup.BPositive));
        (await AuditAsync(nameof(BloodRequest), resting)).ShouldContain(e => e.Action == "bloodbank.second_sample_discrepancy");
        (await AuditAsync(nameof(BloodRequest), confirmed)).ShouldContain(e => e.Action == "bloodbank.grouping_discrepancy_refused" && e.Severity == AuditSeverity.Warning);

        // A third, different group on a request whose samples already disagree changes nothing on it, but is audited too.
        (await RefusalAsync(priya, $"{Bank}/requests/{resting}/grouping", HospitalServer.BloodSample(BloodGroup.ABPositive, drawnMinutesAgo: 5, drawnBy: "Sr. Meena Joseph", newSample: true)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await AuditAsync(nameof(BloodRequest), resting)).ShouldContain(e => e.Action == "bloodbank.grouping_discrepancy_refused" && e.Severity == AuditSeverity.Warning);
    }

    [Fact]
    public async Task A_request_held_in_another_facility_files_its_disagreement_audit_under_that_facility()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var patient = await RegisterPatientAsync();
        var prbc = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Routine);
        var ffp = await AskAsync(patient.Id, BloodComponentType.FFP, BloodRequestUrgency.Routine);
        await santosh.PostAsync($"{Bank}/requests/{prbc}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 60, drawnBy: "Sr. Kavya Menon"));
        await santosh.PostAsync($"{Bank}/requests/{ffp}/grouping", HospitalServer.BloodSample(BloodGroup.BPositive, drawnMinutesAgo: 20, drawnBy: "Sr. Leela Thomas"));

        // The audit event is filed under the held request's own facility (the officer's here; the entry names it, so another
        // branch's held request is found in its own audit view).
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var heldFacility = await db.Set<BloodRequest>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.Id == prbc).Select(r => r.FacilityId).SingleAsync(Ct);
        (await AuditAsync(nameof(BloodRequest), prbc)).Single(e => e.Action == "bloodbank.second_sample_discrepancy").FacilityId.ShouldBe(heldFacility);
    }

    [Fact]
    public async Task A_second_sample_that_disagrees_is_recorded_though_the_officer_who_grouped_the_first_grouped_it()
    {
        var santosh = await server.SignInAsync(HospitalServer.Users.BloodBankOfficer);
        var patient = await RegisterPatientAsync();
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Urgent);
        var path = $"{Bank}/requests/{request}";
        await santosh.PostAsync($"{path}/grouping", HospitalServer.BloodSample(BloodGroup.APositive, drawnMinutesAgo: 45, drawnBy: "Sr. Kavya Menon"));

        // Santosh groups the second tube himself, where a second officer is asked for: B+. He does not confirm the group, but
        // the possible wrong blood in tube he found is recorded and holds the request.
        (await RefusalAsync(santosh, $"{path}/grouping", HospitalServer.BloodSample(BloodGroup.BPositive, drawnMinutesAgo: 10, drawnBy: "Sr. Leela Thomas", newSample: true)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await santosh.GetAsync<BloodRequestDetail>(path)).ShouldSatisfyAllConditions(
            d => (d.GroupingHeld, d.AwaitingSecondSample, d.PatientBloodGroup).ShouldBe((true, true, BloodGroup.APositive)),
            d => (d.DisagreeingSampleGroup, d.DisagreeingSampleDrawnBy).ShouldBe(((BloodGroup?)BloodGroup.BPositive, "Sr. Leela Thomas")));
        (await AuditAsync(nameof(BloodRequest), request)).ShouldContain(e => e.Action == "bloodbank.second_sample_discrepancy" && e.Severity == AuditSeverity.Warning);
    }

    /// <summary>A donor screened and bled, one <paramref name="component"/> separated, and all five markers entered non-reactive by <paramref name="officer"/>.</summary>
    private static async Task<(Guid DonationId, Guid UnitId)> CollectAndEnterAsync(ApiClient officer, BloodGroup group, BloodComponentType component)
    {
        var donor = await officer.PostAsync<Created>($"{Bank}/donors", new SaveDonorRequest(
            FirstName: "Mahesh", LastName: "Donor" + Letters(8), Gender: Gender.Male, DateOfBirth: new DateOnly(1985, 1, 1).AddDays(Random.Shared.Next(3650)),
            BloodGroup: null, Phone: "9" + Digits(9), Email: null, Address: null, Occupation: null, ConsentToNotify: true, Notes: null));
        var screening = await officer.PostAsync<DonorScreeningDto>($"{Bank}/donors/{donor.Id}/screenings", new ScreenDonorRequest(
            HaemoglobinGdl: 14.2m, WeightKg: 68m, Systolic: 122, Diastolic: 78, Pulse: 74, TemperatureC: 36.8m,
            Answers: DonorEligibility.Questions.Select(q => new ScreeningAnswer(q.Key, Yes: false)).ToList()));
        var donation = await officer.PostAsync<Created>($"{Bank}/donors/{donor.Id}/donations", new CollectDonationRequest(
            ScreeningId: screening.Id, BagNumber: null, BagType: BloodBagType.Triple, VolumeMl: 450, Type: DonationType.Voluntary,
            ReplacementForPatientId: null, CollectedAt: null, DonorReaction: null, Notes: null));
        var units = await officer.PostAsync<List<BloodUnitDto>>($"{Bank}/donations/{donation.Id}/components",
            new SeparateComponentsRequest([new ComponentPlan(component, null, "Blood bank fridge 2")]));
        var loaded = await officer.GetAsync<DonationDetail>($"{Bank}/donations/{donation.Id}");
        await officer.PutAsync($"{Bank}/donations/{donation.Id}/lab-results", Clear(group, loaded.Version));
        return (donation.Id, units.Single().Id);
    }

    private static RecordLabResultsRequest Clear(BloodGroup group, uint version) =>
        new(group, TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.NonReactive, version);

    private async Task<Guid> AskAsync(Guid patientId, BloodComponentType component, BloodRequestUrgency urgency, int units = 1)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        return (await doctor.PostAsync<Created>($"{Bank}/requests", new CreateBloodRequestRequest(
            patientId, AdmissionId: null, VisitId: null, component, Units: units, urgency, "Symptomatic anaemia, Hb 6.4 g/dl",
            RequiredBy: null, HaemoglobinGdl: 6.4m, PlateletCount: null))).Id;
    }

    private async Task<PatientCreated> RegisterPatientAsync() =>
        await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));

    /// <summary>
    /// The patient grouped <paramref name="group"/> on an earlier request, on two samples as the two-sample rule asks: the first
    /// by Santosh, the second by Priya. The patient then has a group on record.
    /// </summary>
    private async Task GroupedBeforeAsync(ApiClient santosh, Guid patientId, BloodGroup group)
    {
        var earlier = await AskAsync(patientId, BloodComponentType.PRBC, BloodRequestUrgency.Routine);
        await santosh.PostAsync($"{Bank}/requests/{earlier}/grouping", HospitalServer.BloodSample(group, drawnMinutesAgo: 180));
        await server.ConfirmGroupOnSecondSampleAsync(earlier, group);
    }

    /// <summary>
    /// The facility's transfusion committee has documented another policy than the two-sample rule: the rule switched off,
    /// which waits for and is approved by a second administrator; disposing asks again (the setting removed, so on by default).
    /// </summary>
    private async Task<IAsyncDisposable> TwoSampleRuleOffAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await server.SaveSettingAsync(admin, BloodBankSettings.TwoSampleRule, new SaveSettingRequest(JsonValue.Create(false), facilityId, false, null));
        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(BloodBankSettings.TwoSampleRule, facilityId, CancellationToken.None);
        });
    }

    /// <summary>
    /// The facility's one officer verifies and issues alone: the second officer switched off, which waits for and is approved by
    /// a second administrator; disposing asks again (the setting removed, so on by default).
    /// </summary>
    private async Task<IAsyncDisposable> SecondOfficerOffAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await server.SaveSettingAsync(admin, BloodBankSettings.SeparateVerifier, new SaveSettingRequest(JsonValue.Create(false), facilityId, false, null));
        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(BloodBankSettings.SeparateVerifier, facilityId, CancellationToken.None);
        });
    }

    private async Task<string> PractitionerNameAsync(Guid practitionerId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Practitioner>().IgnoreQueryFilters()
            .Where(p => p.Id == practitionerId).Select(p => p.DisplayName).SingleAsync(Ct);
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object? body, HttpMethod? method = null)
    {
        using var response = await api.SendRawAsync(method ?? HttpMethod.Post, path, body);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private async Task<List<AuditEvent>> AuditAsync(string entityType, Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var key = id.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.EntityType == entityType && e.EntityId == key).ToListAsync(Ct);
    }

    private async Task<BloodUnitStatus> UnitStatusAsync(Guid unitId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<BloodComponent>().IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == unitId).Select(c => c.Status).SingleAsync(Ct);
    }

    private static async Task<Guid> UserIdAsync(ApiClient api) => (await api.GetAsync<AuthUser>("/api/v1/auth/me")).Id;

    private async Task<List<UserNotification>> NoticesAsync(Guid userId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .Where(n => n.UserId == userId).ToListAsync(Ct);
    }

    /// <summary>The advisory lock key of a patient's blood groupings, as <c>BloodGroupingLocks</c> takes it.</summary>
    private static long PatientGroupingLockKey(Guid patientId) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"bloodbank.grouping:{patientId:N}")));

    /// <summary>Waits until <paramref name="count"/> sessions wait for the lock (PostgreSQL shows a bigint key as its high and low halves).</summary>
    private async Task WaitForLockWaitersAsync(long key, int count)
    {
        var (high, low) = ((long)(uint)(key >> 32), (long)(uint)key);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await db.Database.SqlQuery<int>(
                   $"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND classid::bigint = {high} AND objid::bigint = {low} AND objsubid = 1")
                   .SingleAsync(Ct) < count)
        {
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "both groupings should wait for the patient's lock");
            await Task.Delay(50, Ct);
        }
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private sealed record Created(Guid Id);

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
