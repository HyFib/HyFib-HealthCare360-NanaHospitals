using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.BloodBank;

/// <summary>
/// The two-sample rule (BSH pre-transfusion compatibility guidelines): a patient with no blood group on record is not given
/// group-specific blood on one sample's grouping, because a wrong-blood-in-tube sample groups as the patient it came from
/// and its cross-match is "compatible" with that patient. A second sample, drawn separately (another time, its collector
/// named), is grouped by another officer as a new sample and must agree with the first. The doctor's emergency release (O
/// Rh(D)-negative red cells, AB plasma) is not slowed by it; group-specific blood on one sample is the doctor's override,
/// with a clinical reason.
/// </summary>
public sealed class TwoSampleRuleTests
{
    private static readonly Guid Santosh = Guid.CreateVersion7();
    private static readonly Guid Priya = Guid.CreateVersion7();
    private static readonly Guid DrRao = Guid.CreateVersion7();

    private static StaffStamp Officer(Guid userId, string name, DateTimeOffset? at = null) => new(userId, name, at ?? TestData.Now);

    private static StaffStamp SantoshNow => Officer(Santosh, "Mr. Santosh Gaikwad");

    private static StaffStamp PriyaLater => Officer(Priya, "Ms. Priya Nair", TestData.Now.AddHours(1));

    /// <summary>The sample a ward drew from a patient never grouped before, grouped A+ by Santosh: it waits for a second one.</summary>
    private static BloodRequest FirstTimePatientGrouped(BloodRequestUrgency urgency = BloodRequestUrgency.Urgent, BloodComponentType component = BloodComponentType.PRBC)
    {
        var request = new BloodRequest { UnitsRequested = 2, Urgency = urgency, ComponentType = component, Status = BloodRequestStatus.Pending };
        request.RecordGrouping(BloodGroup.APositive, SantoshNow, earlierGroup: null, TestData.Drawn(40, "Sr. Kavya Menon"), needsSecondSample: true)
            .IsSuccess.ShouldBeTrue();
        return request;
    }

    [Fact]
    public void A_patient_never_grouped_before_waits_for_a_second_sample_before_anything_group_specific()
    {
        var request = FirstTimePatientGrouped();

        (request.AwaitingSecondSample, request.GroupingHeld, request.HistoryHeld).ShouldBe((true, true, false));
        request.GroupForIssue.ShouldBe(BloodGroup.Unknown, "nothing is cross-matched or issued on one sample's group");
        request.Status.ShouldBe(BloodRequestStatus.InProcess);

        // The draw is recorded as the tube says, apart from when the blood bank received and grouped it.
        (request.SampleDrawnAt, request.SampleDrawnBy).ShouldBe((TestData.Now.AddMinutes(-40), "Sr. Kavya Menon"));
        (request.SampleReceivedAt, request.GroupedAt).ShouldBe((TestData.Now, TestData.Now));

        // Red cells are refused at issue: no cross-match counts while it is held, and the group the issue relies on is unknown.
        IssueCompatibility.Check(new IssueContext(BloodComponentType.PRBC, BloodGroup.APositive, request.GroupForIssue, false, false))
            .Error!.Code.ShouldBe("bloodbank.crossmatch_required");
        IssueCompatibility.Check(new IssueContext(BloodComponentType.PRBC, BloodGroup.APositive, request.GroupForIssue, true, false))
            .Error!.Code.ShouldBe("bloodbank.abo_incompatible");
    }

