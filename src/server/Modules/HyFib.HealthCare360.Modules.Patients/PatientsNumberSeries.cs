using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Patients;

/// <summary>The numbers the Patients module issues (administrators set their formats under Hospital setup).</summary>
public static class PatientsNumberSeries
{
    /// <summary>One number per patient for the whole hospital chain, so it is shared by every facility.</summary>
    public static NumberSeriesDefinition Uhid { get; } =
        new("uhid", "UHID (patient number)", "Patients", "{FAC}{YY}{SEQ:6}", NumberResetPolicy.Never, MaxLength: 20)
        {
            PerFacility = false,
            LockedReason = "The AI assistant recognises UHIDs by their shape (the facility code followed by 8 digits) to find " +
                           "patients and to keep their details private, so the format cannot change.",
        };

    /// <summary>Out-patient visits and, from the Inpatient module, the visit each admission opens.</summary>
    public static NumberSeriesDefinition Visit { get; } =
        new("visit", "Visit number", "Patients", "{FAC}V{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition VisitorPass { get; } =
        new("visitor-pass", "Visitor pass", "Front office", "VP{YY}{MM}{DD}-{SEQ:4}", NumberResetPolicy.Daily, MaxLength: 24);

    /// <summary>A department's queue tokens for the day, read back as the token's sequence (a whole number).</summary>
    public static NumberSeriesDefinition QueueToken(Guid departmentId) =>
        new($"token:{departmentId:N}", "Queue token", "Front office", "{SEQ}", NumberResetPolicy.Daily, MaxLength: 9)
        {
            LockedReason = "Queue tokens are counted as whole numbers.",
            Hidden = true,
        };

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [Uhid, Visit, VisitorPass];
}
