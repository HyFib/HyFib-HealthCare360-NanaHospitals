using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Inpatient;

/// <summary>
/// The numbers the Inpatient module issues (administrators set their formats under Hospital setup). The visit an
/// admission opens takes its number from the Patients module's visit series.
/// </summary>
public static class InpatientNumberSeries
{
    public static NumberSeriesDefinition Admission { get; } =
        new("admission", "Admission (IP number)", "In-patients", "{FAC}IP{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [Admission];
}
