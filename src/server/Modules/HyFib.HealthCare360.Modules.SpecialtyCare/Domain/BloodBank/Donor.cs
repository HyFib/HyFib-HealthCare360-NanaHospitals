namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// A blood donor (BB-01). Shared across the hospital group (not facility-scoped) so that a deferral recorded at one
/// branch protects every branch. Donors are not patients: their data is visible only to blood bank staff.
/// </summary>
public sealed class Donor : AuditableEntity
{
    public string DonorNumber { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string FullName { get; set; } = string.Empty;
    public Gender Gender { get; set; }
    public DateOnly DateOfBirth { get; set; }

    /// <summary>Self-reported until the blood bank groups a donation; then <see cref="BloodGroupConfirmed"/> is set.</summary>
    public BloodGroup BloodGroup { get; set; }

    public bool BloodGroupConfirmed { get; set; }

    /// <summary>E.164 mobile; needed to recall the donor if a screening test is reactive (NBTC donor notification).</summary>
    public string Phone { get; set; } = string.Empty;

    public string? Email { get; set; }
    public Address Address { get; set; } = new();
    public string? Occupation { get; set; }

    /// <summary>Donor agreed to be informed of abnormal test results and counselled.</summary>
    public bool ConsentToNotify { get; set; } = true;

    public int DonationCount { get; set; }
    public DateTimeOffset? LastDonationAt { get; set; }
    public DateOnly? DeferredUntil { get; set; }
    public bool IsPermanentlyDeferred { get; set; }
    public string? DeferralReason { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsDeferredOn(DateOnly date) => IsPermanentlyDeferred || (DeferredUntil is { } until && until > date);

    public void SetName(string firstName, string? lastName)
    {
        FirstName = firstName.Trim();
        LastName = string.IsNullOrWhiteSpace(lastName) ? null : lastName.Trim();
        FullName = LastName is null ? FirstName : $"{FirstName} {LastName}";
    }

    /// <summary>Deferrals only ever get stricter automatically: a later, shorter deferral never shortens an existing one.</summary>
    public void Defer(DateOnly? until, bool permanent, string reason)
    {
        if (permanent)
        {
            IsPermanentlyDeferred = true;
            DeferredUntil = null;
        }
        else if (!IsPermanentlyDeferred && until is { } date && (DeferredUntil is null || date > DeferredUntil))
        {
            DeferredUntil = date;
        }

        DeferralReason = reason.Trim();
    }

    public void RecordDonation(DateTimeOffset collectedAt)
    {
        DonationCount++;
        LastDonationAt = collectedAt;
    }

    /// <summary>
    /// Records the lab-confirmed group. A different group from an earlier confirmed donation is a labelling or
    /// sampling error that must be investigated before any unit is released.
    /// </summary>
    public Result ConfirmBloodGroup(BloodGroup group)
    {
        if (BloodGroupConfirmed && BloodGroup != group)
        {
            return Error.Rule("bloodbank.donor_group_discrepancy",
                $"This donation groups as {group} but the donor was previously confirmed as {BloodGroup}. Investigate before releasing any unit.");
        }

        BloodGroup = group;
        BloodGroupConfirmed = true;
        return Result.Success();
    }
}
