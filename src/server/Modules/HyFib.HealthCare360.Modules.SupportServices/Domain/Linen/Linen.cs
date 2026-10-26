namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Linen;

/// <summary>A kind of linen: bed sheet, draw sheet, pillow cover, patient gown, OT gown, towel, blanket.</summary>
public sealed class LinenItem : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>How many washes a piece is expected to last; the laundry's wash count is measured against it.</summary>
    public int LifeWashes { get; set; } = 150;

    /// <summary>Pieces washed in all, for the average washes a piece has had.</summary>
    public int Washes { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Where linen is: clean in the linen store, in use on a ward, or soiled at the laundry.</summary>
public enum LinenLocation
{
    Store = 0,
    Ward,
    Laundry,
}

/// <summary>
/// How many pieces of an item are at a place. Soiled linen from an isolation room or soaked in blood or body fluids is
/// kept apart at the laundry (infected, in a red bag) and is washed only by thermal disinfection.
/// </summary>
public sealed class LinenHolding : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid LinenItemId { get; set; }
    public LinenLocation Location { get; set; }

    /// <summary>The ward, for linen in use on a ward.</summary>
    public Guid? WardId { get; set; }

    /// <summary>Infected soiled linen at the laundry.</summary>
    public bool Infected { get; set; }

    public int Quantity { get; set; }

    /// <summary>The ward's par level: the clean pieces it should hold.</summary>
    public int Par { get; set; }

    public Result Take(int quantity, string itemName, string from)
    {
        if (quantity <= 0)
        {
            return Error.Field("lines", "Count more than nothing.");
        }

        if (quantity > Quantity)
        {
            return Error.Rule("linen.short", $"Only {Quantity} {itemName} at the {from}; {quantity} counted.");
        }

        Quantity -= quantity;
        return Result.Success();
    }
}

public enum LinenMovementKind
{
    /// <summary>New linen into the store.</summary>
    Received = 0,

    /// <summary>Clean linen from the store to a ward.</summary>
    Issued,

    /// <summary>Soiled linen from a ward to the laundry, counted at the ward.</summary>
    SoiledReturn,

    /// <summary>A wash batch: soiled linen from the laundry back to the store clean.</summary>
    Washed,

    /// <summary>Torn, stained beyond washing or worn out: taken out of use.</summary>
    Condemned,

    /// <summary>Missing at a ward's count.</summary>
    Lost,
}

public enum WashProcess
{
    Normal = 0,

    /// <summary>Thermal disinfection: 71 °C held for at least 3 minutes, or 65 °C for at least 10.</summary>
    ThermalDisinfection,
}

public enum CondemnReason
{
    Torn = 0,
    Stained,
    WornOut,
}

/// <summary>A movement of linen, with the count of each item, who counted it and, for a wash batch, how it was washed.</summary>
public sealed class LinenMovement : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public LinenMovementKind Kind { get; set; }
    public Guid? WardId { get; set; }
    public string? WardName { get; set; }
    public bool Infected { get; set; }
    public WashProcess? Process { get; set; }
    public int? TemperatureC { get; set; }
    public int? HoldMinutes { get; set; }
    public CondemnReason? CondemnReason { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset At { get; set; }
    public string ByName { get; set; } = string.Empty;
    public List<LinenMovementLine> Lines { get; set; } = [];

    public int Pieces => Lines.Sum(l => l.Quantity);

    /// <summary>
    /// Whether a wash batch disinfects: thermal disinfection at 71 °C for 3 minutes or 65 °C for 10 (HTM 01-04, as
    /// NABH's laundry standard expects). Infected linen is washed only so.
    /// </summary>
    public static Result CheckWash(bool infected, WashProcess process, int? temperatureC, int? holdMinutes)
    {
        if (process == WashProcess.ThermalDisinfection)
        {
            if (temperatureC is null || holdMinutes is null)
            {
                return Error.Field("temperatureC", "Record the temperature reached and how long it was held.");
            }

            if (!(temperatureC >= 71 && holdMinutes >= 3) && !(temperatureC >= 65 && holdMinutes >= 10))
            {
                return Error.Rule("linen.not_disinfected",
                    $"{temperatureC} °C for {holdMinutes} minutes does not disinfect: hold 71 °C for 3 minutes, or 65 °C for 10. Wash the batch again.");
            }
        }
        else if (infected)
        {
            return Error.Field("process", "Infected linen is washed only by thermal disinfection.");
        }

        return Result.Success();
    }
}

public sealed class LinenMovementLine : Entity
{
    public Guid LinenMovementId { get; set; }
    public Guid LinenItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
}
