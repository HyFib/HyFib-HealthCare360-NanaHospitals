namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;

/// <summary>The colour-coded categories of Schedule I of the Bio-Medical Waste Management Rules 2016.</summary>
public enum WasteCategory
{
    /// <summary>Anatomical, soiled, expired or discarded medicines, chemical, microbiology and cytotoxic waste: incinerated.</summary>
    Yellow = 0,

    /// <summary>Contaminated recyclable plastics (tubing, bottles, catheters, gloves): autoclaved and recycled.</summary>
    Red,

    /// <summary>Waste sharps, metals included, in translucent puncture-proof containers.</summary>
    White,

    /// <summary>Broken or discarded glassware, and metallic body implants, in cardboard boxes with blue marking.</summary>
    Blue,
}

public enum WasteBagStatus
{
    /// <summary>Weighed and labelled at the point of generation, in the hospital's storage area.</summary>
    Collected = 0,

    /// <summary>Handed over to the common bio-medical waste treatment facility (CBWTF).</summary>
    HandedOver,

    /// <summary>Labelled or weighed in error; kept with the reason.</summary>
    Voided,
}

/// <summary>
/// One bag or container of bio-medical waste, bar-coded at the point of generation (BMW Rules 2016 as amended in 2018),
/// weighed and kept in the storage area until the CBWTF collects it. Untreated waste is not kept beyond 48 hours.
/// </summary>
public sealed class WasteBag : AuditableEntity, IFacilityScoped
{
    public const decimal MaxWeightKg = 50m;

    /// <summary>Untreated waste is handed over within this time of its generation (Rule 8).</summary>
    public static readonly TimeSpan MaxStorage = TimeSpan.FromHours(48);

    public Guid FacilityId { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public WasteCategory Category { get; set; }
    public Guid? DepartmentId { get; set; }
    public string Source { get; set; } = string.Empty;
    public decimal WeightKg { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
    public Guid? CollectedBy { get; set; }
    public string CollectedByName { get; set; } = string.Empty;
    public WasteBagStatus Status { get; private set; }
    public Guid? HandoverId { get; private set; }
    public string? VoidReason { get; private set; }

    public bool IsOverdue(DateTimeOffset now) => Status == WasteBagStatus.Collected && now - CollectedAt > MaxStorage;

    public static Result Validate(decimal weightKg, DateTimeOffset collectedAt, DateTimeOffset now)
    {
        if (weightKg <= 0 || weightKg > MaxWeightKg)
        {
            return Error.Field("weightKg", $"Weigh the bag: more than 0 and at most {MaxWeightKg} kg.");
        }

        if (collectedAt > now.AddMinutes(5))
        {
            return Error.Field("collectedAt", "The bag cannot be collected in the future.");
        }

        return now - collectedAt > TimeSpan.FromDays(7)
            ? Error.Field("collectedAt", "Record the bag the day it is collected; one older than a week is entered in error.")
            : Result.Success();
    }

    public Result HandOver(Guid handoverId)
    {
        if (Status != WasteBagStatus.Collected)
        {
            return Error.Rule("bmw.bag_not_pending", $"Bag {Barcode} is {(Status == WasteBagStatus.Voided ? "voided" : "already handed over")}.");
        }

        Status = WasteBagStatus.HandedOver;
        HandoverId = handoverId;
        return Result.Success();
    }

    public Result Void(string reason)
    {
        if (Status != WasteBagStatus.Collected)
        {
            return Error.Rule("bmw.bag_not_pending", $"Bag {Barcode} has left the hospital or is already voided.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Say why the bag is voided.");
        }

        Status = WasteBagStatus.Voided;
        VoidReason = reason.Trim();
        return Result.Success();
    }
}

/// <summary>
/// Bags handed over to the CBWTF's vehicle, against the facility's receipt: the record the State Pollution Control
/// Board inspects against the CBWTF's own. Bags kept beyond 48 hours are counted, so the breach is on record.
/// </summary>
public sealed class WasteHandover : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTimeOffset HandedOverAt { get; set; }
    public string Cbwtf { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public string ReceivedBy { get; set; } = string.Empty;
    public string? ReceiptNumber { get; set; }
    public Guid? HandedOverBy { get; set; }
    public string HandedOverByName { get; set; } = string.Empty;
    public int BagCount { get; set; }
    public decimal TotalKg { get; set; }

    /// <summary>Bags kept beyond 48 hours before this handover.</summary>
    public int LateBags { get; set; }
}
