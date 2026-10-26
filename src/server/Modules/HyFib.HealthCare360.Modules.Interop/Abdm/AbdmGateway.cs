using System.Text.Json;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// Settings of the ABDM gateway (the HIE-CM of the National Health Authority), hospital-wide. <see cref="Gateway"/> holds
/// the switch, the gateway's base URL and the hospital's ids; <see cref="Secret"/> the client id and secret NHA issues
/// with sandbox or production access, stored encrypted (a <c>.secret</c> key).
/// </summary>
internal static class AbdmGatewaySettingKeys
{
    /// <summary><c>{ "enabled": false, "baseUrl": "https://dev.abdm.gov.in/api/hiecm", "hipId": "IN27…", "cmId": "sbx", "timeoutSeconds": 30 }</c>.</summary>
    public const string Gateway = "abdm.gateway";

    /// <summary><c>{ "clientId": "SBX_…", "clientSecret": "…" }</c>, encrypted.</summary>
    public const string Secret = "abdm.gateway.secret";
}

/// <summary>The non-secret part of the gateway set-up.</summary>
internal sealed record AbdmGatewaySettings
{
    /// <summary>Off until the hospital is registered with ABDM and has tested in the sandbox.</summary>
    public bool Enabled { get; init; }

    /// <summary>The gateway's base URL: the sandbox (<c>https://dev.abdm.gov.in/api/hiecm</c>) or production, https only.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The hospital's HIP id, normally its Health Facility Registry id.</summary>
    public string? HipId { get; init; }

    /// <summary>Consent manager id: <c>sbx</c> in the sandbox, <c>abdm</c> in production.</summary>
    public string? CmId { get; init; }

    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>The client credentials NHA issues for the gateway.</summary>
internal sealed record AbdmGatewayCredentials
{
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
}

/// <summary>What the administrator sees about the gateway: how it is set up and what is missing.</summary>
/// <param name="Enabled">The gateway switch in <c>abdm.gateway</c>.</param>
/// <param name="Configured">Base URL, HIP id, consent manager id, client id and secret are all set.</param>
/// <param name="Live">Records are sent to ABDM. Always false in this release (see <paramref name="Remaining"/>).</param>
/// <param name="Problems">Why the set-up is incomplete or wrong.</param>
/// <param name="Remaining">What has to be built and certified before records are sent.</param>
public sealed record AbdmGatewayStatus(
    bool Enabled,
    bool Configured,
    bool Live,
    string? BaseUrl,
    string? HipId,
    string? CmId,
    bool HasClientId,
    bool HasClientSecret,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Remaining);

/// <summary>A care context to link to the patient's ABHA: one exported record, named for the patient's PHR app.</summary>
/// <param name="Reference">HC360's reference for it (the record's id), unique per patient.</param>
/// <param name="Display">What the patient sees, for example "Discharge summary, 12 Sep 2026".</param>
internal sealed record AbdmCareContext(string Reference, string Display, AbdmRecordType Type);

/// <summary>HIP-initiated linking: the patient's ABHA address and the care contexts to add to it.</summary>
internal sealed record AbdmCareContextLink(Guid PatientId, string AbhaAddress, string? AbhaNumber, IReadOnlyList<AbdmCareContext> CareContexts);

/// <summary>A consent the consent manager notified the hospital (HIP) of, to be acknowledged.</summary>
internal sealed record AbdmConsentNotification(string ConsentId, string Status, string RequestId);

/// <summary>The HIU's key material for one transfer (Fidelius: X25519 public key, nonce).</summary>
internal sealed record AbdmKeyMaterial(string CryptoAlgorithm, string Curve, string PublicKey, string Nonce);

/// <summary>One document, encrypted for the HIU, under the care context it belongs to.</summary>
internal sealed record AbdmEncryptedEntry(string CareContextReference, string Content, string Checksum, string Media = "application/fhir+json");

/// <summary>A health-information transfer: the encrypted documents pushed to the HIU's data-push URL.</summary>
internal sealed record AbdmHealthInformationTransfer(
    string TransactionId, Uri DataPushUrl, AbdmKeyMaterial HipKeyMaterial, IReadOnlyList<AbdmEncryptedEntry> Entries);

/// <summary>
/// The hospital's side (HIP) of the ABDM health-information exchange, version 3 of the gateway APIs. Each call needs a
/// gateway session (client credentials). No implementation sends anything yet: the registered client
/// (<see cref="DisabledAbdmGatewayClient"/>) refuses every call with <c>abdm.gateway_disabled</c> and says why.
/// <list type="bullet">
/// <item><see cref="CreateSessionAsync"/>: <c>POST /api/hiecm/gateway/v3/sessions</c>.</item>
/// <item><see cref="LinkCareContextsAsync"/>: <c>POST /api/hiecm/v3/token/generate-token</c>, then
/// <c>POST /api/hiecm/hip/v3/link/carecontext</c> (HIP-initiated linking after a record is completed).</item>
/// <item><see cref="AcknowledgeConsentAsync"/>: <c>POST /api/hiecm/consent/v3/request/hip/on-notify</c>.</item>
/// <item><see cref="TransferHealthInformationAsync"/>: <c>POST /api/hiecm/data-flow/v3/health-information/hip/on-request</c>,
/// the encrypted push to the HIU's <c>dataPushUrl</c>, then <c>POST /api/hiecm/data-flow/v3/health-information/notify</c>.</item>
/// </list>
/// The paths follow NHA's v3 sandbox documentation and are to be confirmed against the sandbox when credentials arrive.
/// </summary>
internal interface IAbdmGatewayClient
{
    Task<Result<string>> CreateSessionAsync(CancellationToken ct);

