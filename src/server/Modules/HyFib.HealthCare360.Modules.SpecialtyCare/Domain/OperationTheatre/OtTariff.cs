namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>Service catalogue codes billed by the operation theatre (seeded by <c>SpecialtyCareSeeder</c>).</summary>
public static class OtServiceCodes
{
    public const string MajorTheatreHour = "OT-MAJOR-HR";
    public const string MinorTheatreHour = "OT-MINOR-HR";
    public const string SurgeonFee = "OT-SURGEON-FEE";
    public const string AnaesthetistFee = "OT-ANAES-FEE";

    /// <summary>Ad-hoc line for implants; the price comes from the implant invoice (UnitPriceOverride).</summary>
    public const string Implant = "OT-IMPLANT";

    /// <summary>Ad-hoc line for OT consumables charged at actuals (UnitPriceOverride).</summary>
    public const string Consumable = "OT-CONSUMABLE";
}

/// <summary>How theatre time is billed.</summary>
public static class OtTariff
{
    /// <summary>
    /// Indian hospitals bill OT time per hour with the first hour as a minimum and later time in half-hour slabs
    /// (61–90 minutes is billed as 1.5 hours). This keeps short cases fair while avoiding per-minute disputes.
    /// </summary>
    public static decimal BillableHours(double minutesInTheatre)
    {
        if (minutesInTheatre <= 60)
        {
            return 1m;
        }

        var halfHours = (int)Math.Ceiling(minutesInTheatre / 30d);
        return halfHours / 2m;
    }

    /// <summary>Minor theatres have a cheaper hourly tariff; every other theatre type bills the major rate.</summary>
    public static string HourlyChargeCode(TheatreType type) =>
        type == TheatreType.Minor ? OtServiceCodes.MinorTheatreHour : OtServiceCodes.MajorTheatreHour;
}
