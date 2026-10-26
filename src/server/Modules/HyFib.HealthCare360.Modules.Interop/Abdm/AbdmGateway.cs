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
    /// <summary>
    /// <c>{ "enabled": false, "baseUrl": "https://dev.abdm.gov.in/api/hiecm", "hipId": "IN27…", "cmId": "sbx", "timeoutSeconds": 30 }</c>,
    /// and optionally <c>"jwksUrl"</c>, where the gateway publishes the keys its callbacks are signed with.
    /// </summary>
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

    /// <summary>
    /// The gateway's key set (JWKS), which its callbacks to HC360 are checked against; by default
    /// <c>{baseUrl}/gateway/v3/certs</c>, the v3 place of the specification's <c>/v0.5/certs</c>, to be confirmed in the
    /// sandbox. https only.
    /// </summary>
    public string? JwksUrl { get; init; }

    /// <summary><see cref="JwksUrl"/>, or the default under <see cref="BaseUrl"/>.</summary>
    public string? KeySetUrl => !string.IsNullOrWhiteSpace(JwksUrl) ? JwksUrl
        : string.IsNullOrWhiteSpace(BaseUrl) ? null
        : $"{BaseUrl.TrimEnd('/')}/gateway/v3/certs";
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
/// <param name="Live">
/// The gateway is switched on and set up, so records are linked to patients' ABHAs and sent under their consents.
/// What remains is NHA's sandbox run and certification (<paramref name="Remaining"/>).
/// </param>
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

/// <summary>HIP-initiated linking: the patient (their id and name), their ABHA address and the care contexts to add to it.</summary>
internal sealed record AbdmCareContextLink(
    Guid PatientId, string PatientDisplay, string AbhaAddress, string? AbhaNumber, IReadOnlyList<AbdmCareContext> CareContexts);

/// <summary>The HIU's key material for one transfer (Fidelius: X25519 public key, nonce).</summary>
internal sealed record AbdmKeyMaterial(string CryptoAlgorithm, string Curve, string PublicKey, string Nonce);

/// <summary>One document, encrypted for the HIU, under the care context it belongs to.</summary>
internal sealed record AbdmEncryptedEntry(string CareContextReference, string Content, string Checksum, string Media = "application/fhir+json");

/// <summary>The patient as the gateway asks for a link token: their ABHA address, name, gender and year of birth.</summary>
internal sealed record AbdmLinkTokenRequest(string AbhaAddress, string Name, string Gender, int YearOfBirth);

/// <summary>
/// The hospital's side (HIP) of the ABDM health-information exchange, version 3 of the gateway APIs, under the base URL
/// in <see cref="AbdmGatewaySettings.BaseUrl"/>. Each call needs a gateway session (client credentials), and each is
/// answered asynchronously: the gateway accepts it (202) and later calls HC360 back with the request id it was sent
/// under. Paths and bodies as NHA's ABDM wrapper uses them:
/// <list type="bullet">
/// <item><see cref="CreateSessionAsync"/>: <c>POST /gateway/v3/sessions</c>.</item>
/// <item><see cref="RequestLinkTokenAsync"/>: <c>POST /v3/token/generate-token</c>, answered at
/// <c>/api/v3/hip/token/on-generate-token</c>.</item>
/// <item><see cref="LinkCareContextsAsync"/>: <c>POST /hip/v3/link/carecontext</c> with the link token, answered at
/// <c>/api/v3/link/on_carecontext</c> (HIP-initiated linking after a record is completed).</item>
/// <item><see cref="AnswerDiscoveryAsync"/>, <see cref="AnswerLinkInitAsync"/> and <see cref="AnswerLinkConfirmAsync"/>:
/// <c>POST /user-initiated-linking/v3/patient/care-context/on-discover</c>, <c>…/v3/link/care-context/on-init</c> and
/// <c>…/on-confirm</c>, the hospital's answers when a patient looks for their records from their PHR app.</item>
/// <item><see cref="AnswerConsentNotifyAsync"/>: <c>POST /consent/v3/request/hip/on-notify</c>, the acknowledgement of
/// a consent the consent manager notified.</item>
/// <item><see cref="AnswerHealthInformationRequestAsync"/>: <c>POST /data-flow/v3/health-information/hip/on-request</c>;
/// the encrypted documents then go to the HIU's <c>dataPushUrl</c> (<see cref="AbdmDataPush"/>), and
/// <see cref="NotifyTransferAsync"/> (<c>POST /data-flow/v3/health-information/notify</c>) says what was delivered.</item>
/// </list>
/// The live client is <see cref="HttpAbdmGatewayClient"/>; every call is refused with <c>abdm.gateway_disabled</c>
/// while the gateway is switched off or not set up (<see cref="DisabledAbdmGatewayClient"/>).
/// </summary>
internal interface IAbdmGatewayClient
{
    Task<Result<string>> CreateSessionAsync(CancellationToken ct);

    /// <summary>Asks for a link token for the patient's ABHA address, under <paramref name="requestId"/>.</summary>
    Task<Result> RequestLinkTokenAsync(AbdmLinkTokenRequest request, Guid requestId, CancellationToken ct);

