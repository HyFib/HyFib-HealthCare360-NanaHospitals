namespace HyFib.HealthCare360.Integrations.Icd;

/// <summary>WHO ICD-API configuration (<c>integrations.icd</c>), INT-ICD / MRD-01.</summary>
internal sealed record IcdSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary>
    /// <c>who</c>, <c>simulator</c> (a built-in training list, <see cref="SimulatorIcdCatalogue"/>) or <c>disabled</c>
    /// (online lookup off; the local ICD-10 catalogue still works).
    /// </summary>
    public string? Provider { get; init; }

    public string TokenUrl { get; init; } = "https://icdaccessmanagement.who.int/connect/token";

    public string ApiBaseUrl { get; init; } = "https://id.who.int";

    /// <summary>ICD-11 MMS release id, for example <c>2025-01</c>.</summary>
    public string Icd11Release { get; init; } = "2025-01";

    public string Icd10Release { get; init; } = "2019";

    /// <summary>Accept-Language for titles.</summary>
    public string Language { get; init; } = "en";

    /// <summary>ICD content changes only with a release, so answers are cached this long.</summary>
    public int CacheHours { get; init; } = 24;
}

/// <summary>Secret ICD-API credentials (<c>integrations.icd.secret</c>) from the WHO API access portal.</summary>
internal sealed record IcdSecrets
{
    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }
}
