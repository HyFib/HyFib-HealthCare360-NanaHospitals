namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>A diagnosis or procedure code of an episode with its title, as the medical records coder recorded it.</summary>
public sealed record CodedEntry(string Code, string Title, bool IsPrimary);

/// <summary>
/// The medical records coding of an admission or visit once a second coder has verified it (MRD-02, MB-03): ICD-10
/// diagnoses with the primary diagnosis first, and ICD-10-PCS procedures, each in the coder's order.
/// </summary>
public sealed record VerifiedEpisodeCoding(
    IReadOnlyList<CodedEntry> Diagnoses,
    IReadOnlyList<CodedEntry> Procedures,
    DateTimeOffset VerifiedAt)
{
    /// <summary>
    /// The entries as one line of text for a claim form, such as "L03.1 Cellulitis of other parts of limb; I10 Essential
    /// (primary) hypertension". When that is longer than <paramref name="maxLength"/>, the codes alone; when even those
    /// are longer, the codes that fit, from the first. Null when there are no entries.
    /// </summary>
    public static string? AsText(IReadOnlyList<CodedEntry> entries, int maxLength)
    {
        var titled = string.Join("; ", entries.Select(e => $"{e.Code} {e.Title}".Trim()));
        if (titled.Length <= maxLength)
        {
            return titled.Length == 0 ? null : titled;
        }

        var codes = new List<string>();
        var length = 0;
        foreach (var entry in entries)
        {
            length += (codes.Count == 0 ? 0 : 2) + entry.Code.Length;
            if (length > maxLength)
            {
                break;
            }

            codes.Add(entry.Code);
        }

        return codes.Count == 0 ? null : string.Join("; ", codes);
    }
}

/// <summary>
/// Implemented by the Clinical module, for insurance claims in the Billing module: the verified coding of a patient's
/// admission or, for a bill without an admission, of the visit. Null when no coding of that episode is verified.
/// </summary>
public interface IVerifiedCodingSource
{
    Task<VerifiedEpisodeCoding?> FindAsync(
        Guid patientId, Guid? admissionId, Guid? visitId, CancellationToken cancellationToken);
}

/// <summary>Fallback used until the Clinical module registers its implementation.</summary>
internal sealed class NullVerifiedCodingSource : IVerifiedCodingSource
{
    public Task<VerifiedEpisodeCoding?> FindAsync(
        Guid patientId, Guid? admissionId, Guid? visitId, CancellationToken cancellationToken) =>
        Task.FromResult<VerifiedEpisodeCoding?>(null);
}
