using HyFib.HealthCare360.Modules.Interop.Abdm;

namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>Where a request to link records to a patient's ABHA has got to.</summary>
public enum AbdmLinkStatus
{
    /// <summary>The gateway was asked for a link token for the patient's ABHA address; the records go when it arrives.</summary>
    AwaitingToken,

    /// <summary>The care contexts were sent with the link token; the gateway has not said yet whether it linked them.</summary>
    Sent,

    /// <summary>The gateway linked the care contexts to the patient's ABHA.</summary>
    Linked,

    /// <summary>The gateway refused, or the request could not be sent; <see cref="AbdmLinkRequest.Error"/> says why.</summary>
    Failed,
}

/// <summary>
/// A request to link records to a patient's ABHA address (HIP-initiated linking, ABDM M2): each record a care context
/// the patient then sees in their PHR app. The gateway answers each step asynchronously, by calling HC360 back with the
/// request id it was sent under; this row is how those answers find the request again.
/// </summary>
internal sealed class AbdmLinkRequest : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    public Guid PatientId { get; set; }

    /// <summary>The patient's ABHA address when the request was made, for example <c>name@sbx</c>.</summary>
    public string AbhaAddress { get; set; } = string.Empty;

    public AbdmLinkStatus Status { get; set; }

    /// <summary>The care contexts sent, as the gateway expects them (JSON).</summary>
    public string CareContextsJson { get; set; } = "[]";

    /// <summary>The REQUEST-ID of the link-token request, which the gateway's <c>on-generate-token</c> answers.</summary>
    public Guid? TokenRequestId { get; set; }

    /// <summary>The REQUEST-ID the care contexts were sent under, which the gateway's <c>on_carecontext</c> answers.</summary>
    public Guid? LinkRequestId { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>The gateway's error code (for example <c>ABDM-1010</c>), or HC360's when the request could not be sent.</summary>
    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    public void Fail(string code, string message, DateTimeOffset at)
    {
        Status = AbdmLinkStatus.Failed;
        ErrorCode = code.Length > 64 ? code[..64] : code;
        Error = message.Length > 500 ? message[..500] : message;
        AnsweredAt = at;
    }
}

/// <summary>
/// The link token the gateway issued the hospital for one ABHA address: it lets HC360 add care contexts to that ABHA
/// without asking the patient again. Kept encrypted (it is a credential) and replaced when the gateway issues a new one.
/// </summary>
internal sealed class AbdmLinkToken : AuditableEntity
{
    public string HipId { get; set; } = string.Empty;

    /// <summary>Lower-case, as the gateway compares ABHA addresses.</summary>
    public string AbhaAddress { get; set; } = string.Empty;

    /// <summary>The token, protected with the hospital's field key.</summary>
    public string TokenCipher { get; set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; set; }

    public static string Normalize(string abhaAddress) => abhaAddress.Trim().ToLowerInvariant();
}

/// <summary>How a care context came to be linked to a patient's ABHA.</summary>
public enum AbdmLinkRoute
{
    /// <summary>The hospital linked it (<see cref="AbdmLinkRequest"/>).</summary>
    HipInitiated,

    /// <summary>The patient found it from their PHR app and confirmed with the code sent to them (<see cref="AbdmDiscovery"/>).</summary>
    UserInitiated,
}

/// <summary>
/// A record linked to a patient's ABHA address as a care context, whichever way it was linked: the hospital's list of
/// what the patient's PHR app can ask for. Discovery offers only records not already in it, and the consent manager's
/// requests for health information name care contexts by its <see cref="ReferenceNumber"/>.
/// </summary>
internal sealed class AbdmLinkedCareContext : AuditableEntity
{
    public string HipId { get; set; } = string.Empty;

    public Guid PatientId { get; set; }

    /// <summary>Lower-case (<see cref="AbdmLinkToken.Normalize"/>).</summary>
    public string AbhaAddress { get; set; } = string.Empty;

    public AbdmRecordType RecordType { get; set; }

    public Guid RecordId { get; set; }

    /// <summary><c>{kind}/{record id}</c> (<see cref="Services.AbdmLinkingService.ReferenceNumber"/>).</summary>
    public string ReferenceNumber { get; set; } = string.Empty;

    public string Display { get; set; } = string.Empty;

    public AbdmLinkRoute Route { get; set; }

    public DateTimeOffset LinkedAt { get; set; }
}

