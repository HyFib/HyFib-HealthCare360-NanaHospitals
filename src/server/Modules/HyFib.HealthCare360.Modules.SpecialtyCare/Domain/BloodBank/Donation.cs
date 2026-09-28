namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// One blood collection (BB-02/03). The bag number is the barcode printed on the primary bag; every component made
/// from it inherits the number with a suffix. Grouping and TTI results are recorded once on the donation and then
/// copied to its components.
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

    /// <summary>Records grouping and TTI results. Results can be completed later (pending markers) but never changed once final.</summary>
    public Result RecordLabResults(BloodGroup group, TtiPanel tti, DateTimeOffset now)
    {
        if (!BloodCompatibility.IsKnown(group))
        {
            return Error.Field("bloodGroup", "Enter the ABO and Rh(D) group of the donation.");
        }

        if (Tti.IsComplete)
        {
            return Error.Rule("bloodbank.results_final", "Screening results for this donation are complete and cannot be changed.");
        }

        BloodGroup = group;
        Tti = tti;
        LabResultsAt = now;
        if (tti.AnyReactive)
        {
            Status = DonationStatus.Discarded;
        }

        return Result.Success();
    }
}
