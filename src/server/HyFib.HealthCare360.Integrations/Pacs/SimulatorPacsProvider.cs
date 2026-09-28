namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>
/// Training PACS: answers every accession-number query with one study for that accession (and patient id when given),
/// so radiology staff can practise reconciliation and viewer launch without a PACS. Study UIDs use a fixed test root.
/// </summary>
internal sealed class SimulatorPacsProvider(IntegrationTrace trace, TimeProvider time) : IPacsProvider
{
    private const string TestUidRoot = "1.2.826.0.1.3680043.10.1360";

    public string Key => ProviderKeys.Simulator;

    public Task<bool> PingAsync(PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<IReadOnlyList<ImagingStudyInfo>> FindStudiesAsync(PacsStudyQuery query, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        IReadOnlyList<ImagingStudyInfo> studies = string.IsNullOrWhiteSpace(query.AccessionNumber)
            ? []
            :
            [
                new ImagingStudyInfo(
                    $"{TestUidRoot}.{Math.Abs((long)StableHash(query.AccessionNumber))}",
                    query.AccessionNumber.Trim(),
                    query.PatientId,
                    "SIMULATOR^PATIENT",
                    time.GetUtcNow(),
                    string.IsNullOrWhiteSpace(query.Modality) ? "CR" : query.Modality.ToUpperInvariant(),
                    "Simulated study",
                    1,
                    2),
            ];
        await trace.LogAsync(IntegrationNames.Pacs, Key, "qido.studies", true, started, query.AccessionNumber, null, $"{studies.Count} simulated");
        return studies;
    }

    /// <summary>Deterministic (unlike string.GetHashCode) so the same accession always maps to the same study UID.</summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value.Trim())
            {
                hash = (hash * 31) + c;
            }

            return hash;
        }
    }
}
