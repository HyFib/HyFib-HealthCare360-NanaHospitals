namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// Antimicrobial stewardship (kept by Clinical) as the pharmacy asks it before issuing medicines against a prescription:
/// a restricted antibiotic goes out for its first doses only while it waits for approval, and never once it is rejected.
/// </summary>
public interface IAntimicrobialStewardship
{
    /// <summary>Why the prescription lines may not be issued now (the first reason found); null when they may.</summary>
    Task<string?> IssueRefusalAsync(IReadOnlyCollection<Guid> prescriptionLineIds, CancellationToken cancellationToken);
}