/// <summary>Where a patient's search for their records from their PHR app has got to.</summary>
public enum AbdmDiscoveryStatus
{
    /// <summary>The patient was found and their records offered; the patient has not chosen yet.</summary>
    Offered,

    /// <summary>The patient chose records and a code was sent to their mobile number.</summary>
    CodeSent,

    /// <summary>The patient gave the right code and the records were linked.</summary>
    Linked,

    /// <summary>The code expired or was entered wrongly too often; the patient starts again from their app.</summary>
    Failed,
}

/// <summary>
/// User-initiated linking (ABDM M2): a patient's search for their records at the hospital from their PHR app, and the
/// steps after it. The gateway's discover call creates it under its transaction id; its link-init call names the
/// records the patient chose, and HC360 sends a code to the patient's mobile number; its link-confirm call brings the
/// code back under <see cref="LinkReference"/>.
/// </summary>
internal sealed class AbdmDiscovery : AuditableEntity
{
    /// <summary>Codes are good for ten minutes, as NHA's wrapper gives them.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Wrong codes allowed before the request fails.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The records chosen at link-init must come within this time of the discovery.</summary>
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(30);

    /// <summary>The gateway's transaction id, which ties discovery and linking together.</summary>
    public string TransactionId { get; set; } = string.Empty;

    public string HipId { get; set; } = string.Empty;

    public Guid PatientId { get; set; }

    /// <summary>The ABHA address the search came from, lower-case.</summary>
    public string AbhaAddress { get; set; } = string.Empty;

    /// <summary>How the patient was matched: <c>ABHA_ADDRESS</c>, <c>MOBILE</c>, and <c>MR</c> when the UHID was given.</summary>
    public string MatchedBy { get; set; } = string.Empty;

    /// <summary>The records offered (<see cref="AbdmLinkedRecord"/> list, JSON).</summary>
    public string OfferedJson { get; set; } = "[]";

    /// <summary>The records the patient chose, a subset of <see cref="OfferedJson"/>.</summary>
    public string? SelectedJson { get; set; }

    public AbdmDiscoveryStatus Status { get; set; }

    public DateTimeOffset OfferedAt { get; set; }

    /// <summary>The link reference sent with the code; the gateway's confirm call names it.</summary>
    public Guid? LinkReference { get; set; }

    /// <summary>The code's hash (<see cref="SecretTokens.Hash"/> of the row id and the code), never the code.</summary>
    public string? CodeHash { get; set; }

    public DateTimeOffset? CodeSentAt { get; set; }

    public DateTimeOffset? CodeExpiresAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? LinkedAt { get; set; }

    public string? Error { get; set; }
}

/// <summary>One record as a care context, in the shape the gateway takes it (<see cref="AbdmGatewayMessages.CareContext"/>).</summary>
internal sealed record AbdmLinkedRecord(AbdmRecordType Type, Guid RecordId, string ReferenceNumber, string Display);

/// <summary>
/// A consent the patient gave through ABDM for another provider (the HIU) to see some of their records here: which care
/// contexts, for what purpose, the dates the records may come from, and until when the HIU may keep them. The consent
/// manager notifies it, and later its revocation or expiry; health-information requests are checked against it.
/// </summary>
internal sealed class AbdmConsent : AuditableEntity
{
    /// <summary>The consent artefact's id at the consent manager.</summary>
    public string ConsentId { get; set; } = string.Empty;

    public string HipId { get; set; } = string.Empty;

    public Guid PatientId { get; set; }

    /// <summary>Lower-case.</summary>
    public string AbhaAddress { get; set; } = string.Empty;

    /// <summary><c>GRANTED</c>, or <c>REVOKED</c>, <c>EXPIRED</c>, <c>DENIED</c> once the consent manager says so.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The provider the records go to.</summary>
    public string? HiuId { get; set; }

    /// <summary>The purpose code (for example <c>CAREMGT</c>, care management) and its text.</summary>
    public string? Purpose { get; set; }

    /// <summary>The health-information types consented to, comma-separated.</summary>
    public string? HiTypes { get; set; }

    /// <summary>The records' dates the consent covers.</summary>
    public DateTimeOffset? DateFrom { get; set; }

    public DateTimeOffset? DateTo { get; set; }

    /// <summary>When the HIU must erase the records; no transfer after it.</summary>
    public DateTimeOffset? DataEraseAt { get; set; }

    /// <summary>The care contexts consented to: their references (JSON array of strings).</summary>
    public string CareContextsJson { get; set; } = "[]";

