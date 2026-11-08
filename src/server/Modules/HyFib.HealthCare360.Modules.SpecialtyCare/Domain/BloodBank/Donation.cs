using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// One blood collection (BB-02/03). The bag number is the barcode printed on the primary bag; every component made
/// from it inherits the number with a suffix. Grouping and TTI results are recorded once on the donation and then
/// copied to its components. Results that clear the donation release nothing by themselves: a second blood bank officer
/// verifies them (<see cref="Verify"/>), and only then do the units leave quarantine (Drugs and Cosmetics Rules, Schedule F
/// Part XII-B; NBTC standards).
/// </summary>
public sealed class Donation : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid DonorId { get; set; }
    public Guid ScreeningId { get; set; }
    public string BagNumber { get; set; } = string.Empty;
    public BloodBagType BagType { get; set; }
    public int VolumeMl { get; set; }
    public DonationType Type { get; set; }

    /// <summary>Patient for whom a replacement donor donated (credited on the patient's blood account).</summary>
    public Guid? ReplacementForPatientId { get; set; }

    public DateTimeOffset CollectedAt { get; set; }
    public string? CollectedByName { get; set; }
    public DonationStatus Status { get; set; }

    /// <summary>Lab-confirmed ABO/Rh group of the donation (Unknown until grouped).</summary>
    public BloodGroup BloodGroup { get; set; }

    public TtiPanel Tti { get; set; } = new();
    public DateTimeOffset? LabResultsAt { get; set; }

    /// <summary>The officer who last entered the grouping and TTI results: until they are verified, only they correct them.</summary>
    public Guid? LabResultsByUserId { get; set; }

    public string? LabResultsByName { get; set; }

    /// <summary>
    /// When the results were verified and the units released. Null while they wait, and for a reactive donation, which is
    /// discarded on entry. Donations released before verification was required have the time with no officer.
    /// </summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    public Guid? VerifiedByUserId { get; set; }
    public string? VerifiedByName { get; set; }

    /// <summary>
    /// The officer who entered the results verified them too, as a facility with one officer on duty allows
    /// (<c>bloodbank.separateVerifier</c> off): released on one officer's word, for the medical officer's review.
    /// </summary>
    public bool VerifiedByEnterer { get; set; }

    public bool IsVerified => VerifiedAt is not null;

    /// <summary>Grouped with all five markers non-reactive, and waiting for a second officer before its units leave quarantine.</summary>
    public bool AwaitingVerification =>
        !IsVerified && Status != DonationStatus.Discarded && Tti.AllNonReactive && BloodCompatibility.IsKnown(BloodGroup);
    public DateTimeOffset? SeparatedAt { get; set; }

    /// <summary>Donor reaction during/after collection (vasovagal episode, haematoma…).</summary>
    public string? DonorReaction { get; set; }

    public string? Notes { get; set; }

    public Result EnsureCanSeparate()
    {
        return Status switch
        {
            DonationStatus.Separated => Error.Rule("bloodbank.already_separated", "Components have already been prepared from this donation."),
            DonationStatus.Discarded => Error.Rule("bloodbank.donation_discarded", "This donation was discarded."),
            _ => Result.Success(),
        };
    }

    public void MarkSeparated(DateTimeOffset now)
    {
        Status = DonationStatus.Separated;
        SeparatedAt = now;
    }

    /// <summary>
    /// Records grouping and TTI results. Markers still pending are completed later, by any officer. Once all five are in,
    /// only the officer who entered them corrects them, and only until they are verified. A reactive result discards the
    /// donation at once and is final.
    /// </summary>
    public Result RecordLabResults(BloodGroup group, TtiPanel tti, StaffStamp by)
    {
        if (!BloodCompatibility.IsKnown(group))
        {
            return Error.Field("bloodGroup", "Enter the ABO and Rh(D) group of the donation.");
        }

        if (IsVerified)
        {
            return Error.Rule("bloodbank.results_final", "Screening results for this donation were verified and its units released; they cannot be changed.");
        }

        if (Status == DonationStatus.Discarded)
        {
            return Error.Rule("bloodbank.results_final", "This donation was discarded on a reactive screening result; its results cannot be changed.");
        }

        if (Tti.IsComplete && LabResultsByUserId is { } enteredBy && enteredBy != by.UserId)
        {
            return Error.Forbidden("bloodbank.results_entered_by_other",
                $"{LabResultsByName ?? "Another officer"} entered these results, and only they correct them before they are verified. If they are right, verify them.");
        }

        BloodGroup = group;
        Tti = tti;
        LabResultsAt = by.At;
        LabResultsByUserId = by.UserId;
        LabResultsByName = by.Name;
        if (tti.AnyReactive)
        {
            Status = DonationStatus.Discarded;
        }

        return Result.Success();
    }

    /// <summary>
    /// A second officer verifies the results as entered, against the worksheet and the analyser's printout. While the
    /// facility asks for a second officer (<paramref name="separateVerifier"/>), the officer who entered them may not;
    /// where it does not, they may, and the release is marked for the medical officer's review.
    /// </summary>
    public Result Verify(StaffStamp by, bool separateVerifier)
    {
        if (IsVerified)
        {
            return Error.Rule("bloodbank.already_verified",
                $"The screening results of bag {BagNumber} were verified{(VerifiedByName is null ? string.Empty : $" by {VerifiedByName}")}.");
        }

        if (!AwaitingVerification)
        {
            return Error.Rule("bloodbank.nothing_to_verify",
                "Only results with the group and all five markers non-reactive are verified. Enter the results first; a reactive donation is discarded.");
        }

        var own = LabResultsByUserId is { } enteredBy && enteredBy == by.UserId;
        if (own && separateVerifier)
        {
            return Error.Forbidden("bloodbank.verify_own_results",
                "You entered these results. Another blood bank officer verifies them before the units leave quarantine.");
        }

        VerifiedAt = by.At;
        VerifiedByUserId = by.UserId;
        VerifiedByName = by.Name;
        VerifiedByEnterer = own;
        return Result.Success();
    }
}
