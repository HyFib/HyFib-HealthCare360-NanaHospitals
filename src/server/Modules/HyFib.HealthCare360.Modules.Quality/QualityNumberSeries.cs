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

    /// <summary>A medical device adverse event report to the Materiovigilance Programme of India (MvPI).</summary>
    public static NumberSeriesDefinition Mvpi { get; } =
        new("quality.mvpi", "Device adverse event report (MvPI)", "Quality and safety", "{FAC}MDAE{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>A possible hospital-acquired infection under review by the infection control team.</summary>
    public static NumberSeriesDefinition Hai { get; } =
        new("quality.hai", "Infection surveillance case (HAI)", "Quality and safety", "{FAC}HAI{YY}{SEQ:4}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>An internal NABH audit.</summary>
    public static NumberSeriesDefinition Audit { get; } =
        new("quality.audit", "Internal audit", "Quality and safety", "{FAC}AUD{YY}{SEQ:4}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [Incident, Capa, Risk, Downtime, Mvpi, Hai, Audit];
}