    /// <summary>Adds the care contexts to the patient's ABHA with the link token, under <paramref name="requestId"/>.</summary>
    Task<Result> LinkCareContextsAsync(AbdmCareContextLink link, string linkToken, Guid requestId, CancellationToken ct);

    /// <summary>Answers the gateway's discover call: the patient's unlinked records, or why there are none.</summary>
    Task<Result> AnswerDiscoveryAsync(AbdmGatewayMessages.OnDiscover answer, CancellationToken ct);

    /// <summary>Answers the gateway's link-init call: the link reference and where the code was sent, or why not.</summary>
    Task<Result> AnswerLinkInitAsync(AbdmGatewayMessages.OnLinkInit answer, CancellationToken ct);

    /// <summary>Answers the gateway's link-confirm call: the care contexts linked, or why not.</summary>
    Task<Result> AnswerLinkConfirmAsync(AbdmGatewayMessages.OnLinkConfirm answer, CancellationToken ct);

    /// <summary>Acknowledges a consent notification: <c>OK</c>, or why not.</summary>
    Task<Result> AnswerConsentNotifyAsync(AbdmGatewayMessages.OnConsentNotify answer, CancellationToken ct);

    /// <summary>Answers a health-information request: <c>ACKNOWLEDGED</c>, or why not.</summary>
    Task<Result> AnswerHealthInformationRequestAsync(AbdmGatewayMessages.OnHealthInformationRequest answer, CancellationToken ct);

    /// <summary>Tells the gateway what a transfer delivered, per care context.</summary>
    Task<Result> NotifyTransferAsync(AbdmGatewayMessages.TransferNotify notification, CancellationToken ct);
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
    /// <summary>What has to happen before the hospital exchanges records through ABDM for real (also in docs/modules/interop.md).</summary>
    public static IReadOnlyList<string> Remaining { get; } =
    [
        "Register the hospital as a Health Information Provider (HIP) with its HFR id and get NHA sandbox client credentials.",
        "Run linking, consent and the transfer of records in NHA's sandbox, and confirm the gateway's key-set URL and token issuer.",
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

        if (!string.IsNullOrWhiteSpace(gateway.JwksUrl)
            && (!Uri.TryCreate(gateway.JwksUrl, UriKind.Absolute, out var jwks) || jwks.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(jwks.UserInfo)))
        {
            problems.Add("The gateway's key-set URL (\"jwksUrl\") must be an https address without a user name or password.");
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
            Live: gateway.Enabled && problems.Count == 0,
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
/// What every gateway call answers while it cannot be made: <c>abdm.gateway_disabled</c>, naming what is missing (the
/// switch, the set-up), so code written against <see cref="IAbdmGatewayClient"/> fails safely. The live client uses it
/// for the calls it does not make yet (consent and data flow).
/// </summary>
internal sealed class DisabledAbdmGatewayClient(AbdmGatewayConfiguration configuration) : IAbdmGatewayClient
{
    public const string Code = "abdm.gateway_disabled";

    public async Task<Result<string>> CreateSessionAsync(CancellationToken ct) => await RefuseAsync(ct);

    public async Task<Result> RequestLinkTokenAsync(AbdmLinkTokenRequest request, Guid requestId, CancellationToken ct) => await RefuseAsync(ct);

    public async Task<Result> LinkCareContextsAsync(AbdmCareContextLink link, string linkToken, Guid requestId, CancellationToken ct) =>
        await RefuseAsync(ct);

    public async Task<Result> AnswerDiscoveryAsync(AbdmGatewayMessages.OnDiscover answer, CancellationToken ct) => await RefuseAsync(ct);

    public async Task<Result> AnswerLinkInitAsync(AbdmGatewayMessages.OnLinkInit answer, CancellationToken ct) => await RefuseAsync(ct);

    public async Task<Result> AnswerLinkConfirmAsync(AbdmGatewayMessages.OnLinkConfirm answer, CancellationToken ct) => await RefuseAsync(ct);

    public async Task<Result> AnswerConsentNotifyAsync(AbdmGatewayMessages.OnConsentNotify answer, CancellationToken ct) => await RefuseAsync(ct);

    public async Task<Result> AnswerHealthInformationRequestAsync(AbdmGatewayMessages.OnHealthInformationRequest answer, CancellationToken ct) =>
        await RefuseAsync(ct);

    public async Task<Result> NotifyTransferAsync(AbdmGatewayMessages.TransferNotify notification, CancellationToken ct) => await RefuseAsync(ct);

    /// <summary>The refusal, or null when the gateway is switched on and set up.</summary>
    public async Task<Error?> WhyNotAsync(CancellationToken ct)
    {
        var (gateway, _, problems) = await configuration.ReadAsync(ct);
        return !gateway.Enabled ? Refusal($"The gateway is switched off (\"enabled\" in the {AbdmGatewaySettingKeys.Gateway} setting).")
            : problems.Count > 0 ? Refusal(problems[0])
            : null;
    }

    public static Error Refusal(string reason) => Error.Rule(Code, $"Nothing was sent to ABDM. {reason}");

    /// <summary>The refusal: the gateway is off or not set up, or, when it is on, this client is not the one to call.</summary>
    private async Task<Error> RefuseAsync(CancellationToken ct) =>
        await WhyNotAsync(ct) ?? Refusal("The gateway is on: the live client sends this.");
}
