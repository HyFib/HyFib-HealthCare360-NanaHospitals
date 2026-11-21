using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// A request for blood components for a patient (BB-06). The patient's ABO/Rh group used for compatibility is the
/// one determined by the blood bank on the request sample — never the self-reported group from registration — and is
/// compared with the groups of the patient's earlier requests. A patient with no group on record has it confirmed on a
/// second, separately drawn sample before group-specific red cells or plasma are cross-matched or issued (the two-sample
/// rule), and a grouping resting on one sample is held while any other sample of the patient disagrees with it.
/// </summary>
public sealed class BloodRequest : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? VisitId { get; set; }

    /// <summary>EMR order line that created the request (unique; makes the order handler idempotent).</summary>
    public Guid? OrderItemId { get; set; }

    public BloodComponentType ComponentType { get; set; }
    public int UnitsRequested { get; set; }
    public BloodRequestUrgency Urgency { get; set; }
    public string Indication { get; set; } = string.Empty;
    public DateTimeOffset? RequiredBy { get; set; }
    public Guid? RequestedByPractitionerId { get; set; }
    public string? RequestedByName { get; set; }

    /// <summary>Pre-transfusion haemoglobin / platelet count, kept for transfusion-appropriateness audits.</summary>
    public decimal? HaemoglobinGdl { get; set; }

    public int? PlateletCount { get; set; }

    public BloodGroup PatientBloodGroup { get; set; }

    /// <summary>When the blood bank received the sample the grouping uses (recorded as it is grouped).</summary>
    public DateTimeOffset? SampleReceivedAt { get; set; }

    /// <summary>
    /// When the sample the grouping uses was drawn from the patient, and who drew it, as the tube and the form say: not
    /// when it reached the blood bank. None on groupings made before draws were recorded.
    /// </summary>
    public DateTimeOffset? SampleDrawnAt { get; set; }

    public string? SampleDrawnBy { get; set; }

    public DateTimeOffset? GroupedAt { get; set; }

    /// <summary>The officer whose grouping the request uses: the first sample's, or the new sample's after a regroup.</summary>
    public Guid? GroupedByUserId { get; set; }

    public string? GroupedByName { get; set; }

    /// <summary>
    /// The group the patient's earlier requests confirmed, when this request's sample grouped otherwise: nothing is
    /// cross-matched or issued on this request's grouping until another officer groups a new sample (<see cref="Regroup"/>).
    /// </summary>
    public BloodGroup? EarlierGroup { get; set; }

    /// <summary>The group of the first sample, kept when a new sample's grouping replaced or confirmed it.</summary>
    public BloodGroup? FirstSampleGroup { get; set; }

    /// <summary>When the first sample was drawn and who drew it, kept when a new sample's grouping replaced or confirmed it.</summary>
    public DateTimeOffset? FirstSampleDrawnAt { get; set; }

    public string? FirstSampleDrawnBy { get; set; }

    public DateTimeOffset? RegroupedAt { get; set; }

    /// <summary>
    /// The patient had no group on record when the first sample was grouped, the facility applies the two-sample rule and
    /// the request is for red cells or plasma (<see cref="TwoSampleRuleCovers"/>): group-specific blood waits for a second,
    /// separately drawn sample to confirm the group (<see cref="Regroup"/>), or for the treating doctor's emergency override
    /// (<see cref="AuthoriseSingleSampleOverride"/>).
    /// </summary>
    public bool SecondSampleRequired { get; set; }

    /// <summary>
    /// The group of a sample that disagreed with the one the request rests on while it waited for its second sample
    /// (possible wrong blood in one of the tubes): a later sample of this request, or a sample of the patient grouped on
    /// another request (<see cref="DisagreeingSampleRequestNumber"/>). With when it was grouped, when it was drawn and who
    /// drew it. The request is held, whatever a doctor authorised on the one sample, until a sample drawn separately agrees
    /// with the first.
    /// </summary>
    public BloodGroup? DisagreeingSampleGroup { get; set; }

    public DateTimeOffset? DisagreeingSampleAt { get; set; }

    /// <summary>When the disagreeing sample was drawn and who drew it, as its tube and form say (none for a grouping made before draws were recorded).</summary>
    public DateTimeOffset? DisagreeingSampleDrawnAt { get; set; }

    public string? DisagreeingSampleDrawnBy { get; set; }

    /// <summary>The patient's other request the disagreeing sample was grouped on; none when it was a sample of this request.</summary>
    public string? DisagreeingSampleRequestNumber { get; set; }

    /// <summary>The doctor who authorised group-specific blood on one sample's grouping, by name as the practitioner list has it.</summary>
    public string? SingleSampleOverrideBy { get; set; }

    public string? SingleSampleOverrideReason { get; set; }
    public DateTimeOffset? SingleSampleOverrideAt { get; set; }

    /// <summary>The doctor's own sign-in that authorised it, and their practitioner record.</summary>
    public Guid? SingleSampleOverrideByUserId { get; set; }

    public Guid? SingleSampleOverrideByPractitionerId { get; set; }

    /// <summary>The doctor who authorised uncross-matched release, by name as the practitioner list has it.</summary>
    public string? EmergencyReleaseAuthorisedBy { get; set; }

    public string? EmergencyReleaseReason { get; set; }
    public DateTimeOffset? EmergencyReleaseAt { get; set; }

    /// <summary>The doctor's own sign-in that authorised it, and their practitioner record.</summary>
    public Guid? EmergencyReleaseByUserId { get; set; }

    public Guid? EmergencyReleaseByPractitionerId { get; set; }

    public BloodRequestStatus Status { get; set; }
    public int UnitsReserved { get; set; }
    public int UnitsIssued { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsOpen => Status is not (BloodRequestStatus.Issued or BloodRequestStatus.Cancelled);

    public bool IsGrouped => GroupedAt is not null && BloodCompatibility.IsKnown(PatientBloodGroup);

    public bool EmergencyReleaseAuthorised => EmergencyReleaseAt is not null;

    /// <summary>The grouping disagrees with the patient's earlier groups and no new sample has been grouped yet.</summary>
    public bool HistoryHeld => EarlierGroup is not null && RegroupedAt is null;

    /// <summary>The patient's first grouping waits for a second sample to confirm it.</summary>
    public bool AwaitingSecondSample => SecondSampleRequired && RegroupedAt is null;

    public bool SingleSampleOverridden => SingleSampleOverrideAt is not null;

    /// <summary>
    /// A sample disagreed with the one the request rests on (a later sample of its own, or one grouped on another request
    /// of the patient), and none drawn separately has agreed with the first since.
    /// </summary>
    public bool SamplesDisagree => AwaitingSecondSample && DisagreeingSampleGroup is not null;

    /// <summary>
    /// Nothing group-specific is cross-matched or issued on the grouping: it disagrees with the patient's earlier groups, it
    /// waits for a second sample and no doctor has authorised going ahead on the one, or a sample disagreed with it.
    /// </summary>
    public bool GroupingHeld => HistoryHeld || (AwaitingSecondSample && (!SingleSampleOverridden || SamplesDisagree));

    /// <summary>
    /// The patient's group an issue may rely on: unknown while the grouping is held, so that only what an ungrouped patient
    /// may have under the doctor's emergency release goes (O Rh(D)-negative red cells, AB plasma).
    /// </summary>
    public BloodGroup GroupForIssue => GroupForIssueGiven(null);

    /// <summary>The shortest clinical reason a doctor gives for group-specific blood on one sample.</summary>
    public const int MinimumOverrideReason = 10;

    /// <summary>
    /// Whether the two-sample rule covers a request for <paramref name="component"/>: red cells and plasma (whole blood,
    /// packed red cells, fresh frozen plasma). Platelets and cryoprecipitate go on one sample's grouping: ABO-incompatible
    /// platelets are accepted practice, and they are scarce.
    /// </summary>
    public static bool TwoSampleRuleCovers(BloodComponentType component) =>
        component is BloodComponentType.WholeBlood or BloodComponentType.PRBC or BloodComponentType.FFP;

    /// <summary>
    /// Whether nothing group-specific is cross-matched or issued on the grouping, given a sample of the patient grouped on
    /// another request that disagrees with it (<paramref name="contrary"/>, from <see cref="GroupingHistory.Contrary"/>):
    /// on top of <see cref="GroupingHeld"/>, a grouping resting on one sample is held while another sample of the patient
    /// disagrees with it, whatever a doctor authorised on it.
    /// </summary>
    public bool HeldGiven(FoundGrouping? contrary) => GroupingHeld || ContraryGroup(contrary) is not null;

    /// <summary>The patient's group an issue may rely on, given <paramref name="contrary"/> (<see cref="HeldGiven"/>): unknown while held.</summary>
    public BloodGroup GroupForIssueGiven(FoundGrouping? contrary) => HeldGiven(contrary) ? BloodGroup.Unknown : PatientBloodGroup;

    /// <summary>
    /// Every group the patient's samples were found to be when they disagree on the grouping this request goes on (possible
    /// wrong blood in a tube): its sample and the patient's earlier group for a grouping held against earlier requests; its
    /// one sample, the disagreeing sample, and <paramref name="contrary"/> for one resting on one sample. Empty when the
    /// grouping is not disputed. A unit issued on the request is started at the bedside only if it suits all of them.
    /// </summary>
    public IReadOnlyList<BloodGroup> DisputedGroups(FoundGrouping? contrary)
    {
        if (!IsGrouped)
        {
            return [];
        }

        if (HistoryHeld)
        {
            return [PatientBloodGroup, EarlierGroup!.Value];
        }

        var other = ContraryGroup(contrary);
        if (!SamplesDisagree && other is null)
        {
            return [];
        }

        return new[] { PatientBloodGroup, SamplesDisagree ? DisagreeingSampleGroup : null, other }
            .OfType<BloodGroup>()
            .Where(BloodCompatibility.IsKnown)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// How far the bedside may rely on the group this request goes on, given <paramref name="contrary"/>: not grouped yet,
    /// grouped (confirmed on two samples, agreeing with the patient's group on record, or where the facility does not apply
    /// the two-sample rule), resting on one sample, or disputed by samples that disagree (<see cref="DisputedGroups"/>).
    /// </summary>
    public PatientGroupStanding StandingGiven(FoundGrouping? contrary) =>
        !IsGrouped ? PatientGroupStanding.NotGrouped
        : DisputedGroups(contrary).Count > 0 ? PatientGroupStanding.SamplesDisagree
        : AwaitingSecondSample ? PatientGroupStanding.OneSample
        : PatientGroupStanding.Grouped;

    /// <summary>
    /// Which samples disagree with the one the request rests on, as the screens and refusals say it: a later sample of its
    /// own, or one grouped on another request of the patient (recorded, or <paramref name="contrary"/>).
    /// </summary>
    public string DisagreementSummary(FoundGrouping? contrary) =>
        SamplesDisagree
            ? DisagreeingSampleRequestNumber is { } other
                ? $"A sample grouped on the patient's request {other} came out {DisagreeingSampleGroup}, this request's one sample {PatientBloodGroup}"
                : $"A later sample grouped {DisagreeingSampleGroup} but the first {PatientBloodGroup}"
            : $"A sample grouped on the patient's request {contrary?.RequestNumber} came out {ContraryGroup(contrary)}, this request's one sample {PatientBloodGroup}";

    public Result EnsureOpen() =>
        IsOpen ? Result.Success() : Error.Rule("bloodbank.request_closed", "This blood request is closed.");

    /// <summary>
    /// Records the blood bank's grouping of the patient sample, with when it was drawn and who drew it. A second grouping
    /// that disagrees with the first signals a wrong-blood-in-tube error and is refused so it can be investigated. A first
    /// grouping that disagrees with the group of the patient's earlier requests (<paramref name="earlierGroup"/>, from
    /// <see cref="GroupingHistory"/>) is recorded, and holds the request until another officer groups a new sample. A first
    /// grouping nothing on record confirms (<paramref name="needsSecondSample"/>, <see cref="GroupingHistory.NeedsSecondSample"/>)
    /// is recorded, and waits for a second sample when the request is for red cells or plasma (<see cref="TwoSampleRuleCovers"/>).
    /// A disagreement on a request waiting for its second sample is refused and also recorded (<see cref="DisagreeingSampleGroup"/>):
    /// the caller saves the request although the grouping is refused. The same group again on a request waiting for its
    /// second sample is refused too: the second sample is grouped as a new sample (<see cref="Regroup"/>), drawn separately.
    /// </summary>
    public Result RecordGrouping(BloodGroup group, StaffStamp by, BloodGroup? earlierGroup, SampleDraw draw, bool needsSecondSample)
    {
        var valid = EnsureCanGroup(group, draw);
        if (valid.IsFailure)
        {
            return valid;
        }

        if (IsGrouped && PatientBloodGroup != group)
        {
            RecordDisagreement(group, by.At, draw.DrawnAt, draw.DrawnBy, requestNumber: null);
            return Error.Rule("bloodbank.grouping_discrepancy",
                $"This sample groups as {group} but the patient was grouped as {PatientBloodGroup}. Collect a fresh sample and investigate (possible wrong blood in tube).");
        }

        if (IsGrouped && AwaitingSecondSample)
        {
            return Error.Rule("bloodbank.group_as_new_sample",
                "This request waits for its second sample: group it as a new sample (newSample), drawn separately from the first. The same sample grouped again confirms nothing.");
        }

        if (!IsGrouped)
        {
            PatientBloodGroup = group;
            SampleReceivedAt ??= by.At;
            SampleDrawnAt = draw.DrawnAt;
            SampleDrawnBy = draw.DrawnBy.Trim();
            GroupedAt = by.At;
            GroupedByUserId = by.UserId;
            GroupedByName = by.Name;
            EarlierGroup = earlierGroup is { } earlier && earlier != group ? earlier : null;
            SecondSampleRequired = needsSecondSample && EarlierGroup is null && TwoSampleRuleCovers(ComponentType);
        }

        Refresh();
        return Result.Success();
    }

    /// <summary>
    /// Another officer groups a new sample of a held request, drawn separately from the first (at another time, its
    /// collector named). For a grouping that disagreed with the patient's earlier groups, the new sample decides between
    /// them: it must agree with the first sample or with the earlier group. For a patient with no group on record it is the
    /// second sample, and must agree with the first; the one officer on duty groups it too where the facility has switched
    /// the second officer off (<paramref name="separateVerifier"/>). Its grouping replaces the request's. A group that
    /// agrees with neither is refused as a discrepancy (possible wrong blood in the tube), and the request stays held. A
    /// sample that disagrees with the one a request waiting for its second rests on is recorded before anything else is
    /// checked, whoever grouped it and whenever it was drawn (<see cref="DisagreeingSampleGroup"/>): the request is held
    /// even on a doctor's authorisation of blood on one sample, and the caller saves it although the grouping is refused.
    /// After a disagreement, the sample that decides is drawn at another time than both tubes.
    /// </summary>
    public Result Regroup(BloodGroup group, StaffStamp by, SampleDraw draw, bool separateVerifier)
    {
        var valid = EnsureCanGroup(group, draw);
        if (valid.IsFailure)
        {
            return valid;
        }

        if (!HistoryHeld && !AwaitingSecondSample)
        {
            return Error.Rule("bloodbank.regroup_not_needed",
                "A new sample is grouped here only when the request's grouping disagreed with the patient's earlier requests, or to confirm the group of a patient with none on record.");
        }

        // Possible wrong blood in a tube is not lost to the refusals below: the officer who grouped the first sample, or the
        // first tube grouped again, found it all the same.
        if (AwaitingSecondSample && group != PatientBloodGroup)
        {
            RecordDisagreement(group, by.At, draw.DrawnAt, draw.DrawnBy, requestNumber: null);
            return Error.Rule("bloodbank.grouping_discrepancy",
                $"This sample groups as {group} but the first grouped as {PatientBloodGroup}. Do not cross-match or issue: possible wrong blood in tube. Collect a fresh sample, drawn separately, and investigate.");
        }

        if (GroupedByUserId is { } firstOfficer && firstOfficer == by.UserId && (HistoryHeld || separateVerifier))
        {
            return Error.Forbidden("bloodbank.regroup_own_sample", HistoryHeld
                ? "You grouped the first sample. Another blood bank officer groups the new one."
                : "You grouped the first sample. Another blood bank officer groups the second.");
        }

        if (draw.SameDrawAs(SampleDrawnAt))
        {
            return Error.Rule("bloodbank.sample_not_separate",
                "This sample's draw time is the first sample's: the same draw confirms nothing. Group a sample drawn separately, at another time.");
        }

        if (SamplesDisagree && draw.SameDrawAs(DisagreeingSampleDrawnAt))
        {
            return Error.Rule("bloodbank.sample_not_separate",
                "This sample's draw time is that of the sample that disagreed: the same tube decides nothing. Group a fresh sample, drawn separately, at another time.");
        }

        if (HistoryHeld && group != PatientBloodGroup && group != EarlierGroup)
        {
            return Error.Rule("bloodbank.grouping_discrepancy",
                $"The new sample groups as {group}, the first as {PatientBloodGroup} and the patient's earlier requests as {EarlierGroup}. Collect another sample and investigate.");
        }

        FirstSampleGroup = PatientBloodGroup;
        FirstSampleDrawnAt = SampleDrawnAt;
        FirstSampleDrawnBy = SampleDrawnBy;
        PatientBloodGroup = group;
        SampleDrawnAt = draw.DrawnAt;
        SampleDrawnBy = draw.DrawnBy.Trim();
        SampleReceivedAt = by.At;
        GroupedAt = by.At;
        GroupedByUserId = by.UserId;
        GroupedByName = by.Name;
        RegroupedAt = by.At;
        Refresh();
        return Result.Success();
    }

    /// <summary>
    /// Uncross-matched emergency release is the treating doctor's decision, made from their own sign-in, only for emergency
    /// requests: <paramref name="doctor"/> is their account, <paramref name="practitionerId"/> their practitioner record.
    /// </summary>
    public Result AuthoriseEmergencyRelease(StaffStamp doctor, Guid practitionerId, string reason)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (Urgency != BloodRequestUrgency.Emergency)
        {
            return Error.Rule("bloodbank.not_emergency", "Uncross-matched release is only possible for emergency requests.");
        }

        if (EmergencyReleaseAuthorised)
        {
            return Error.Rule("bloodbank.release_already_authorised", $"{EmergencyReleaseAuthorisedBy ?? "A doctor"} has already authorised uncross-matched release for this request.");
        }

        EmergencyReleaseAuthorisedBy = doctor.Name;
        EmergencyReleaseByUserId = doctor.UserId;
        EmergencyReleaseByPractitionerId = practitionerId;
        EmergencyReleaseReason = reason.Trim();
        EmergencyReleaseAt = doctor.At;
        return Result.Success();
    }

    /// <summary>
    /// Group-specific blood on one sample's grouping, before a second sample confirms it, is the treating doctor's emergency
    /// decision, made from their own sign-in with the clinical reason (at least <see cref="MinimumOverrideReason"/>
    /// characters), and only on an emergency request (for others the blood bank groups the second sample first):
    /// <paramref name="doctor"/> is their account, <paramref name="practitionerId"/> their practitioner record. Only while
    /// the request waits for a second sample: a grouping that disagrees with earlier requests is decided by a new sample,
    /// and no doctor authorises blood on either of two samples that disagree, whether the other sample was this request's
    /// (<see cref="SamplesDisagree"/>) or another request's (<paramref name="contrary"/>, <see cref="GroupingHistory.Contrary"/>).
    /// </summary>
    public Result AuthoriseSingleSampleOverride(StaffStamp doctor, Guid practitionerId, string reason, FoundGrouping? contrary)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (Urgency != BloodRequestUrgency.Emergency)
        {
            return Error.Rule("bloodbank.not_emergency",
                "Group-specific blood on one sample is the emergency override, only for emergency requests: otherwise the blood bank groups the second sample first.");
        }

        var why = reason?.Trim() ?? string.Empty;
        if (why.Length < MinimumOverrideReason)
        {
            return Error.Field("reason", $"Give the clinical reason for issuing blood on one sample (at least {MinimumOverrideReason} characters).");
        }

        if (SamplesDisagree || ContraryGroup(contrary) is not null)
        {
            return Error.Rule("bloodbank.grouping_discrepancy",
                $"{DisagreementSummary(contrary)}: possible wrong blood in tube. Blood is not authorised on either; nothing group-specific goes until a fresh sample, drawn separately, groups the same as the first. In an emergency the doctor can release O Rh(D)-negative red cells or AB plasma uncross-matched.");
        }

        if (SingleSampleOverridden)
        {
            return Error.Rule("bloodbank.single_sample_override_authorised",
                $"{SingleSampleOverrideBy ?? "A doctor"} has already authorised group-specific blood on one sample for this request.");
        }

        if (HistoryHeld)
        {
            return Error.Rule("bloodbank.grouping_history_discrepancy",
                "This request's sample grouped otherwise than the patient's earlier requests: another officer groups a new sample. In an emergency the doctor can release O Rh(D)-negative red cells or AB plasma uncross-matched.");
        }

        if (!IsGrouped)
        {
            return Error.Rule("bloodbank.patient_not_grouped", "Group the patient's sample first: the override is for the group one sample found.");
        }

        if (!AwaitingSecondSample)
        {
            return Error.Rule("bloodbank.single_sample_override_not_needed", "This request does not wait for a second sample.");
        }

        SingleSampleOverrideBy = doctor.Name;
        SingleSampleOverrideByUserId = doctor.UserId;
        SingleSampleOverrideByPractitionerId = practitionerId;
        SingleSampleOverrideReason = why;
        SingleSampleOverrideAt = doctor.At;
        return Result.Success();
    }

    /// <summary>
    /// A sample of the patient grouped on another request (<paramref name="sample"/>) found otherwise than the one sample
    /// this request rests on: recorded as its disagreeing sample at <paramref name="at"/>, as a later sample of its own
    /// would be, so that nothing group-specific goes on either, whatever a doctor authorised (<see cref="SamplesDisagree"/>).
    /// Only on a request waiting for its second sample, not cancelled, whose group differs and that has no disagreement
    /// recorded yet (the first is kept); returns whether it was recorded.
    /// </summary>
    public bool RecordContrarySample(FoundGrouping sample, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (Status == BloodRequestStatus.Cancelled || DisagreeingSampleGroup is not null || ContraryGroup(sample) is null)
        {
            return false;
        }

        RecordDisagreement(sample.Group, at, sample.DrawnAt, sample.DrawnBy, sample.RequestNumber);
        return true;
    }

    /// <summary>The units reserved for the request went back to stock when it was held: none counts toward it now.</summary>
    public void OnReservationsReleased()
    {
        UnitsReserved = 0;
        Refresh();
    }

    public void OnUnitReserved()
    {
        UnitsReserved++;
        Refresh();
    }

    public void OnReservationReleased()
    {
        UnitsReserved = Math.Max(0, UnitsReserved - 1);
        Refresh();
    }

    public void OnUnitIssued(bool wasReserved)
    {
        UnitsIssued++;
        if (wasReserved)
        {
            UnitsReserved = Math.Max(0, UnitsReserved - 1);
        }

        Refresh();
    }

    public void OnUnitReturned()
    {
        UnitsIssued = Math.Max(0, UnitsIssued - 1);
        ClosedAt = null;
        Refresh();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (UnitsIssued > 0)
        {
            return Error.Rule("bloodbank.request_partly_issued", "Units have already been issued against this request; it cannot be cancelled.");
        }

        Status = BloodRequestStatus.Cancelled;
        CancelReason = reason.Trim();
        ClosedAt = now;
        UnitsReserved = 0;
        return Result.Success();
    }

    /// <summary>A grouping is recorded only on an open request, for a known group, on a sample whose collector is named.</summary>
    private Result EnsureCanGroup(BloodGroup group, SampleDraw draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (!BloodCompatibility.IsKnown(group))
        {
            return Error.Field("bloodGroup", "Enter the patient's ABO and Rh(D) group.");
        }

        return string.IsNullOrWhiteSpace(draw.DrawnBy)
            ? Error.Field("sampleDrawnBy", "Enter who drew the sample, as the tube and the form name them.")
            : Result.Success();
    }

    /// <summary>
    /// A sample grouped <paramref name="group"/> at <paramref name="at"/>, otherwise than the one a request waiting for its
    /// second sample rests on, with its draw (none for a grouping made before draws were recorded) and, for a sample grouped
    /// on another request, that request's number: the first such sample is kept, and holds the request
    /// (<see cref="SamplesDisagree"/>).
    /// </summary>
    private void RecordDisagreement(BloodGroup group, DateTimeOffset at, DateTimeOffset? drawnAt, string? drawnBy, string? requestNumber)
    {
        if (AwaitingSecondSample && DisagreeingSampleGroup is null)
        {
            DisagreeingSampleGroup = group;
            DisagreeingSampleAt = at;
            DisagreeingSampleDrawnAt = drawnAt;
            DisagreeingSampleDrawnBy = string.IsNullOrWhiteSpace(drawnBy) ? null : drawnBy.Trim();
            DisagreeingSampleRequestNumber = requestNumber;
        }
    }

    /// <summary>
    /// The group of <paramref name="contrary"/>, a sample of the patient grouped on another request, when it disagrees with
    /// the one sample this request rests on; null otherwise, and for a request that does not rest on one sample.
    /// </summary>
    private BloodGroup? ContraryGroup(FoundGrouping? contrary) =>
        AwaitingSecondSample && IsGrouped && contrary is { } other && BloodCompatibility.IsKnown(other.Group) && other.Group != PatientBloodGroup
            ? other.Group
            : null;


    private void Refresh()
    {
        if (Status == BloodRequestStatus.Cancelled)
        {
            return;
        }

        Status = UnitsIssued >= UnitsRequested ? BloodRequestStatus.Issued
            : UnitsIssued > 0 ? BloodRequestStatus.PartiallyIssued
            : UnitsReserved >= UnitsRequested ? BloodRequestStatus.Ready
            : IsGrouped ? BloodRequestStatus.InProcess
            : BloodRequestStatus.Pending;
    }
}
