using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Quality;

/// <summary>The numbers the Quality module issues (administrators set their formats under Hospital setup).</summary>
public static class QualityNumberSeries
{
    public static NumberSeriesDefinition Incident { get; } =
        new("quality.incident", "Incident report", "Quality and safety", "{FAC}IR{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition Capa { get; } =
        new("quality.capa", "Corrective action (CAPA)", "Quality and safety", "{FAC}CAPA{YY}{SEQ:4}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition Risk { get; } =
        new("quality.risk", "Risk", "Quality and safety", "{FAC}RSK{SEQ:4}", NumberResetPolicy.Never, MaxLength: 24);

    public static NumberSeriesDefinition Downtime { get; } =
        new("quality.downtime", "Downtime", "Quality and safety", "{FAC}DT{YY}{SEQ:4}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [Incident, Capa, Risk, Downtime];
}
