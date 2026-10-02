using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Scheduling;

/// <summary>The numbers the Scheduling module issues (administrators set their formats under Hospital setup).</summary>
public static class SchedulingNumberSeries
{
    public static NumberSeriesDefinition Appointment { get; } =
        new("appointment", "Appointment", "Appointments", "{FAC}AP{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [Appointment];
}
