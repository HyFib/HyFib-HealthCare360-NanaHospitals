namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>The checks a unit's issue rests on, and who made each.</summary>
/// <param name="GroupedBy">Who grouped the patient's sample the request uses.</param>
/// <param name="CrossMatchedBy">Who made the valid compatible cross-match the issue relies on, for red cells.</param>
/// <param name="CrossMatched">The issue relies on a cross-match (red cells issued normally).</param>
/// <param name="EmergencyRelease">The unit goes uncross-matched under a doctor's emergency release.</param>
/// <param name="ReleaseAuthorisedBy">The doctor's account that authorised the release.</param>
/// <param name="OneSampleAuthorisedBy">The doctor's account that authorised group-specific blood on one sample (the single-sample override), while the issue goes on that one sample.</param>
public sealed record IssueChecks(
    Guid? GroupedBy, Guid? CrossMatchedBy, bool CrossMatched, bool EmergencyRelease, Guid? ReleaseAuthorisedBy, Guid? OneSampleAuthorisedBy = null);

/// <summary>
/// Grouping, cross-match and issue are not all one person (maker-checker, MAKER-CHECKER.md slice 3). An issue that rests
/// only on checks the issuer made alone is refused (<see cref="OwnChecks"/>, 403), unless the facility has switched the
/// second officer off (<c>bloodbank.separateVerifier</c>): then it is issued, audited at Warning and listed for the medical
/// officer's review.
/// </summary>
public static class IssueSeparation
{
    public const string OwnChecks = "bloodbank.issue_own_checks";

    /// <summary>
    /// Why the issue would rest on <paramref name="issuer"/>'s word alone, or null when someone else made one of its checks:
    /// red cells, when the issuer both grouped the patient and cross-matched the unit; plasma, platelets and cryoprecipitate,
    /// which go on the group alone, when the issuer grouped the patient; an uncross-matched emergency release, when the
    /// issuer is the doctor who authorised it; group-specific blood on one sample, when the issuer is the doctor who authorised
    /// that (a blood bank medical officer who is also a doctor may not group the sample, authorise blood on it and issue it).
    /// </summary>
    public static string? OwnChecksReason(Guid issuer, IssueChecks checks)
    {
        if (checks.EmergencyRelease)
        {
            return checks.ReleaseAuthorisedBy == issuer
                ? "You authorised this uncross-matched release as the doctor: another blood bank officer issues the unit."
                : null;
        }

        if (checks.OneSampleAuthorisedBy is { } doctor && doctor == issuer)
        {
            return "You authorised blood on this one sample as the doctor: another blood bank officer issues the unit.";
        }

        if (checks.CrossMatched)
        {
            return checks.GroupedBy == issuer && checks.CrossMatchedBy == issuer
                ? "You grouped the patient and cross-matched this unit. Another blood bank officer issues it, or cross-matches it again first."
                : null;
        }

        return checks.GroupedBy == issuer
            ? "You grouped the patient, and this component is issued on the group alone. Another blood bank officer issues it."
            : null;
    }
}

