using System.Globalization;

namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

public enum DietOrderStatus
{
    Active = 0,
    Stopped,
    Completed,
}

public enum DietOrderSource
{
    Doctor = 0,
    Dietitian,
    Nurse,

    /// <summary>Created from a clinical order (ServiceOrderPlaced, category Dietary).</summary>
    ClinicalOrder,
}

/// <summary>Food preference, which Indian hospital kitchens plan and cook separately.</summary>
public enum FoodPreference
{
    Vegetarian = 0,
    NonVegetarian,
    Eggetarian,
    Jain,
}

/// <summary>
/// A patient's diet prescription for an admission (DIET-01). One order is active per admission: a new order supersedes
/// the previous one, so the kitchen never receives two diets for the same patient. Orders created from clinical orders
/// without an exact diet match, or without known preferences, are flagged for the dietitian's review.
/// </summary>
public sealed class DietOrder : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid DietTypeId { get; set; }
    public FoodPreference Preference { get; set; }
    public List<string> Restrictions { get; set; } = [];
    public string? AllergyNotes { get; set; }
    public string? Instructions { get; set; }
    public int? FluidRestrictionMl { get; set; }
    public int? CalorieTargetKcal { get; set; }
    public MealSlot MealSlots { get; set; } = Dietary.MealSlots.Standard;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DietOrderStatus Status { get; set; }
    public DietOrderSource Source { get; set; }
    public Guid? ServiceOrderItemId { get; set; }
    public Guid? OrderedByPractitionerId { get; set; }
    public string? OrderedByName { get; set; }
    public bool NeedsReview { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public Guid? StoppedBy { get; set; }
    public string? StopReason { get; set; }

    public bool IsEffectiveOn(DateOnly date) => Status == DietOrderStatus.Active && StartDate <= date && (EndDate is null || EndDate >= date);

    public Result Stop(Guid? userId, string reason, DateOnly lastDate, DateTimeOffset now)
    {
        if (Status != DietOrderStatus.Active)
        {
            return Error.Rule("diet.not_active", "This diet order is no longer active.");
        }

        Close(DietOrderStatus.Stopped, lastDate, reason.Trim(), userId, now);
        return Result.Success();
    }

    /// <summary>A new order for the same admission replaces this one from <paramref name="newStart"/>.</summary>
    public void Supersede(DateOnly newStart, DateTimeOffset now)
    {
        if (Status == DietOrderStatus.Active)
        {
            Close(DietOrderStatus.Stopped, newStart.AddDays(-1),
                string.Create(CultureInfo.InvariantCulture, $"Superseded by a new diet order from {newStart:dd-MM-yyyy}."), null, now);
        }
    }

    public void CompleteOnDischarge(DateOnly dischargeDate, DateTimeOffset now)
    {
        if (Status == DietOrderStatus.Active)
        {
            Close(DietOrderStatus.Completed, dischargeDate, "Patient discharged.", null, now);
        }
    }

    public Result MarkReviewed(Guid? userId, DateTimeOffset now)
    {
        if (!NeedsReview)
        {
            return Error.Rule("diet.no_review", "This diet order does not need a review.");
        }

        NeedsReview = false;
        ReviewedAt = now;
        ReviewedBy = userId;
        return Result.Success();
    }

    private void Close(DietOrderStatus status, DateOnly lastDate, string reason, Guid? userId, DateTimeOffset now)
    {
        Status = status;
        EndDate = lastDate < StartDate ? StartDate : lastDate;
        StoppedAt = now;
        StoppedBy = userId;
        StopReason = reason;
    }
}
