namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>The checks a unit's issue rests on, and who made each.</summary>
/// <param name="GroupedBy">Who grouped the patient's sample the request uses.</param>
/// <param name="CrossMatchedBy">Who made the valid compatible cross-match the issue relies on, for red cells.</param>
/// <param name="CrossMatched">The issue relies on a cross-match (red cells issued normally).</param>
/// <param name="EmergencyRelease">The unit goes uncross-matched under a doctor's emergency release.</param>
/// <param name="ReleaseAuthorisedBy">The doctor's account that authorised the release.</param>
public sealed record IssueChecks(Guid? GroupedBy, Guid? CrossMatchedBy, bool CrossMatched, bool EmergencyRelease, Guid? ReleaseAuthorisedBy);

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
    /// issuer is the doctor who authorised it.
    /// </summary>
    public static string? OwnChecksReason(Guid issuer, IssueChecks checks)
    {
        if (checks.EmergencyRelease)
        {
            return checks.ReleaseAuthorisedBy == issuer
                ? "You authorised this uncross-matched release as the doctor: another blood bank officer issues the unit."
                : null;
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
}