/// <summary>
/// The patient's group from earlier requests (BB-06): a new grouping is compared with the groups the blood bank confirmed
/// on the patient's other requests, as well as with this request's own first sample (<see cref="BloodRequest.RecordGrouping"/>).
/// </summary>
public static class GroupingHistory
{
    /// <summary>
    /// The earlier confirmed group a new grouping disagrees with, most recent first (<paramref name="earlier"/> in that
    /// order), or null when it agrees with all of them or there are none.
    /// </summary>
    public static BloodGroup? Disagreement(BloodGroup group, IEnumerable<BloodGroup> earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        foreach (var confirmed in earlier)
        {
            if (BloodCompatibility.IsKnown(confirmed) && confirmed != group)
            {
                return confirmed;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a first grouping waits for a second sample to confirm it (the two-sample rule,
    /// <c>bloodbank.twoSampleRule</c>): while <paramref name="twoSampleRule"/> applies, unless one of the patient's earlier
    /// groupings was made on a sample drawn at another time than <paramref name="draw"/>, or before draw times were
    /// recorded. An earlier grouping of the same draw (one tube sent with two requests) is the same sample, and confirms
    /// nothing. While the facility has a second officer (<paramref name="separateVerifier"/>), an earlier grouping made by the
    /// officer grouping this sample (<paramref name="officer"/>) confirms nothing either: as within one request, another
    /// officer groups the second sample.
    /// </summary>
    public static bool NeedsSecondSample(
        bool twoSampleRule, SampleDraw draw, IEnumerable<EarlierGrouping> earlier, Guid? officer = null, bool separateVerifier = false)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(earlier);
        return twoSampleRule && !earlier.Any(e => BloodCompatibility.IsKnown(e.Group) && !draw.SameDrawAs(e.DrawnAt)
                                                  && !(separateVerifier && officer is { } own && e.GroupedBy == own));
    }

    /// <summary>
    /// What an earlier request of the patient puts on record, with when each sample was drawn: the grouping it goes on
    /// (<paramref name="group"/>, drawn <paramref name="drawnAt"/>), and the first sample's too when a new sample agreed with
    /// it (<paramref name="regrouped"/>: the group confirmed on two samples), so that its second tube grouped again for
    /// another request is not taken for the only sample on record. A first sample a new sample set aside confirms nothing,
    /// and a request whose samples disagree (<paramref name="disputed"/>) puts nothing on record. <paramref name="groupedBy"/>
    /// is the officer whose grouping the request uses; the first sample of a confirmed group has none on record (the group
    /// rests on two samples, so it stands whoever groups another).
    /// </summary>
    public static IReadOnlyList<EarlierGrouping> OnRecord(
        BloodGroup group, DateTimeOffset? drawnAt, BloodGroup? firstSampleGroup, DateTimeOffset? firstSampleDrawnAt, bool regrouped, bool disputed,
        Guid? groupedBy = null) =>
        disputed ? []
        : regrouped && firstSampleGroup == group ? [new EarlierGrouping(group, drawnAt, groupedBy), new EarlierGrouping(group, firstSampleDrawnAt)]
        : [new EarlierGrouping(group, drawnAt, groupedBy)];

    /// <summary>
    /// Every sample the blood bank grouped on <paramref name="request"/>, with its draw and the number of the request it was
    /// grouped on: the sample its grouping goes on, its first sample when a new one replaced or confirmed it, and a sample
    /// recorded as disagreeing with it (grouped on that request, or on another). None before it is grouped.
    /// </summary>
    public static IReadOnlyList<FoundGrouping> SamplesOf(BloodRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsGrouped)
        {
            return [];
        }

        var samples = new List<FoundGrouping> { new(request.PatientBloodGroup, request.SampleDrawnAt, request.SampleDrawnBy, request.RequestNumber) };
        if (request.FirstSampleGroup is { } first)
        {
            samples.Add(new FoundGrouping(first, request.FirstSampleDrawnAt, request.FirstSampleDrawnBy, request.RequestNumber));
        }

        if (request.DisagreeingSampleGroup is { } disagreeing)
        {
            samples.Add(new FoundGrouping(disagreeing, request.DisagreeingSampleDrawnAt, request.DisagreeingSampleDrawnBy,
                request.DisagreeingSampleRequestNumber ?? request.RequestNumber));
        }

        return samples;
    }

    /// <summary>
    /// The first of the samples grouped on the patient's other requests (<paramref name="found"/>, from <see cref="SamplesOf"/>)
    /// that disagrees with <paramref name="group"/>, the one sample a request waiting for its second rests on; null when
    /// none does. Such a request is held while it does, whatever a doctor authorised (<see cref="BloodRequest.HeldGiven"/>):
    /// two samples of the patient that disagree mean one of the tubes may hold another patient's blood. A sample from the
    /// same draw counts too: one tube that groups two ways is no more to be relied on.
    /// </summary>
    public static FoundGrouping? Contrary(BloodGroup group, IEnumerable<FoundGrouping> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        return found.FirstOrDefault(f => BloodCompatibility.IsKnown(f.Group) && f.Group != group);
    }
}

/// <summary>When a sample was drawn from the patient and who drew it, as the tube and the request form say.</summary>
/// <param name="DrawnAt">When it was drawn, not when it reached the blood bank.</param>
/// <param name="DrawnBy">The collector named on the tube and the form.</param>
public sealed record SampleDraw(DateTimeOffset DrawnAt, string DrawnBy)
{
    /// <summary>
    /// Whether a sample drawn at <paramref name="drawnAt"/> is this same draw: drawn in the same minute, as tubes and forms
    /// write the time. An unrecorded draw time is never this draw.
    /// </summary>
    public bool SameDrawAs(DateTimeOffset? drawnAt) => drawnAt is { } other && Minute(other) == Minute(DrawnAt);

    private static DateTimeOffset Minute(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }
}

/// <summary>
/// A group the blood bank found on another request of the patient, when that sample was drawn (null before draw times were
/// recorded) and the officer who grouped it (null when not on record).
/// </summary>
public sealed record EarlierGrouping(BloodGroup Group, DateTimeOffset? DrawnAt, Guid? GroupedBy = null);

/// <summary>
/// A sample of the patient the blood bank grouped: its group, when it was drawn and who drew it (null before draws were
/// recorded), and the number of the request it was grouped on.
/// </summary>
public sealed record FoundGrouping(BloodGroup Group, DateTimeOffset? DrawnAt, string? DrawnBy, string RequestNumber);
