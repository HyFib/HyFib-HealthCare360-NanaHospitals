using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Privacy;

/// <summary>The numbers the Privacy module issues (administrators set their formats under Hospital setup).</summary>
public static class PrivacyNumberSeries
{
    /// <summary>A data principal's request to see, correct or erase their data (DPDP Act 2023 s11 to s13).</summary>
    public static NumberSeriesDefinition DataRequest { get; } =
        new("privacy.request", "Data request", "Privacy", "{FAC}DPR{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>Personal data breaches, reported for the hospital as a whole.</summary>
    public static NumberSeriesDefinition Breach { get; } =
        new("privacy.breach", "Data breach", "Privacy", "DPB{YY}{SEQ:4}", NumberResetPolicy.Yearly, MaxLength: 24) { PerFacility = false };

    /// <summary>Data Protection Impact Assessments of a Significant Data Fiduciary (DPDP Act 2023 s.10(2)(c)), for the hospital as a whole.</summary>
    public static NumberSeriesDefinition Dpia { get; } =
        new("privacy.dpia", "Data protection impact assessment", "Privacy", "DPIA{YY}{SEQ:3}", NumberResetPolicy.Yearly, MaxLength: 24) { PerFacility = false };

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [DataRequest, Breach, Dpia];
}
