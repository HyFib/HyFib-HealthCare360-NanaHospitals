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

/// <summary>One record as a care context, in the shape the gateway takes it (<see cref="AbdmGatewayMessages.CareContext"/>).</summary>
internal sealed record AbdmLinkedRecord(AbdmRecordType Type, Guid RecordId, string ReferenceNumber, string Display);
