using System.Globalization;

namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

public enum MealDeliveryStatus
{
    Planned = 0,
    Delivered,

    /// <summary>Not served: the patient was away, refused or kept nil by mouth at the bedside.</summary>
    NotTaken,

    /// <summary>Served and returned uneaten.</summary>
    Returned,

    Cancelled,
}

/// <summary>
/// One patient's meal for one round on one day: the line of the ward-wise meal plan (DIET-02), counted into the kitchen
/// production list (DIET-03) and tracked through delivery (DIET-04). Diet details are copied when planned, so later order
/// changes never rewrite what the kitchen was asked to cook. High volume, so not row-audited.
/// </summary>
[NotAudited]
public sealed class PlannedMeal : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public DateOnly PlanDate { get; set; }
    public MealSlot Slot { get; set; }
    public Guid DietOrderId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? WardId { get; set; }
    public string? BedLabel { get; set; }
    public Guid DietTypeId { get; set; }
    public string DietCode { get; set; } = string.Empty;
    public string? DietName { get; set; }
    public FoodPreference Preference { get; set; }
    public List<string> Restrictions { get; set; } = [];
    public string? AllergyNotes { get; set; }
    public string? Instructions { get; set; }
    public MealDeliveryStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public Guid? DeliveredBy { get; set; }
    public string? DeliveredByName { get; set; }
    public string? Remarks { get; set; }
    public uint Version { get; set; }

    public Result RecordDelivery(MealDeliveryStatus outcome, string? remarks, Guid? userId, string? userName, DateTimeOffset now)
    {
        var check = MealDeliveryRules.Validate(Status, outcome);
        if (check.IsFailure)
        {
            return check;
        }

        Status = outcome;
        DeliveredAt = outcome == MealDeliveryStatus.Returned ? DeliveredAt ?? now : now;
        DeliveredBy = userId;
        DeliveredByName = userName;
        Remarks = string.IsNullOrWhiteSpace(remarks) ? Remarks : remarks.Trim();
        return Result.Success();
    }

    public void Cancel(string reason)
    {
        if (Status == MealDeliveryStatus.Planned)
        {
            Status = MealDeliveryStatus.Cancelled;
            Remarks = reason;
        }
    }
}

/// <summary>Delivery transitions: Planned → Delivered or NotTaken; Delivered → Returned. Cancelled meals are final.</summary>
public static class MealDeliveryRules
{
    public static Result Validate(MealDeliveryStatus current, MealDeliveryStatus outcome)
    {
        if (current == MealDeliveryStatus.Cancelled)
        {
            return Error.Rule("meal.cancelled", "This meal was cancelled (diet changed or patient discharged).");
        }

        var allowed = (current, outcome) is (MealDeliveryStatus.Planned, MealDeliveryStatus.Delivered or MealDeliveryStatus.NotTaken)
            or (MealDeliveryStatus.Delivered, MealDeliveryStatus.Returned);
        return allowed
            ? Result.Success()
            : Error.Rule("meal.invalid_transition",
                string.Create(CultureInfo.InvariantCulture, $"A meal that is {current} cannot be marked {outcome}."));
    }
}
