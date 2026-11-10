namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>Where an employee's balance stands for an in-service encashment of one leave type and year.</summary>
/// <param name="Closing">The balance's closing days (opening + credited + adjusted − taken − encashed).</param>
/// <param name="PendingLeave">Days of leave applied for and not yet decided: they are reserved.</param>
/// <param name="PendingEncashment">Days of encashment already asked for and not yet decided: reserved too.</param>
/// <param name="EncashedThisYear">Days already encashed in service from this year's balance (approved).</param>
public sealed record EncashmentPosition(decimal Closing, decimal PendingLeave, decimal PendingEncashment, decimal EncashedThisYear);

/// <summary>Why an encashment is refused: a stable code and the words for the person asking.</summary>
public sealed record EncashmentRefusal(string Code, string Message);

/// <summary>
/// The leave policy for paying out days while in service (HR-05). A type pays out in service only when it is encashable
/// and names a yearly cap; the days asked for must fit under the cap (what was encashed this year and what waits count),
/// and the balance left after them, net of leave and encashments still waiting, must not fall below the minimum the
/// policy keeps. Whole days unless the type allows half days.
/// </summary>
public static class LeaveEncashmentPolicy
{
    public const string NotAllowedCode = "leave.encash_not_allowed";
    public const string OverCapCode = "leave.encash_over_cap";
    public const string BelowMinimumCode = "leave.encash_below_minimum";
    public const string WholeDaysCode = "leave.encash_whole_days";

    /// <summary>The most days that can be encashed now (0 when none can), or null when the type does not pay out in service.</summary>
    public static decimal? MostDays(LeaveType type, EncashmentPosition position)
    {
        if (!type.IsEncashable || !type.TracksBalance || type.InServiceEncashmentDaysPerYear is not { } cap || cap <= 0)
        {
            return null;
        }

        var underCap = cap - position.EncashedThisYear - position.PendingEncashment;
        var aboveMinimum = position.Closing - position.PendingLeave - position.PendingEncashment - type.MinimumBalanceAfterEncashment;
        var most = Math.Max(0, Math.Min(underCap, aboveMinimum));
        return type.AllowHalfDay ? Math.Floor(most * 2) / 2 : Math.Floor(most);
    }

    /// <summary>Null when <paramref name="days"/> may be encashed; otherwise the refusal.</summary>
    public static EncashmentRefusal? Check(LeaveType type, EncashmentPosition position, decimal days)
    {
        if (MostDays(type, position) is null)
        {
            return new EncashmentRefusal(NotAllowedCode,
                $"{type.Name} ({type.Code}) is not paid out while in service under the leave policy: it is encashed only in the full and final settlement, if at all.");
        }

        if (days <= 0 || (type.AllowHalfDay ? days * 2 != Math.Floor(days * 2) : days != Math.Floor(days)))
        {
            return new EncashmentRefusal(WholeDaysCode, type.AllowHalfDay ? "Encash whole or half days." : $"{type.Code} is encashed in whole days.");
        }

        var cap = type.InServiceEncashmentDaysPerYear!.Value;
        var used = position.EncashedThisYear + position.PendingEncashment;
        if (used + days > cap)
        {
            return new EncashmentRefusal(OverCapCode,
                $"The policy pays out at most {cap:0.#} day(s) of {type.Code} a year in service; {used:0.#} already encashed or waiting, so at most {Math.Max(0, cap - used):0.#} more.");
        }

        var left = position.Closing - position.PendingLeave - position.PendingEncashment - days;
        if (left < type.MinimumBalanceAfterEncashment)
        {
            var most = Math.Max(0, position.Closing - position.PendingLeave - position.PendingEncashment - type.MinimumBalanceAfterEncashment);
            return new EncashmentRefusal(BelowMinimumCode,
                $"The policy keeps {type.MinimumBalanceAfterEncashment:0.#} day(s) of {type.Code} in the balance; after leave and encashments waiting, at most {most:0.#} day(s) can be encashed.");
        }

        return null;
    }
}
