namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// What the expand/contract rule for database migrations goes by (MigrationPolicyTests; docs/deployment/DEPLOYMENT.md,
/// section 7): where the rule starts, the releases HyFib has published, and the contract steps reviewed. The upgrade test
/// (UpgradeTests, in the end-to-end tests, which compile this file too) reads the releases: it upgrades a populated database
/// from the previous release's newest migration.
/// </summary>
internal static class MigrationPolicy
{
    /// <summary>
    /// The newest migration written before the rule (20261114110000_PatientPhoneVerification): it and every migration before
    /// it are not checked. Some of them take things away (AppointmentRemindersOwnTable drops two columns), and no published
    /// release depends on what they took.
    /// </summary>
    public const string LastGrandfathered = "20261114110000";

    /// <summary>
    /// Each release HyFib has published, oldest first, with the newest migration it carries. Add a release when it is tagged:
    /// a contract step can only follow an expand step that a published release already carries, and the upgrade test then
    /// starts from it.
    /// </summary>
    public static IReadOnlyList<PublishedRelease> Releases { get; } = [];

    /// <summary>
    /// The migrations reviewed as contract steps, each with the expand step it completes and why the release before it no
    /// longer needs what it takes away. Each must come at least one release after the release that shipped its expand step.
    /// </summary>
    public static IReadOnlyList<ContractStep> ContractSteps { get; } = [];
}

/// <param name="Version">The release's version, as tagged: "1.0.0".</param>
/// <param name="LastMigration">The newest migration it carries, by its file name without ".cs": "20261114110000_PatientPhoneVerification".</param>
internal sealed record PublishedRelease(string Version, string LastMigration);

/// <param name="Migration">The contract step, by its file name without ".cs".</param>
/// <param name="ExpandStep">The migration that added what replaces what this one takes away, and after which the code stopped using it.</param>
/// <param name="Reason">Why the release before this one does not need what it takes away, in a sentence or two, and who reviewed it.</param>
internal sealed record ContractStep(string Migration, string ExpandStep, string Reason);
