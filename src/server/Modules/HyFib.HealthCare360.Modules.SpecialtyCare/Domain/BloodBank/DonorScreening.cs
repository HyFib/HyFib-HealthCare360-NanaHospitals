namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// Pre-donation screening (BB-02): measurements, questionnaire and the eligibility decision. A donation must be
/// collected against an eligible screening done the same session, and each screening can be used only once.
/// </summary>
public sealed class DonorScreening : AuditableEntity, IFacilityScoped
{
    /// <summary>A screening is valid for the donation session it was done for.</summary>
    public static readonly TimeSpan ValidFor = TimeSpan.FromHours(12);

    public Guid FacilityId { get; set; }
    public Guid DonorId { get; set; }
    public DateTimeOffset ScreenedAt { get; set; }
    public string? ScreenedByName { get; set; }
    public decimal HaemoglobinGdl { get; set; }
    public decimal WeightKg { get; set; }
    public int Systolic { get; set; }
    public int Diastolic { get; set; }
    public int Pulse { get; set; }
    public decimal TemperatureC { get; set; }
    public List<ScreeningAnswer> Answers { get; set; } = [];
    public DonorEligibilityOutcome Outcome { get; set; }
    public List<string> Reasons { get; set; } = [];
    public DateOnly? DeferredUntil { get; set; }
    public int? MaxBagVolumeMl { get; set; }

    /// <summary>Set when a donation is collected against this screening.</summary>
    public Guid? DonationId { get; set; }

    public Result EnsureUsableForDonation(int volumeMl, DateTimeOffset now)
    {
        if (Outcome != DonorEligibilityOutcome.Eligible)
        {
            return Error.Rule("bloodbank.donor_not_eligible", "The donor was not found eligible at screening.");
        }

        if (DonationId is not null)
        {
            return Error.Rule("bloodbank.screening_used", "This screening has already been used for a donation.");
        }

        if (now - ScreenedAt > ValidFor)
        {
            return Error.Rule("bloodbank.screening_stale", "The screening is older than 12 hours; screen the donor again.");
        }

        if (volumeMl is not (350 or 450))
        {
            return Error.Field("volumeMl", "Collect 350 ml or 450 ml.");
        }

        return volumeMl > (MaxBagVolumeMl ?? 350)
            ? Error.Rule("bloodbank.volume_not_allowed", $"A 450 ml collection needs a donor weight of at least {DonorEligibility.MinWeightFor450Kg} kg; collect 350 ml.")
            : Result.Success();
    }
}