    Task<Result> LinkCareContextsAsync(AbdmCareContextLink link, CancellationToken ct);

    Task<Result> AcknowledgeConsentAsync(AbdmConsentNotification notification, CancellationToken ct);

    Task<Result> TransferHealthInformationAsync(AbdmHealthInformationTransfer transfer, CancellationToken ct);
}

/// <summary>
/// Fidelius, ABDM's encryption of health information in transit (<see cref="Fidelius"/>): every document of a transfer
/// encrypted for the HIU's key material under one fresh HIP key pair, which goes with them.
/// </summary>
internal interface IFideliusCipher
{
    Result<(IReadOnlyList<AbdmEncryptedEntry> Entries, AbdmKeyMaterial HipKeyMaterial)> Encrypt(
        IReadOnlyList<(string CareContextReference, string DocumentJson)> documents, AbdmKeyMaterial hiu);
}

/// <summary>Reads the gateway settings and says what is set up and what is missing (never the secret itself).</summary>
internal sealed class AbdmGatewayConfiguration(ISettingsService settings)
{
    /// <summary>What has to happen before records are sent to ABDM (also in docs/modules/interop.md).</summary>
    public static IReadOnlyList<string> Remaining { get; } =
    [
        "Register the hospital as a Health Information Provider (HIP) with its HFR id and get NHA sandbox client credentials.",
        "Build the gateway client (sessions, HIP-initiated care-context linking, consent notifications, data-flow requests) and the callback endpoints the gateway calls.",
        "Pass NHA's sandbox functional tests and certification (milestone M2), then switch to the production gateway.",
        "Sign the documents (Bundle.signature, optional in the implementation guide) with the doctor's digital signature certificate.",
    ];

