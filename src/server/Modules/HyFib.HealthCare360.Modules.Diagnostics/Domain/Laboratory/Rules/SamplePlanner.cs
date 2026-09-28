namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>What decides whether two tests can share one tube.</summary>
public readonly record struct SampleKey(
    ContainerType Container, SpecimenType Specimen, LabSection Section, Guid? ReferralLabId, Guid? DedicatedTestId);

/// <summary>
/// Tube planning (pure; unit-tested). Tests share a tube when the container, specimen and bench (section) match.
/// Keeping benches apart lets haematology and biochemistry process in parallel. Outsourced tests travel in their own
/// tube, and timed specimens (post-prandial glucose) always get a dedicated tube. A test added later joins a matching
/// tube only while that tube is still waiting to be drawn.
/// </summary>
internal static class SamplePlanner
{
    public static SampleKey KeyFor(LabTest test, Guid? referralLabId) =>
        new(test.Container, test.SpecimenType, test.Section, referralLabId, test.DrawSeparately ? test.Id : null);

    public static bool CanJoin(LabSample sample, SampleKey key) =>
        sample.Status == SampleStatus.Pending
        && key.DedicatedTestId is null
        && sample.DedicatedTestId is null
        && sample.Container == key.Container
        && sample.SpecimenType == key.Specimen
        && sample.Section == key.Section
        && sample.ReferralLabId == key.ReferralLabId;

    /// <summary>The pending tube a new test should join, or null when a new tube is needed.</summary>
    public static LabSample? FindTube(IEnumerable<LabSample> samples, SampleKey key) =>
        samples.FirstOrDefault(s => CanJoin(s, key));
}