    [Fact]
    public void Another_officer_groups_a_second_separately_drawn_sample_that_agrees_and_the_group_is_confirmed()
    {
        var request = FirstTimePatientGrouped();

        // Not the officer who grouped the first, where the facility asks for a second officer; not the same draw again.
        request.Regroup(BloodGroup.APositive, Officer(Santosh, "Mr. Santosh Gaikwad", TestData.Now.AddHours(1)), TestData.Drawn(5, "Sr. Leela Thomas"), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.regroup_own_sample");
        request.Regroup(BloodGroup.APositive, PriyaLater, TestData.Drawn(40, "Sr. Kavya Menon"), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.sample_not_separate");
        request.Regroup(BloodGroup.APositive, PriyaLater, new SampleDraw(TestData.Now.AddMinutes(-40).AddSeconds(30), "Sr. Leela Thomas"), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.sample_not_separate", "the same minute on the tube is the same draw");
        request.AwaitingSecondSample.ShouldBeTrue();

        // Priya groups a second sample drawn 35 minutes after the first, by another nurse: A+, as the first.
        request.Regroup(BloodGroup.APositive, PriyaLater, TestData.Drawn(5, "Sr. Leela Thomas"), separateVerifier: true).IsSuccess.ShouldBeTrue();

        (request.AwaitingSecondSample, request.GroupingHeld, request.SecondSampleRequired).ShouldBe((false, false, true));
        (request.PatientBloodGroup, request.GroupForIssue, request.FirstSampleGroup).ShouldBe((BloodGroup.APositive, BloodGroup.APositive, (BloodGroup?)BloodGroup.APositive));
        (request.FirstSampleDrawnAt, request.FirstSampleDrawnBy).ShouldBe((TestData.Now.AddMinutes(-40), "Sr. Kavya Menon"));
        (request.SampleDrawnAt, request.SampleDrawnBy, request.SampleReceivedAt).ShouldBe((TestData.Now.AddMinutes(-5), "Sr. Leela Thomas", TestData.Now.AddHours(1)));
        (request.GroupedByUserId, request.RegroupedAt).ShouldBe(((Guid?)Priya, TestData.Now.AddHours(1)));
        IssueCompatibility.Check(new IssueContext(BloodComponentType.PRBC, BloodGroup.APositive, request.GroupForIssue, true, false)).Value.ShouldBeFalse();

        // Confirmed: nothing more to group as a new sample.
        request.Regroup(BloodGroup.APositive, Officer(Guid.CreateVersion7(), "Mr. Arun Pillai"), TestData.Drawn(1), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.regroup_not_needed");
    }

    [Fact]
    public void A_second_sample_that_disagrees_is_refused_as_wrong_blood_in_tube_and_the_request_stays_held()
    {
        var request = FirstTimePatientGrouped();

        // The first tube held another patient's blood: the second, from the patient, groups B+.
        request.Regroup(BloodGroup.BPositive, PriyaLater, TestData.Drawn(5, "Sr. Leela Thomas"), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");

        (request.AwaitingSecondSample, request.GroupingHeld, request.PatientBloodGroup, request.SampleDrawnBy)
            .ShouldBe((true, true, BloodGroup.APositive, "Sr. Kavya Menon"), "the request keeps the first grouping, and nothing group-specific goes");
        (request.DisagreeingSampleGroup, request.DisagreeingSampleAt).ShouldBe(((BloodGroup?)BloodGroup.BPositive, (DateTimeOffset?)TestData.Now.AddHours(1)),
            "the disagreement is recorded on the request");
        (request.DisagreeingSampleDrawnAt, request.DisagreeingSampleDrawnBy, request.DisagreeingSampleRequestNumber)
            .ShouldBe(((DateTimeOffset?)TestData.Now.AddMinutes(-5), "Sr. Leela Thomas", (string?)null),
                "the disagreeing tube's draw and collector are kept, for the investigation; it was this request's own sample");
        request.RecordGrouping(BloodGroup.BPositive, PriyaLater, earlierGroup: null, TestData.Drawn(5), needsSecondSample: true)
            .Error!.Code.ShouldBe("bloodbank.grouping_discrepancy", "the same request grouped otherwise is the discrepancy it always was");
    }

    [Fact]
    public void A_second_sample_that_disagrees_holds_the_request_although_a_doctor_authorised_blood_on_one_sample()
    {
        var request = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        request.AuthoriseSingleSampleOverride(Officer(DrRao, "Dr. Ramesh Rao"), Guid.CreateVersion7(), "O-negative stock exhausted, ongoing PPH, Hb 5.2", contrary: null)
            .IsSuccess.ShouldBeTrue();
        request.GroupingHeld.ShouldBeFalse("group-specific A+ goes on the one sample");

        // The second sample groups B+: one of the two tubes holds another patient's blood. Refused, and the request is held
        // again, whatever the doctor authorised: only the emergency release's O Rh(D)-negative red cells and AB plasma go.
        request.Regroup(BloodGroup.BPositive, PriyaLater, TestData.Drawn(5, "Sr. Leela Thomas"), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");

        (request.GroupingHeld, request.GroupForIssue).ShouldBe((true, BloodGroup.Unknown), "the override was for one sample, not for two that disagree");
        IssueCompatibility.Check(new IssueContext(BloodComponentType.PRBC, BloodGroup.APositive, request.GroupForIssue, true, false))
            .Error!.Code.ShouldBe("bloodbank.abo_incompatible");
        request.AuthoriseSingleSampleOverride(Officer(DrRao, "Dr. Ramesh Rao"), Guid.CreateVersion7(), "Still bleeding, asked again on the first sample", contrary: null)
            .Error!.Code.ShouldBe("bloodbank.grouping_discrepancy", "no doctor authorises blood on either of two samples that disagree");
        request.AuthoriseEmergencyRelease(Officer(DrRao, "Dr. Ramesh Rao"), Guid.CreateVersion7(), "Haemorrhagic shock, samples disagree").IsSuccess.ShouldBeTrue();
        IssueCompatibility.Check(new IssueContext(BloodComponentType.PRBC, BloodGroup.ONegative, request.GroupForIssue, false, request.EmergencyReleaseAuthorised))
            .Value.ShouldBeTrue("the emergency release still lets O Rh(D)-negative red cells go");

        // The same disagreement grouped on the request itself (not as a new sample) is refused and recorded alike.
        var sameRequest = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        sameRequest.AuthoriseSingleSampleOverride(Officer(DrRao, "Dr. Ramesh Rao"), Guid.CreateVersion7(), "Ongoing haemorrhage, no O-negative left", contrary: null)
            .IsSuccess.ShouldBeTrue();
        sameRequest.RecordGrouping(BloodGroup.BPositive, PriyaLater, earlierGroup: null, TestData.Drawn(5), needsSecondSample: true)
            .Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");
        (sameRequest.GroupingHeld, sameRequest.DisagreeingSampleGroup).ShouldBe((true, (BloodGroup?)BloodGroup.BPositive));

        // A further sample, drawn separately, that agrees with the first confirms its group: two of the three agree.
        request.Regroup(BloodGroup.APositive, Officer(Guid.CreateVersion7(), "Mr. Arun Pillai", TestData.Now.AddHours(2)),
            new SampleDraw(TestData.Now.AddMinutes(90), "Sr. Leela Thomas"), separateVerifier: true).IsSuccess.ShouldBeTrue();
        (request.AwaitingSecondSample, request.GroupingHeld, request.PatientBloodGroup, request.DisagreeingSampleGroup)
            .ShouldBe((false, false, BloodGroup.APositive, (BloodGroup?)BloodGroup.BPositive), "the disagreeing sample stays on the record");
    }

    [Fact]
    public void A_later_sample_on_a_request_that_needs_no_second_sample_is_not_recorded_as_a_disagreement()
    {
        var request = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };
        request.RecordGrouping(BloodGroup.APositive, SantoshNow, earlierGroup: null, TestData.Drawn(), needsSecondSample: false).IsSuccess.ShouldBeTrue();

        request.RecordGrouping(BloodGroup.BPositive, PriyaLater, earlierGroup: null, TestData.Drawn(5), needsSecondSample: false)
            .Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");

        (request.DisagreeingSampleGroup, request.GroupingHeld).ShouldBe((null, false), "the same-request discrepancy is refused as it always was");
    }

    [Fact]
    public void A_patient_with_a_group_on_record_from_another_sample_proceeds_on_this_one()
    {
        var draw = TestData.Drawn(40);
        GroupingHistory.NeedsSecondSample(true, draw, [new EarlierGrouping(BloodGroup.APositive, TestData.Now.AddDays(-200))]).ShouldBeFalse();

        var request = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };
        request.RecordGrouping(BloodGroup.APositive, SantoshNow, earlierGroup: null, draw, needsSecondSample: false).IsSuccess.ShouldBeTrue();

        (request.AwaitingSecondSample, request.GroupingHeld, request.SecondSampleRequired, request.GroupForIssue)
            .ShouldBe((false, false, false, BloodGroup.APositive));
        request.Regroup(BloodGroup.APositive, PriyaLater, TestData.Drawn(5), separateVerifier: true).Error!.Code.ShouldBe("bloodbank.regroup_not_needed");
    }

    [Fact]
    public void While_a_second_officer_is_required_an_earlier_grouping_confirms_only_if_another_officer_made_it()
    {
        var santosh = Guid.NewGuid();
        var priya = Guid.NewGuid();
        var draw = TestData.Drawn(40);
        var bySantosh = new[] { new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt.AddHours(-3), santosh) };

        GroupingHistory.NeedsSecondSample(true, draw, bySantosh, santosh, separateVerifier: true).ShouldBeTrue("the same officer found it: another officer groups the second sample");
        GroupingHistory.NeedsSecondSample(true, draw, bySantosh, priya, separateVerifier: true).ShouldBeFalse("another officer grouped the earlier sample");
        GroupingHistory.NeedsSecondSample(true, draw, bySantosh, santosh, separateVerifier: false).ShouldBeFalse("the facility has one officer on duty");
        GroupingHistory.NeedsSecondSample(true, draw, [new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt.AddHours(-3))], santosh, separateVerifier: true)
            .ShouldBeFalse("an officer not on record is taken for another");
        GroupingHistory.NeedsSecondSample(true, draw, [.. bySantosh, new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt.AddHours(-9), priya)], santosh, separateVerifier: true)
            .ShouldBeFalse("one of two earlier groupings was another officer's");

        // The officer carries from the request that was grouped, and a confirmed group stands whoever groups another sample.
        var confirmed = GroupingHistory.OnRecord(BloodGroup.APositive, draw.DrawnAt.AddHours(-3), BloodGroup.APositive, draw.DrawnAt.AddHours(-9), regrouped: true, disputed: false, groupedBy: santosh);
        GroupingHistory.NeedsSecondSample(true, draw, confirmed, santosh, separateVerifier: true).ShouldBeFalse();
        GroupingHistory.OnRecord(BloodGroup.APositive, draw.DrawnAt, null, null, regrouped: false, disputed: false, groupedBy: santosh)
            .ShouldBe([new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt, santosh)]);
    }