    public DateTimeOffset NotifiedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>Where a transfer of records to a provider under a consent has got to.</summary>
public enum AbdmTransferStatus
{
    /// <summary>The request was accepted; the documents are being made, encrypted and sent.</summary>
    Acknowledged,

    /// <summary>Every page reached the HIU.</summary>
    Transferred,

    /// <summary>The documents could not all be made or sent; <see cref="AbdmTransfer.Error"/> says why.</summary>
    Failed,
}

/// <summary>
/// One health-information request under a consent, and its transfer: the consented records made as ABDM documents,
/// encrypted for the HIU (Fidelius) and posted to its data-push URL, then reported to the gateway.
/// </summary>
internal sealed class AbdmTransfer : AuditableEntity
{
    /// <summary>The gateway's transaction id for this transfer.</summary>
    public string TransactionId { get; set; } = string.Empty;

    public string ConsentId { get; set; } = string.Empty;

    public string HipId { get; set; } = string.Empty;

    public Guid PatientId { get; set; }

    public string DataPushUrl { get; set; } = string.Empty;

    /// <summary>The HIU's key material as it sent it (<see cref="AbdmGatewayMessages.KeyMaterial"/>, JSON): a public key and nonce.</summary>
    public string KeyMaterialJson { get; set; } = "{}";

    /// <summary>The records' dates asked for, within the consent's.</summary>
    public DateTimeOffset? DateFrom { get; set; }

    public DateTimeOffset? DateTo { get; set; }

    public AbdmTransferStatus Status { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>What happened to each care context (<see cref="AbdmGatewayMessages.CareContextStatus"/> list, JSON).</summary>
    public string? ResultsJson { get; set; }

    public string? Error { get; set; }
}

/// <summary>Where a patient who shared their ABHA profile at a counter has got to.</summary>
public enum AbdmShareStatus
{
    /// <summary>In the counter's queue with their token number.</summary>
    Waiting,

    /// <summary>Staff registered them, or found their record, from the share.</summary>
    Registered,

    /// <summary>Staff took them off the queue without registering them (they left, or shared by mistake).</summary>
    Dismissed,
}

/// <summary>
/// A patient's ABHA profile shared at the hospital by scanning its QR code with their PHR app (scan and share, ABDM M1):
/// the token number they were given for the counter, what their ABHA says about them, and the patient on record it
/// matches. Staff register the patient from it, or open their record, at the counter. Kept a week, then deleted.
/// </summary>
internal sealed class AbdmProfileShare : AuditableEntity
{
    /// <summary>A token holds for an hour: the same patient scanning again at the same counter gets the same one.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    /// <summary>How long shares are kept, for the desk to look back at a busy morning.</summary>
    public const int KeptDays = 7;

    public string HipId { get; set; } = string.Empty;

    /// <summary>The counter's code in the QR the patient scanned.</summary>
    public string Context { get; set; } = string.Empty;

    /// <summary>The day the token is for, in India; numbers start again at 1 each day.</summary>
    public DateOnly TokenDate { get; set; }

    public int TokenNumber { get; set; }

    /// <summary>The ABHA address, lower-case.</summary>
    public string AbhaAddress { get; set; } = string.Empty;

    /// <summary>The 14-digit ABHA number, encrypted like other identifiers.</summary>
    public string? AbhaNumberCipher { get; set; }

    public string? AbhaNumberMasked { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>ABDM's code: <c>M</c>, <c>F</c>, <c>O</c> or <c>U</c>.</summary>
    public string? Gender { get; set; }

    public int? YearOfBirth { get; set; }

    public int? MonthOfBirth { get; set; }

    public int? DayOfBirth { get; set; }

    /// <summary>The mobile number on the ABHA, E.164.</summary>
    public string? Phone { get; set; }

    public string? AddressLine { get; set; }

    public string? District { get; set; }

    public string? State { get; set; }

    public string? Pincode { get; set; }

    public DateTimeOffset SharedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public AbdmShareStatus Status { get; set; }

    /// <summary>
    /// While waiting, the patient on record the share matches, if exactly one does (<see cref="MatchedBy"/>); once
    /// registered, the patient staff registered or chose.
    /// </summary>
    public Guid? PatientId { get; set; }

    /// <summary><c>ABHA_ADDRESS</c>, <c>ABHA_NUMBER</c> or <c>MOBILE</c>; null when nothing matched.</summary>
    public string? MatchedBy { get; set; }

    public DateTimeOffset? HandledAt { get; set; }
}
