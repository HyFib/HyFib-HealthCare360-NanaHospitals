using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Engagement;

/// <summary>The numbers the Engagement module issues (administrators set their formats under Hospital setup).</summary>
public static class EngagementNumberSeries
{
    public static NumberSeriesDefinition Lead { get; } =
        new("crm.lead", "Lead", "Patient relations", "{FAC}LD{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition Complaint { get; } =
        new("feedback.complaint", "Complaint", "Patient feedback", "{FAC}CMP{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [Lead, Complaint];
}