    [Fact]
    public void Only_an_earlier_grouping_of_another_draw_counts_and_only_while_the_rule_applies()
    {
        var draw = TestData.Drawn(40);

        GroupingHistory.NeedsSecondSample(true, draw, []).ShouldBeTrue("nothing on record");
        GroupingHistory.NeedsSecondSample(true, draw, [new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt)])
            .ShouldBeTrue("one tube sent with two requests is one sample");
        GroupingHistory.NeedsSecondSample(true, draw, [new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt.AddSeconds(25).AddTicks(7))])
            .ShouldBeTrue("draw times are written to the minute");
        GroupingHistory.NeedsSecondSample(true, draw, [new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt.AddMinutes(-1))]).ShouldBeFalse();
        GroupingHistory.NeedsSecondSample(true, draw, [new EarlierGrouping(BloodGroup.Unknown, TestData.Now.AddDays(-3))]).ShouldBeTrue();
        GroupingHistory.NeedsSecondSample(true, draw, [new EarlierGrouping(BloodGroup.OPositive, null)])
            .ShouldBeFalse("a grouping from before draws were recorded was another request's sample");
        GroupingHistory.NeedsSecondSample(false, draw, []).ShouldBeFalse("the transfusion committee has documented another policy");

        // A request whose group two samples confirmed puts both draws on record: its second tube used again for this request
        // is not this request's only confirmation, the first sample is. One that a disagreeing sample disputes puts none.
        var confirmed = GroupingHistory.OnRecord(BloodGroup.APositive, draw.DrawnAt, BloodGroup.APositive, draw.DrawnAt.AddHours(-3), regrouped: true, disputed: false);
        confirmed.ShouldBe([new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt), new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt.AddHours(-3))]);
        GroupingHistory.NeedsSecondSample(true, draw, confirmed).ShouldBeFalse("the group is on record from two samples");
        GroupingHistory.OnRecord(BloodGroup.APositive, draw.DrawnAt, BloodGroup.BPositive, draw.DrawnAt.AddHours(-3), regrouped: true, disputed: false)
            .ShouldBe([new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt)], "a first sample a new sample set aside confirms nothing");
        GroupingHistory.OnRecord(BloodGroup.APositive, draw.DrawnAt, null, null, regrouped: false, disputed: false)
            .ShouldBe([new EarlierGrouping(BloodGroup.APositive, draw.DrawnAt)]);
        GroupingHistory.OnRecord(BloodGroup.APositive, draw.DrawnAt.AddHours(-3), null, null, regrouped: false, disputed: true)
            .ShouldBeEmpty("a group two samples disagree on is no one's record");

        // A disagreement with the earlier group holds the request as before; it does not also wait for a second sample.
        var request = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };
        request.RecordGrouping(BloodGroup.BPositive, SantoshNow, earlierGroup: BloodGroup.APositive, draw, needsSecondSample: true).IsSuccess.ShouldBeTrue();
        (request.HistoryHeld, request.AwaitingSecondSample, request.SecondSampleRequired).ShouldBe((true, false, false));
    }

    [Fact]
    public void The_one_officer_on_duty_groups_the_second_sample_only_where_the_facility_has_no_second_officer()
    {
        var request = FirstTimePatientGrouped();

        request.Regroup(BloodGroup.APositive, Officer(Santosh, "Mr. Santosh Gaikwad", TestData.Now.AddHours(1)), TestData.Drawn(5, "Sr. Leela Thomas"), separateVerifier: false)
            .IsSuccess.ShouldBeTrue();
        (request.AwaitingSecondSample, request.GroupedByUserId).ShouldBe((false, (Guid?)Santosh));

        // A grouping that disagrees with earlier requests is still regrouped by another officer, whatever the setting.
        var held = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };
        held.RecordGrouping(BloodGroup.BPositive, SantoshNow, earlierGroup: BloodGroup.APositive, TestData.Drawn(40), needsSecondSample: false).IsSuccess.ShouldBeTrue();
        held.Regroup(BloodGroup.APositive, Officer(Santosh, "Mr. Santosh Gaikwad", TestData.Now.AddHours(1)), TestData.Drawn(5), separateVerifier: false)
            .Error!.Code.ShouldBe("bloodbank.regroup_own_sample");
    }

    [Fact]
    public void The_emergency_release_is_not_slowed_by_the_rule()
    {
        var request = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);

        request.AuthoriseEmergencyRelease(Officer(DrRao, "Dr. Ramesh Rao"), Guid.CreateVersion7(), "Haemorrhagic shock after a road accident").IsSuccess.ShouldBeTrue();

        // O Rh(D)-negative red cells go uncross-matched while the second sample is awaited; group-specific A+ does not.
        IssueCompatibility.Check(new IssueContext(BloodComponentType.PRBC, BloodGroup.ONegative, request.GroupForIssue, false, request.EmergencyReleaseAuthorised))
            .Value.ShouldBeTrue();
        IssueCompatibility.Check(new IssueContext(BloodComponentType.PRBC, BloodGroup.APositive, request.GroupForIssue, false, request.EmergencyReleaseAuthorised))
            .Error!.Code.ShouldBe("bloodbank.crossmatch_required");

        // AB plasma likewise; A plasma waits for the second sample.
        IssueCompatibility.Check(new IssueContext(BloodComponentType.FFP, BloodGroup.ABPositive, request.GroupForIssue, false, request.EmergencyReleaseAuthorised))
            .Value.ShouldBeTrue();
        IssueCompatibility.Check(new IssueContext(BloodComponentType.FFP, BloodGroup.APositive, request.GroupForIssue, false, request.EmergencyReleaseAuthorised))
            .Error!.Code.ShouldBe("bloodbank.patient_not_grouped");
    }

    [Fact]
    public void Group_specific_blood_on_one_sample_needs_the_doctors_override_with_a_clinical_reason()
    {
        var request = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        var doctor = Officer(DrRao, "Dr. Ramesh Rao");
        var practitioner = Guid.CreateVersion7();

        request.AuthoriseSingleSampleOverride(doctor, practitioner, "Bleeding", contrary: null).Error!.Fields!.Keys.ShouldBe(["reason"]);
        request.AuthoriseSingleSampleOverride(doctor, practitioner, "   Bleeding    ", contrary: null).Error!.Fields!.Keys
            .ShouldBe(["reason"], "the reason is counted without its spaces");
        request.GroupingHeld.ShouldBeTrue();

        request.AuthoriseSingleSampleOverride(doctor, practitioner, "  O-negative stock exhausted, ongoing PPH, Hb 5.2  ", contrary: null).IsSuccess.ShouldBeTrue();

        (request.SingleSampleOverrideBy, request.SingleSampleOverrideByUserId, request.SingleSampleOverrideByPractitionerId, request.SingleSampleOverrideReason, request.SingleSampleOverrideAt)
            .ShouldBe(("Dr. Ramesh Rao", (Guid?)DrRao, (Guid?)practitioner, "O-negative stock exhausted, ongoing PPH, Hb 5.2", (DateTimeOffset?)TestData.Now));
        (request.GroupingHeld, request.AwaitingSecondSample, request.GroupForIssue).ShouldBe((false, true, BloodGroup.APositive),
            "group-specific blood goes on the one sample, and the second is still grouped when it comes");
        IssueCompatibility.Check(new IssueContext(BloodComponentType.PRBC, BloodGroup.APositive, request.GroupForIssue, true, false)).Value.ShouldBeFalse();
        request.AuthoriseSingleSampleOverride(doctor, practitioner, "Asked again for the same request", contrary: null).Error!.Code.ShouldBe("bloodbank.single_sample_override_authorised");

        // The second sample, when it comes, still confirms the group.
        request.Regroup(BloodGroup.APositive, PriyaLater, TestData.Drawn(5, "Sr. Leela Thomas"), separateVerifier: true).IsSuccess.ShouldBeTrue();
        request.AwaitingSecondSample.ShouldBeFalse();
    }

    [Fact]
    public void The_override_is_only_for_a_request_waiting_for_a_second_sample()
    {
        var doctor = Officer(DrRao, "Dr. Ramesh Rao");
        const string reason = "Ongoing haemorrhage, no O-negative left";

        new BloodRequest { UnitsRequested = 1, Urgency = BloodRequestUrgency.Emergency, Status = BloodRequestStatus.Pending }
            .AuthoriseSingleSampleOverride(doctor, Guid.CreateVersion7(), reason, contrary: null)
            .Error!.Code.ShouldBe("bloodbank.patient_not_grouped");

        var onRecord = new BloodRequest { UnitsRequested = 1, Urgency = BloodRequestUrgency.Emergency, Status = BloodRequestStatus.Pending };
        onRecord.RecordGrouping(BloodGroup.APositive, SantoshNow, earlierGroup: null, TestData.Drawn(), needsSecondSample: false).IsSuccess.ShouldBeTrue();
        onRecord.AuthoriseSingleSampleOverride(doctor, Guid.CreateVersion7(), reason, contrary: null).Error!.Code.ShouldBe("bloodbank.single_sample_override_not_needed");

        var disagrees = new BloodRequest { UnitsRequested = 1, Urgency = BloodRequestUrgency.Emergency, Status = BloodRequestStatus.Pending };
        disagrees.RecordGrouping(BloodGroup.BPositive, SantoshNow, earlierGroup: BloodGroup.APositive, TestData.Drawn(), needsSecondSample: false).IsSuccess.ShouldBeTrue();
        disagrees.AuthoriseSingleSampleOverride(doctor, Guid.CreateVersion7(), reason, contrary: null).Error!.Code
            .ShouldBe("bloodbank.grouping_history_discrepancy", "a group that disagrees with earlier requests is decided by a new sample, not overridden");
        disagrees.GroupingHeld.ShouldBeTrue();

        var cancelled = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        cancelled.Cancel("Requested by mistake", TestData.Now).IsSuccess.ShouldBeTrue();
        cancelled.AuthoriseSingleSampleOverride(doctor, Guid.CreateVersion7(), reason, contrary: null).Error!.Code.ShouldBe("bloodbank.request_closed");
    }

    [Theory]
    [InlineData(BloodRequestUrgency.Routine)]
    [InlineData(BloodRequestUrgency.Urgent)]
    public void Blood_on_one_sample_is_the_emergency_override_and_is_refused_on_other_requests(BloodRequestUrgency urgency)
    {
        var doctor = Officer(DrRao, "Dr. Ramesh Rao");
        const string reason = "Symptomatic anaemia, Hb 6.1, theatre list tomorrow";

        // Nothing clinical stops the blood bank waiting for the second sample: the rule is not a click-through.
        var request = FirstTimePatientGrouped(urgency);
        request.AuthoriseSingleSampleOverride(doctor, Guid.CreateVersion7(), reason, contrary: null).Error!.Code.ShouldBe("bloodbank.not_emergency");
        (request.SingleSampleOverridden, request.GroupingHeld, request.GroupForIssue).ShouldBe((false, true, BloodGroup.Unknown));

        // The emergency release is refused there alike; on an emergency request the override goes.
        request.AuthoriseEmergencyRelease(doctor, Guid.CreateVersion7(), reason).Error!.Code.ShouldBe("bloodbank.not_emergency");
        FirstTimePatientGrouped(BloodRequestUrgency.Emergency).AuthoriseSingleSampleOverride(doctor, Guid.CreateVersion7(), reason, contrary: null)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_sample_grouped_on_another_request_that_disagrees_ends_the_override_given_before_it_and_frees_the_reserved_units()
    {
        // The PRBC request's one sample groups A+; the doctor authorises blood on it, and an A+ unit is cross-matched.
        var prbc = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        prbc.RequestNumber = "BR26-00412";
        prbc.AuthoriseSingleSampleOverride(Officer(DrRao, "Dr. Ramesh Rao"), Guid.CreateVersion7(), "O-negative stock exhausted, ongoing PPH", contrary: null)
            .IsSuccess.ShouldBeTrue();
        prbc.OnUnitReserved();
        (prbc.GroupingHeld, prbc.GroupForIssue, prbc.UnitsReserved).ShouldBe((false, BloodGroup.APositive, 1));

        // The FFP request's sample, drawn 40 minutes later by another nurse, groups O+: one of the tubes holds another
        // patient's blood. Recorded on the PRBC request as its disagreeing sample, with the other request's number.
        var ffpSample = new FoundGrouping(BloodGroup.OPositive, TestData.Now, "Sr. Leela Thomas", "BR26-00413");
        prbc.RecordContrarySample(ffpSample, TestData.Now.AddMinutes(30)).ShouldBeTrue();

        (prbc.SamplesDisagree, prbc.GroupingHeld, prbc.GroupForIssue).ShouldBe((true, true, BloodGroup.Unknown), "the override was for one sample, not for two that disagree");
        (prbc.DisagreeingSampleGroup, prbc.DisagreeingSampleAt, prbc.DisagreeingSampleDrawnAt, prbc.DisagreeingSampleDrawnBy, prbc.DisagreeingSampleRequestNumber)
            .ShouldBe(((BloodGroup?)BloodGroup.OPositive, (DateTimeOffset?)TestData.Now.AddMinutes(30), (DateTimeOffset?)TestData.Now, "Sr. Leela Thomas", "BR26-00413"));
        IssueCompatibility.Check(new IssueContext(BloodComponentType.PRBC, BloodGroup.APositive, prbc.GroupForIssue, true, false))
            .Error!.Code.ShouldBe("bloodbank.abo_incompatible", "an A+ unit cross-matched on the first sample no longer goes to a patient another sample says is O+");
        prbc.AuthoriseSingleSampleOverride(Officer(DrRao, "Dr. Ramesh Rao"), Guid.CreateVersion7(), "Asked again, still bleeding badly", contrary: null)
            .Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");
        prbc.DisputedGroups(null).ShouldBe([BloodGroup.APositive, BloodGroup.OPositive]);
        prbc.StandingGiven(null).ShouldBe(PatientGroupStanding.SamplesDisagree);

        // The first disagreeing sample is kept; the units reserved on the first sample go back to stock.
        prbc.RecordContrarySample(new FoundGrouping(BloodGroup.BPositive, TestData.Now, "Sr. Asha", "BR26-00414"), TestData.Now.AddHours(1)).ShouldBeFalse();
        prbc.DisagreeingSampleGroup.ShouldBe(BloodGroup.OPositive);
        prbc.OnReservationsReleased();
        (prbc.UnitsReserved, prbc.Status).ShouldBe((0, BloodRequestStatus.InProcess));

        // Two of three: a further sample, drawn at another time than both tubes, that agrees with the first decides.
        var arun = Officer(Guid.CreateVersion7(), "Mr. Arun Pillai", TestData.Now.AddHours(2));
        prbc.Regroup(BloodGroup.APositive, arun, new SampleDraw(TestData.Now.AddSeconds(20), "Sr. Leela Thomas"), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.sample_not_separate", "the tube that disagreed decides nothing");
        prbc.Regroup(BloodGroup.APositive, arun, new SampleDraw(TestData.Now.AddMinutes(75), "Sr. Leela Thomas"), separateVerifier: true).IsSuccess.ShouldBeTrue();
        (prbc.AwaitingSecondSample, prbc.GroupingHeld, prbc.StandingGiven(null)).ShouldBe((false, false, PatientGroupStanding.Grouped));
    }

    [Fact]
    public void A_request_resting_on_one_sample_is_held_and_refused_the_override_while_a_sample_on_another_request_disagrees()
    {
        var doctor = Officer(DrRao, "Dr. Ramesh Rao");
        const string reason = "O-negative stock exhausted, ongoing PPH";
        var ffpSample = new FoundGrouping(BloodGroup.OPositive, TestData.Now, "Sr. Leela Thomas", "BR26-00413");

        // Found on the other request but not recorded here (two groupings saved at once before the lock, or a request grouped
        // before this rule): held all the same, and refused the override.
        var prbc = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        (prbc.HeldGiven(ffpSample), prbc.GroupForIssueGiven(ffpSample), prbc.StandingGiven(ffpSample)).ShouldBe((true, BloodGroup.Unknown, PatientGroupStanding.SamplesDisagree));
        prbc.DisputedGroups(ffpSample).ShouldBe([BloodGroup.APositive, BloodGroup.OPositive]);
        var refused = prbc.AuthoriseSingleSampleOverride(doctor, Guid.CreateVersion7(), reason, ffpSample);
        refused.Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");
        refused.Error.Message.ShouldContain("BR26-00413");
        prbc.SingleSampleOverridden.ShouldBeFalse();

        // Once overridden, a contrary sample found later holds it again, recorded or not.
        prbc.AuthoriseSingleSampleOverride(doctor, Guid.CreateVersion7(), reason, contrary: null).IsSuccess.ShouldBeTrue();
        (prbc.HeldGiven(null), prbc.HeldGiven(ffpSample)).ShouldBe((false, true));

        // A sample that agrees holds nothing.
        var agreeing = new FoundGrouping(BloodGroup.APositive, TestData.Now, "Sr. Leela Thomas", "BR26-00413");
        (prbc.HeldGiven(agreeing), prbc.StandingGiven(agreeing)).ShouldBe((false, PatientGroupStanding.OneSample));
        prbc.DisputedGroups(agreeing).ShouldBeEmpty();

        // Recorded first, the override is refused on the request alone.
        var recorded = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        recorded.RecordContrarySample(ffpSample, TestData.Now.AddMinutes(30)).ShouldBeTrue();
        recorded.AuthoriseSingleSampleOverride(doctor, Guid.CreateVersion7(), reason, contrary: null).Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");
    }

    [Fact]
    public void Only_a_request_resting_on_one_sample_records_a_contrary_sample()
    {
        var contrary = new FoundGrouping(BloodGroup.OPositive, TestData.Now, "Sr. Leela Thomas", "BR26-00413");

        // Confirmed on two samples: its group rests on two tubes that agree.
        var confirmed = FirstTimePatientGrouped();
        confirmed.Regroup(BloodGroup.APositive, PriyaLater, TestData.Drawn(5, "Sr. Leela Thomas"), separateVerifier: true).IsSuccess.ShouldBeTrue();
        confirmed.RecordContrarySample(contrary, TestData.Now).ShouldBeFalse();

        // A group on record from another sample, or a facility that does not apply the rule.
        var onRecord = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };
        onRecord.RecordGrouping(BloodGroup.APositive, SantoshNow, earlierGroup: null, TestData.Drawn(), needsSecondSample: false).IsSuccess.ShouldBeTrue();
        onRecord.RecordContrarySample(contrary, TestData.Now).ShouldBeFalse();

        // Held against earlier requests already, cancelled, not grouped, or the same group.
        var historyHeld = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };
        historyHeld.RecordGrouping(BloodGroup.APositive, SantoshNow, earlierGroup: BloodGroup.BPositive, TestData.Drawn(), needsSecondSample: true).IsSuccess.ShouldBeTrue();
        historyHeld.RecordContrarySample(contrary, TestData.Now).ShouldBeFalse();
        historyHeld.DisputedGroups(null).ShouldBe([BloodGroup.APositive, BloodGroup.BPositive]);
        var cancelled = FirstTimePatientGrouped();
        cancelled.Cancel("Requested by mistake", TestData.Now).IsSuccess.ShouldBeTrue();
        cancelled.RecordContrarySample(contrary, TestData.Now).ShouldBeFalse();
        new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending }.RecordContrarySample(contrary, TestData.Now).ShouldBeFalse();
        FirstTimePatientGrouped().RecordContrarySample(contrary with { Group = BloodGroup.APositive }, TestData.Now).ShouldBeFalse();
        FirstTimePatientGrouped().RecordContrarySample(contrary with { Group = BloodGroup.Unknown }, TestData.Now).ShouldBeFalse();

        // A request fully issued on the override still records it: its units may not have been started on the ward yet.
        var issued = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        issued.AuthoriseSingleSampleOverride(Officer(DrRao, "Dr. Ramesh Rao"), Guid.CreateVersion7(), "Ongoing haemorrhage, no O-negative", contrary: null).IsSuccess.ShouldBeTrue();
        issued.OnUnitIssued(wasReserved: false);
        issued.OnUnitIssued(wasReserved: false);
        issued.Status.ShouldBe(BloodRequestStatus.Issued);
        issued.RecordContrarySample(contrary, TestData.Now).ShouldBeTrue();
        issued.DisputedGroups(null).ShouldBe([BloodGroup.APositive, BloodGroup.OPositive]);
    }

    [Fact]
    public void Every_sample_grouped_on_a_request_is_on_record_with_its_draw_for_the_patients_other_requests()
    {
        GroupingHistory.SamplesOf(new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending }).ShouldBeEmpty();

        var waiting = FirstTimePatientGrouped();
        waiting.RequestNumber = "BR26-00412";
        GroupingHistory.SamplesOf(waiting).ShouldBe([new FoundGrouping(BloodGroup.APositive, TestData.Now.AddMinutes(-40), "Sr. Kavya Menon", "BR26-00412")]);

        // A disagreeing sample is on record too, with the request it was grouped on; then both samples of a confirmed group.
        waiting.RecordContrarySample(new FoundGrouping(BloodGroup.OPositive, TestData.Now, "Sr. Leela Thomas", "BR26-00413"), TestData.Now).ShouldBeTrue();
        GroupingHistory.SamplesOf(waiting).ShouldBe([
            new FoundGrouping(BloodGroup.APositive, TestData.Now.AddMinutes(-40), "Sr. Kavya Menon", "BR26-00412"),
            new FoundGrouping(BloodGroup.OPositive, TestData.Now, "Sr. Leela Thomas", "BR26-00413"),
        ]);
        var confirmed = FirstTimePatientGrouped();
        confirmed.RequestNumber = "BR26-00415";
        confirmed.Regroup(BloodGroup.APositive, PriyaLater, TestData.Drawn(5, "Sr. Leela Thomas"), separateVerifier: true).IsSuccess.ShouldBeTrue();
        GroupingHistory.SamplesOf(confirmed).ShouldBe([
            new FoundGrouping(BloodGroup.APositive, TestData.Now.AddMinutes(-5), "Sr. Leela Thomas", "BR26-00415"),
            new FoundGrouping(BloodGroup.APositive, TestData.Now.AddMinutes(-40), "Sr. Kavya Menon", "BR26-00415"),
        ]);

        // The first that disagrees with a group is the contrary one; none when all agree or are unknown.
        var found = GroupingHistory.SamplesOf(waiting).Concat(GroupingHistory.SamplesOf(confirmed)).ToList();
        GroupingHistory.Contrary(BloodGroup.APositive, found)!.RequestNumber.ShouldBe("BR26-00413");
        GroupingHistory.Contrary(BloodGroup.APositive, GroupingHistory.SamplesOf(confirmed)).ShouldBeNull();
        GroupingHistory.Contrary(BloodGroup.APositive, [new FoundGrouping(BloodGroup.Unknown, null, null, "BR26-00416")]).ShouldBeNull();
        GroupingHistory.Contrary(BloodGroup.APositive, [new FoundGrouping(BloodGroup.APositive, TestData.Now.AddMinutes(-40), null, "BR26-00417")])
            .ShouldBeNull("the same tube grouped alike for another request agrees");
        GroupingHistory.Contrary(BloodGroup.APositive, [new FoundGrouping(BloodGroup.ONegative, TestData.Now.AddMinutes(-40), null, "BR26-00418")])
            .ShouldNotBeNull("one tube that groups two ways is no more to be relied on");
    }

    [Fact]
    public void A_sample_that_disagrees_is_recorded_whoever_grouped_it_and_whenever_it_was_drawn()
    {
        // Santosh, who grouped the first sample, groups the second tube where a second officer is asked for: B+. He may not
        // confirm the group, but the disagreement he found is not lost to that refusal.
        var request = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        request.Regroup(BloodGroup.BPositive, Officer(Santosh, "Mr. Santosh Gaikwad", TestData.Now.AddHours(1)), TestData.Drawn(5, "Sr. Leela Thomas"), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");
        (request.SamplesDisagree, request.GroupingHeld, request.DisagreeingSampleGroup, request.DisagreeingSampleDrawnBy)
            .ShouldBe((true, true, (BloodGroup?)BloodGroup.BPositive, "Sr. Leela Thomas"));

        // The same tube grouped again that comes out otherwise: no tube that groups two ways is relied on either.
        var sameTube = FirstTimePatientGrouped(BloodRequestUrgency.Emergency);
        sameTube.Regroup(BloodGroup.OPositive, PriyaLater, TestData.Drawn(40, "Sr. Kavya Menon"), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");
        (sameTube.SamplesDisagree, sameTube.DisagreeingSampleDrawnAt).ShouldBe((true, (DateTimeOffset?)TestData.Now.AddMinutes(-40)));

        // Agreeing, the refusals stand as before.
        var agrees = FirstTimePatientGrouped();
        agrees.Regroup(BloodGroup.APositive, Officer(Santosh, "Mr. Santosh Gaikwad", TestData.Now.AddHours(1)), TestData.Drawn(5), separateVerifier: true)
            .Error!.Code.ShouldBe("bloodbank.regroup_own_sample");
        agrees.DisagreeingSampleGroup.ShouldBeNull();
    }

    [Fact]
    public void The_first_sample_grouped_again_on_a_request_waiting_for_its_second_is_refused_pointing_to_a_new_sample()
    {
        var request = FirstTimePatientGrouped();

        // Not a silent success: nothing is confirmed, and the screen or the client is told to group a new sample.
        request.RecordGrouping(BloodGroup.APositive, PriyaLater, earlierGroup: null, TestData.Drawn(5, "Sr. Leela Thomas"), needsSecondSample: true)
            .Error!.Code.ShouldBe("bloodbank.group_as_new_sample");
        (request.AwaitingSecondSample, request.SampleDrawnBy, request.GroupedAt, request.DisagreeingSampleGroup)
            .ShouldBe((true, "Sr. Kavya Menon", (DateTimeOffset?)TestData.Now, (BloodGroup?)null));

        // A request that waits for nothing still takes the same group again as it always did.
        var onRecord = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };
        onRecord.RecordGrouping(BloodGroup.APositive, SantoshNow, earlierGroup: null, TestData.Drawn(), needsSecondSample: false).IsSuccess.ShouldBeTrue();
        onRecord.RecordGrouping(BloodGroup.APositive, PriyaLater, earlierGroup: null, TestData.Drawn(5), needsSecondSample: false).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(BloodComponentType.PlateletConcentrate, false)]
    [InlineData(BloodComponentType.Cryoprecipitate, false)]
    [InlineData(BloodComponentType.PRBC, true)]
    [InlineData(BloodComponentType.WholeBlood, true)]
    [InlineData(BloodComponentType.FFP, true)]
    public void The_two_sample_rule_holds_red_cells_and_plasma_only(BloodComponentType component, bool held)
    {
        var request = new BloodRequest { UnitsRequested = 2, Urgency = BloodRequestUrgency.Emergency, ComponentType = component, Status = BloodRequestStatus.Pending };
        request.RecordGrouping(BloodGroup.APositive, SantoshNow, earlierGroup: null, TestData.Drawn(40), needsSecondSample: true).IsSuccess.ShouldBeTrue();

        BloodRequest.TwoSampleRuleCovers(component).ShouldBe(held);
        (request.SecondSampleRequired, request.AwaitingSecondSample, request.GroupingHeld).ShouldBe((held, held, held));
        request.GroupForIssue.ShouldBe(held ? BloodGroup.Unknown : BloodGroup.APositive,
            "ABO-incompatible platelets are accepted practice and they are scarce: they go on one sample's grouping");
        request.StandingGiven(null).ShouldBe(held ? PatientGroupStanding.OneSample : PatientGroupStanding.Grouped);
    }

    [Fact]
    public void A_sample_is_grouped_only_with_its_collector_named()
    {
        var request = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };

        request.RecordGrouping(BloodGroup.APositive, SantoshNow, earlierGroup: null, new SampleDraw(TestData.Now.AddMinutes(-30), "  "), needsSecondSample: true)
            .Error!.Fields!.Keys.ShouldBe(["sampleDrawnBy"]);
        request.IsGrouped.ShouldBeFalse();

        var waiting = FirstTimePatientGrouped();
        waiting.Regroup(BloodGroup.APositive, PriyaLater, new SampleDraw(TestData.Now.AddMinutes(-5), ""), separateVerifier: true)
            .Error!.Fields!.Keys.ShouldBe(["sampleDrawnBy"]);
    }
}
