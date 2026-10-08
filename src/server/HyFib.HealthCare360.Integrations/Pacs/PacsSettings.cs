namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>PACS configuration (<c>integrations.pacs</c>) for a DICOMweb server such as Orthanc or dcm4chee (INT-PACS).</summary>
internal sealed record PacsSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>dicomweb</c> or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>DICOMweb root, for example <c>http://orthanc:8042/dicom-web</c> (QIDO-RS lives under <c>/studies</c>).</summary>
    public string? QidoUrl { get; init; }

    /// <summary>
    /// DICOMweb root for WADO-RS (rendered images and thumbnails) when the PACS serves it at another address; empty means
    /// <see cref="QidoUrl"/>.
    /// </summary>
    public string? WadoUrl { get; init; }

    /// <summary>
    /// DICOMweb root for STOW-RS (sending objects to the PACS) when the PACS accepts them at another address; empty means
    /// <see cref="QidoUrl"/>.
    /// </summary>
    public string? StowUrl { get; init; }

    /// <summary><c>none</c>, <c>basic</c> or <c>bearer</c>.</summary>
    public string AuthType { get; init; } = "none";

    public string? UserName { get; init; }

    /// <summary>
    /// Viewer launch URL with <c>{studyUid}</c> and <c>{accession}</c> placeholders, for example
    /// <c>https://pacs.local/ohif/viewer?StudyInstanceUIDs={studyUid}</c>.
    /// </summary>
    public string? ViewerUrlTemplate { get; init; }

    public int Limit { get; init; } = 50;

    /// <summary>Time zone of study dates/times the PACS returns without an explicit offset.</summary>
    public string TimeZone { get; init; } = "Asia/Kolkata";

    /// <summary>The WADO-RS root: <see cref="WadoUrl"/>, or the QIDO-RS one when that is empty.</summary>
    public string? WadoRoot => string.IsNullOrWhiteSpace(WadoUrl) ? QidoUrl : WadoUrl;

    /// <summary>The STOW-RS root: <see cref="StowUrl"/>, or the QIDO-RS one when that is empty.</summary>
    public string? StowRoot => string.IsNullOrWhiteSpace(StowUrl) ? QidoUrl : StowUrl;
}

/// <summary>Secret PACS configuration (<c>integrations.pacs.secret</c>).</summary>
internal sealed record PacsSecrets
{
    public string? Password { get; init; }

    public string? BearerToken { get; init; }
}

internal interface IPacsProvider
{
    string Key { get; }

    Task<bool> PingAsync(PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken);

    Task<IReadOnlyList<ImagingStudyInfo>> FindStudiesAsync(PacsStudyQuery query, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken);

    Task<IReadOnlyList<ImagingSeriesInfo>> FindSeriesAsync(string studyInstanceUid, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken);

    Task<IReadOnlyList<ImagingInstanceInfo>> FindInstancesAsync(
        string studyInstanceUid, string seriesInstanceUid, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken);

    Task<PacsImage> RetrieveImageAsync(PacsImageRequest request, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one prepared object (STOW-RS). A PACS that cannot be reached or answers with a failure gives an outcome, not
    /// an exception, so the caller goes on with the next object.
    /// </summary>
    Task<PacsStoreOutcome> StoreAsync(DicomObjectToStore item, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken);
}
