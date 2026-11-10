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
/// a new sample; and uncross-matched blood is released on the treating doctor's own sign-in, not on a name the blood bank
/// types. A blood bank with one officer on duty switches the second officer off (a second administrator approving), and
/// what that officer releases alone is listed for the medical officer's review.
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
        await santosh.PostAsync($"{Bank}/requests/{request}/grouping", new GroupPatientRequest(BloodGroup.APositive));

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

        // Now issuable: Santosh grouped the patient, Priya cross-matches, and Santosh issues.
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
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Urgent);

        // The request shows who grouped and who cross-matched, by account.
        await santosh.PostAsync($"{Bank}/requests/{request}/grouping", new GroupPatientRequest(BloodGroup.OPositive));
        await santosh.PostAsync<CrossMatchDto>($"{Bank}/requests/{request}/crossmatches",
            new RecordCrossMatchRequest(unitId, CrossmatchMethod.AntiHumanGlobulin, CrossmatchResult.Compatible, null));
        var santoshId = (await santosh.GetAsync<AuthUser>("/api/v1/auth/me")).Id;
        var detail = await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{request}");
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

        // The same officer groups, cross-matches and issues: allowed, flagged on the issue, listed and audited at Warning.
        var patient = await RegisterPatientAsync();
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Urgent);
        await santosh.PostAsync($"{Bank}/requests/{request}/grouping", new GroupPatientRequest(BloodGroup.BPositive));
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
        await santosh.PostAsync($"{Bank}/requests/{earlier}/grouping", new GroupPatientRequest(BloodGroup.APositive));

        // Now the sample groups B+: recorded, but the request is held, at Warning, and nothing is cross-matched or issued on it.
        var request = await AskAsync(patient.Id, BloodComponentType.PRBC, BloodRequestUrgency.Urgent);
        await santosh.PostAsync($"{Bank}/requests/{request}/grouping", new GroupPatientRequest(BloodGroup.BPositive));
        var held = await santosh.GetAsync<BloodRequestDetail>($"{Bank}/requests/{request}");
        (held.GroupingHeld, held.PatientBloodGroup, held.EarlierGroup).ShouldBe((true, BloodGroup.BPositive, (BloodGroup?)BloodGroup.APositive));
        (await AuditAsync(nameof(BloodRequest), request)).ShouldContain(e => e.Action == "bloodbank.grouping_history_discrepancy" && e.Severity == AuditSeverity.Warning);
        (await RefusalAsync(priya, $"{Bank}/requests/{request}/crossmatches",
            new RecordCrossMatchRequest(unitId, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null))).ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_history_discrepancy"));
        (await RefusalAsync(priya, $"{Bank}/requests/{request}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha")))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_history_discrepancy"));

        // The same sample grouped again A+ is still the same-request discrepancy; the way out is a new sample, grouped by
        // another officer than the one who grouped the first.
        (await RefusalAsync(priya, $"{Bank}/requests/{request}/grouping", new GroupPatientRequest(BloodGroup.APositive)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "bloodbank.grouping_discrepancy"));
        (await RefusalAsync(santosh, $"{Bank}/requests/{request}/grouping", new GroupPatientRequest(BloodGroup.APositive, NewSample: true)))
            .ShouldBe((HttpStatusCode.Forbidden, "bloodbank.regroup_own_sample"));
        await priya.PostAsync($"{Bank}/requests/{request}/grouping", new GroupPatientRequest(BloodGroup.APositive, NewSample: true));
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

    private async Task<Guid> AskAsync(Guid patientId, BloodComponentType component, BloodRequestUrgency urgency)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        return (await doctor.PostAsync<Created>($"{Bank}/requests", new CreateBloodRequestRequest(
            patientId, AdmissionId: null, VisitId: null, component, Units: 1, urgency, "Symptomatic anaemia, Hb 6.4 g/dl",
            RequiredBy: null, HaemoglobinGdl: 6.4m, PlateletCount: null))).Id;
    }

    private async Task<PatientCreated> RegisterPatientAsync() =>
        await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice));

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

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body, HttpMethod? method = null)
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

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private sealed record Created(Guid Id);

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
