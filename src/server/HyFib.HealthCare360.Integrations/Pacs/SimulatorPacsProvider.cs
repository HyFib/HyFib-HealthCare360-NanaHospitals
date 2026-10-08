using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>
/// Training PACS: answers every accession-number or study-UID query with one study, so radiology staff can practise
/// linking, the image viewer and the viewer launch without a PACS. Every study has the same three series: a localiser
/// (one image), an image series (eight images) and a cine loop (one object of twelve frames, without a series
/// thumbnail, as many real PACS). The study is held under the patient ID HC360 expects, so linking succeeds. UIDs use
/// a fixed test root and are derived from the study, so they stay the same. Objects sent to it (STOW-RS) are taken as
/// stored and not kept.
/// </summary>
internal sealed class SimulatorPacsProvider(IntegrationTrace trace, TimeProvider time) : IPacsProvider
{
    private const string TestUidRoot = "1.2.826.0.1.3680043.10.1360";

    private static readonly SimulatedSeries[] Series =
    [
        new(1, "Localiser", 1, 1, HasThumbnail: true),
        new(2, "Images", 8, 1, HasThumbnail: true),
        new(3, "Cine loop", 1, 12, HasThumbnail: false),
    ];

    public string Key => ProviderKeys.Simulator;

    public Task<bool> PingAsync(PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<IReadOnlyList<ImagingStudyInfo>> FindStudiesAsync(PacsStudyQuery query, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var accession = string.IsNullOrWhiteSpace(query.AccessionNumber) ? null : query.AccessionNumber.Trim();
        var uid = QidoQuery.IsValidUid(query.StudyInstanceUid)
            ? query.StudyInstanceUid!
            : accession is null ? null : string.Create(CultureInfo.InvariantCulture, $"{TestUidRoot}.{Math.Abs((long)StableHash(accession))}");
        IReadOnlyList<ImagingStudyInfo> studies = uid is null
            ? []
            :
            [
                new ImagingStudyInfo(
                    uid,
                    accession,
                    query.PatientId ?? query.ExpectedPatientId,
                    "SIMULATOR^PATIENT",
                    time.GetUtcNow(),
                    string.IsNullOrWhiteSpace(query.Modality) ? "CR" : query.Modality.ToUpperInvariant(),
                    "Simulated study",
                    Series.Length,
                    Series.Sum(s => s.Objects)),
            ];
        await trace.LogAsync(IntegrationNames.Pacs, Key, "qido.studies", true, started, accession ?? uid, null, $"{studies.Count} simulated");
        return studies;
    }

    public async Task<IReadOnlyList<ImagingSeriesInfo>> FindSeriesAsync(string studyInstanceUid, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        IReadOnlyList<ImagingSeriesInfo> series = QidoQuery.IsValidUid(studyInstanceUid)
            ? Series.Select(s => new ImagingSeriesInfo(SeriesUid(studyInstanceUid, s.Number), "OT", s.Number, s.Description, null, s.Objects)).ToList()
            : [];
        await trace.LogAsync(IntegrationNames.Pacs, Key, "qido.series", true, started, studyInstanceUid, null, $"{series.Count} simulated");
        return series;
    }

    public Task<IReadOnlyList<ImagingInstanceInfo>> FindInstancesAsync(
        string studyInstanceUid, string seriesInstanceUid, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        IReadOnlyList<ImagingInstanceInfo> instances = Find(studyInstanceUid, seriesInstanceUid) is { } series
            ? Enumerable.Range(1, series.Objects)
                .Select(n => new ImagingInstanceInfo(InstanceUid(studyInstanceUid, series.Number, n), "1.2.840.10008.5.1.4.1.1.7", n, series.Frames, 512, 512))
                .ToList()
            : [];
        return Task.FromResult(instances);
    }

    public Task<PacsImage> RetrieveImageAsync(PacsImageRequest request, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        if (Find(request.StudyInstanceUid, request.SeriesInstanceUid) is not { } series)
        {
            return Task.FromResult(new PacsImage(PacsImageStatus.NotFound));
        }

        if (request.SopInstanceUid is null)
        {
            return Task.FromResult(series.HasThumbnail
                ? Png(request.Size, series.Number * 100)
                : new PacsImage(PacsImageStatus.NotAvailable));
        }

        var number = Enumerable.Range(1, series.Objects).FirstOrDefault(n => InstanceUid(request.StudyInstanceUid, series.Number, n) == request.SopInstanceUid);
        if (number == 0 || request.Frame is < 1 || request.Frame > series.Frames)
        {
            return Task.FromResult(new PacsImage(PacsImageStatus.NotFound));
        }

        return Task.FromResult(Png(request.Size, (series.Number * 100) + number + (request.Frame ?? 1)));
    }

    /// <summary>Takes every object as stored, so staff can practise sending device files without a PACS.</summary>
    public async Task<PacsStoreOutcome> StoreAsync(DicomObjectToStore item, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        await trace.LogAsync(IntegrationNames.Pacs, Key, "stow", true, started, item.SopInstanceUid,
            string.Create(CultureInfo.InvariantCulture, $"one object, {item.Content.Length / 1024.0:0.#} KB"), "stored (simulated)");
        return new PacsStoreOutcome(item.Reference, PacsStoreStatus.Stored, item.SopInstanceUid, null);
    }

    private static PacsImage Png(int size, int seed) => new(PacsImageStatus.Ok, SimulatorImages.Png(Math.Min(size, 512), seed), PacsImageContent.Png);

    private static SimulatedSeries? Find(string studyInstanceUid, string seriesInstanceUid) =>
        QidoQuery.IsValidUid(studyInstanceUid)
            ? Series.FirstOrDefault(s => SeriesUid(studyInstanceUid, s.Number) == seriesInstanceUid)
            : null;

    private static string SeriesUid(string studyInstanceUid, int number) =>
        string.Create(CultureInfo.InvariantCulture, $"{TestUidRoot}.2.{Math.Abs((long)StableHash(studyInstanceUid))}.{number}");

    private static string InstanceUid(string studyInstanceUid, int series, int number) =>
        string.Create(CultureInfo.InvariantCulture, $"{TestUidRoot}.3.{Math.Abs((long)StableHash(studyInstanceUid))}.{series}.{number}");

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

    private sealed record SimulatedSeries(int Number, string Description, int Objects, int Frames, bool HasThumbnail);
}
