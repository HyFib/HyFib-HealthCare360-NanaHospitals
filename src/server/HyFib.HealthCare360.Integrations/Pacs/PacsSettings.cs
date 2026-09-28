namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>PACS configuration (<c>integrations.pacs</c>) for a DICOMweb server such as Orthanc or dcm4chee (INT-PACS).</summary>
internal sealed record PacsSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>dicomweb</c> or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>DICOMweb root, for example <c>http://orthanc:8042/dicom-web</c> (QIDO-RS lives under <c>/studies</c>).</summary>
    public string? QidoUrl { get; init; }

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
}
