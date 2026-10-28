namespace HyFib.HealthCare360.SharedKernel;

/// <summary>
/// India Standard Time, the hospital's time zone: every module's "today" and start of day come from it (audit P9). Windows
/// without the IANA time zone database (no ICU) does not know "Asia/Kolkata". India has kept UTC+05:30, without daylight
/// saving, since 1945, so that fixed offset stands in for it there, never UTC, which would move the hospital's day by five
/// and a half hours: the OPD queue, today's visits and every date checked against today.
/// </summary>
public static class HospitalTimeZone
{
    public const string Id = "Asia/Kolkata";

    public static TimeZoneInfo Zone { get; } = Resolve(Id);

    /// <summary>The time zone <paramref name="id"/>, or India Standard Time as a fixed offset where this machine does not know it.</summary>
    public static TimeZoneInfo Resolve(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");
        }
    }
}
