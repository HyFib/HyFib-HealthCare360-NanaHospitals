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

    /// <summary>The bag's weight on the scale at the handover, before the CBWTF's driver (Rule 4(e): weighed and recorded per bag).</summary>
    public decimal? HandoverWeightKg { get; private set; }

    public string? VoidReason { get; private set; }

    /// <summary>How far the weight at handover may differ from the weight where the bag was filled before it is flagged.</summary>
    public static decimal Tolerance(decimal weightKg) => Math.Max(0.1m, Math.Round(weightKg * 0.05m, 3));

    /// <summary>The weight at handover less the weight where it was filled, once handed over.</summary>
    public decimal? WeightDifferenceKg => HandoverWeightKg is { } at ? at - WeightKg : null;

    /// <summary>Whether the weight at handover differs from the weight where it was filled by more than <see cref="Tolerance"/>: a bag opened, added to or swapped.</summary>
    public bool WeightMismatch => WeightDifferenceKg is { } difference && Math.Abs(difference) > Tolerance(WeightKg);

    /// <summary>The colour of the bag or container (Schedule I), as printed on the label.</summary>
    public static string Colour(WasteCategory category) => category switch
    {
        WasteCategory.Yellow => "Yellow bag",
        WasteCategory.Red => "Red bag",
        WasteCategory.White => "White translucent puncture-proof container",
        _ => "Cardboard box with blue marking",
    };

    /// <summary>The symbol the label carries (Schedule IV): cytotoxic for yellow (cytotoxic waste goes in yellow), biohazard otherwise.</summary>
    public static string Symbol(WasteCategory category) => category == WasteCategory.Yellow ? "Biohazard / Cytotoxic" : "Biohazard";

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

    /// <summary>The bag leaves with the CBWTF, weighed again on the scale at the handover.</summary>
    public Result HandOver(Guid handoverId, decimal weightKg)
    {
        if (Status != WasteBagStatus.Collected)
        {
            return Error.Rule("bmw.bag_not_pending", $"Bag {Barcode} is {(Status == WasteBagStatus.Voided ? "voided" : "already handed over")}.");
        }

        if (weightKg <= 0 || weightKg > MaxWeightKg)
        {
            return Error.Field("bags", $"Weigh bag {Barcode} at the handover: more than 0 and at most {MaxWeightKg} kg.");
        }

        Status = WasteBagStatus.HandedOver;
        HandoverId = handoverId;
        HandoverWeightKg = Math.Round(weightKg, 3);
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

    /// <summary>The bags' weights on the scale at the handover (what the CBWTF signs for).</summary>
    public decimal TotalKg { get; set; }

    /// <summary>Bags whose weight at handover differs from the weight where they were filled beyond the tolerance.</summary>
    public int MismatchedBags { get; set; }

    /// <summary>Bags kept beyond 48 hours before this handover.</summary>
    public int LateBags { get; set; }
}

/// <summary>What happened, as Form I (Rule 4(o), 5(i) and 15(2)) asks: the kind of accident.</summary>
public enum WasteAccidentKind
{
    NeedleStick = 0,
    SharpsInjury,
    Spill,
    Exposure,
    Fire,
    SegregationFailure,
    TransportAccident,
    Other,
}

/// <summary>
/// An accident in handling bio-medical waste, reported on Form I of the BMW Management Rules 2016 to the prescribed
/// authority: what happened and in what sequence, the waste involved, who was affected and how, what was done at once,
/// to lessen its effects and to keep it from happening again. The annual report (Form IV) counts them.
/// </summary>
public sealed class WasteAccident : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public WasteAccidentKind Kind { get; set; }
    public string Place { get; set; } = string.Empty;

    /// <summary>The sequence of events leading to the accident.</summary>
    public string Sequence { get; set; } = string.Empty;

    public WasteCategory? Category { get; set; }

    /// <summary>The waste involved, in words ("a used needle from the dressing trolley").</summary>
    public string WasteInvolved { get; set; } = string.Empty;

    public int PersonsAffected { get; set; }

    /// <summary>The assessment of the effects on people's health and on the environment.</summary>
    public string Effects { get; set; } = string.Empty;

    public string EmergencyMeasures { get; set; } = string.Empty;
    public string? StepsToAlleviate { get; set; }
    public string? StepsToPrevent { get; set; }

    /// <summary>When the prescribed authority (the State Pollution Control Board) was told, if it was.</summary>
    public DateTimeOffset? AuthorityInformedAt { get; set; }

    public DateTimeOffset ReportedAt { get; set; }
    public Guid? ReportedBy { get; set; }
    public string ReportedByName { get; set; } = string.Empty;
    public string? ReportedByDesignation { get; set; }
}
