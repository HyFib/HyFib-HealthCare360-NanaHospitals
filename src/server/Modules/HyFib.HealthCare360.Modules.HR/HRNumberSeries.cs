using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.HR;

/// <summary>The numbers the HR module issues (administrators set their formats under Hospital setup).</summary>
public static class HRNumberSeries
{
    /// <summary>Employee codes; the canteen's staff credit keeps them in 20 characters, so they may not be longer.</summary>
    public static NumberSeriesDefinition Employee { get; } =
        new("hr.employee", "Employee code", "Human resources", "{FAC}EMP{SEQ:5}", NumberResetPolicy.Never, MaxLength: 20);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [Employee];
}
