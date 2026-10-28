namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// GST on hospital room rent. Health care by a clinical establishment is exempt (notification 12/2017-Central Tax (Rate),
/// entry 74), except, since 18 July 2022, a room other than an ICU, CCU, ICCU or NICU charged above ₹5,000 a day to a
/// patient (the exception added by 4/2022-CT(R)). That room is taxed at 5% on the whole charge, without input tax credit
/// (11/2017-CT(R) entry 31A, inserted by 3/2022): CGST and SGST of 2.5% each, or IGST of 5%. The rule replaces the room's
/// rate in the service master. A day is over the line by what it is charged after the tariff plan's concession; a
/// discount given later on the bill lowers the tax, not the rate.
/// </summary>
public static class RoomRentGst
{
    /// <summary>A day charged above this is taxed.</summary>
    public const decimal DayCharge = 5_000m;

    public const decimal Rate = 5m;

    /// <summary>The GST rate of a day of <paramref name="room"/> charged <paramref name="dayCharge"/>.</summary>
    public static decimal RateFor(RoomCharge room, decimal dayCharge) => room == RoomCharge.Room && dayCharge > DayCharge ? Rate : 0m;
}