    /// <summary>The settings, the credentials, and what is missing or wrong in them (the on/off switch aside).</summary>
    public async Task<(AbdmGatewaySettings Settings, AbdmGatewayCredentials Credentials, List<string> Problems)> ReadAsync(CancellationToken ct)
    {
        var problems = new List<string>();
        var gateway = await TryReadAsync<AbdmGatewaySettings>(AbdmGatewaySettingKeys.Gateway, problems, ct) ?? new AbdmGatewaySettings();
        var credentials = await TryReadAsync<AbdmGatewayCredentials>(AbdmGatewaySettingKeys.Secret, problems, ct) ?? new AbdmGatewayCredentials();

        if (string.IsNullOrWhiteSpace(gateway.BaseUrl))
        {
            problems.Add($"No gateway base URL (\"baseUrl\" in {AbdmGatewaySettingKeys.Gateway}).");
        }
        else if (!Uri.TryCreate(gateway.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            problems.Add("The gateway base URL must be an https address without a user name or password.");
        }

        if (string.IsNullOrWhiteSpace(gateway.HipId))
        {
            problems.Add($"No HIP id (\"hipId\" in {AbdmGatewaySettingKeys.Gateway}): the hospital's HFR id registered as a HIP.");
        }

        if (gateway.CmId is not ("sbx" or "abdm"))
        {
            problems.Add($"The consent manager id (\"cmId\" in {AbdmGatewaySettingKeys.Gateway}) must be \"sbx\" (sandbox) or \"abdm\" (production).");
        }

        if (string.IsNullOrWhiteSpace(credentials.ClientId) || string.IsNullOrWhiteSpace(credentials.ClientSecret))
        {
            problems.Add($"No gateway client id and secret (the {AbdmGatewaySettingKeys.Secret} setting, stored encrypted).");
        }

        return (gateway, credentials, problems);
    }

    public async Task<AbdmGatewayStatus> StatusAsync(CancellationToken ct)
    {
        var (gateway, credentials, problems) = await ReadAsync(ct);
        return new AbdmGatewayStatus(
            gateway.Enabled,
            Configured: problems.Count == 0,
            Live: false,
            gateway.BaseUrl,
            gateway.HipId,
            gateway.CmId,
            !string.IsNullOrWhiteSpace(credentials.ClientId),
            !string.IsNullOrWhiteSpace(credentials.ClientSecret),
            problems,
            Remaining);
    }

    private async Task<T?> TryReadAsync<T>(string key, List<string> problems, CancellationToken ct)
        where T : class
    {
        try
        {
            return await settings.GetAsync<T>(key, null, ct);
        }
        catch (JsonException)
        {
            problems.Add($"The {key} setting is not valid JSON of the expected shape.");
            return null;
        }
    }
}

/// <summary>
/// The gateway client registered today: it sends nothing and answers every call with <c>abdm.gateway_disabled</c>,
/// naming what is missing, so code written against <see cref="IAbdmGatewayClient"/> fails safely until the live client
/// replaces it.
/// </summary>
internal sealed class DisabledAbdmGatewayClient(AbdmGatewayConfiguration configuration) : IAbdmGatewayClient
{
    public const string Code = "abdm.gateway_disabled";

    public async Task<Result<string>> CreateSessionAsync(CancellationToken ct) => await RefuseAsync(ct);

    public async Task<Result> LinkCareContextsAsync(AbdmCareContextLink link, CancellationToken ct) => await RefuseAsync(ct);

    public async Task<Result> AcknowledgeConsentAsync(AbdmConsentNotification notification, CancellationToken ct) => await RefuseAsync(ct);

    public async Task<Result> TransferHealthInformationAsync(AbdmHealthInformationTransfer transfer, CancellationToken ct) => await RefuseAsync(ct);

    private async Task<Error> RefuseAsync(CancellationToken ct)
    {
        var (gateway, _, problems) = await configuration.ReadAsync(ct);
        var reason = !gateway.Enabled ? $"The gateway is switched off (\"enabled\" in the {AbdmGatewaySettingKeys.Gateway} setting)."
            : problems.Count > 0 ? problems[0]
            : "The live ABDM gateway client is not part of this release.";
        return Error.Rule(Code, $"Nothing was sent to ABDM. {reason}");
    }
}
