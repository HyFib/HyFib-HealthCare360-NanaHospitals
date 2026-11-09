namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// Whether a transfusion reaction's classification is being lowered (maker-checker, slice 15). A lower severity or a
/// weaker imputability can take a reaction out of haemovigilance reporting, so lowering either waits for the blood bank
/// medical officer, never the officer who asked; raising either, or a first assessment, is made at once.
/// </summary>
public static class ReactionDowngrade
{
    /// <summary>
    /// What would be lowered going from the classification in force to the one asked, in words ("severity Severe → Mild"),
    /// or nothing. An imputability not yet assessed is not lowered by a first assessment.
    /// </summary>
    public static IReadOnlyList<string> Lowers(
        ReactionSeverity severity, ReactionImputability imputability, ReactionSeverity askedSeverity, ReactionImputability askedImputability)
    {
        var lowered = new List<string>();
        if (askedSeverity < severity)
        {
            lowered.Add($"severity {severity} → {askedSeverity}");
        }

        if (imputability != ReactionImputability.NotAssessed && askedImputability < imputability)
        {
            lowered.Add($"imputability {imputability} → {askedImputability}");
        }

        return lowered;
    }

    /// <summary>
    /// Whether the reaction is reported to the Haemovigilance Programme of India: while its imputability is not assessed, or
    /// at least possible. Excluded or unlikely takes it out.
    /// </summary>
    public static bool Reportable(ReactionImputability imputability) =>
        imputability is ReactionImputability.NotAssessed or >= ReactionImputability.Possible;
}
