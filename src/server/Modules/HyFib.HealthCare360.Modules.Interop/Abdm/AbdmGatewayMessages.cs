using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// The bodies of the ABDM gateway's v3 HIP calls and callbacks, field for field as NHA's ABDM wrapper sends and
/// receives them (github.com/NHA-ABDM/ABDM-wrapper, the <c>in.nha.abdm.wrapper.v3</c> request and response classes)
/// and as the gateway's specification names them. Property names are the wire names.
/// </summary>
internal static class AbdmGatewayMessages
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary><c>POST /gateway/v3/sessions</c>.</summary>
    public sealed record SessionRequest(string ClientId, string ClientSecret, string GrantType = "client_credentials");

    /// <summary>The session's answer; <c>expiresIn</c> is in seconds (twenty minutes in the sandbox).</summary>
    public sealed record SessionResponse(string? AccessToken, int? ExpiresIn, string? TokenType);

    /// <summary><c>POST /v3/token/generate-token</c>: a link token for the patient's ABHA address.</summary>
    public sealed record GenerateTokenRequest(string AbhaAddress, string Name, string Gender, int YearOfBirth);

    /// <summary>
    /// <c>POST /hip/v3/link/carecontext</c>, sent with the link token in <c>X-LINK-TOKEN</c>: the care contexts grouped
    /// by health-information type, one group per type.
    /// </summary>
    public sealed record AddCareContextsRequest(string? AbhaNumber, string AbhaAddress, IReadOnlyList<CareContextGroup> Patient);

    public sealed record CareContextGroup(string ReferenceNumber, string Display, IReadOnlyList<CareContext> CareContexts, string HiType, int Count);

    public sealed record CareContext(string ReferenceNumber, string Display);

    /// <summary>The request a gateway callback answers.</summary>
    public sealed record RespRequest(string? RequestId);

    public sealed record GatewayError(string? Code, string? Message);

    /// <summary>The gateway's callback <c>/api/v3/hip/token/on-generate-token</c>.</summary>
    public sealed record OnGenerateToken(string? AbhaAddress, string? LinkToken, RespRequest? Response, GatewayError? Error);

    /// <summary>The gateway's callback <c>/api/v3/link/on_carecontext</c>.</summary>
    public sealed record OnAddCareContexts(string? AbhaAddress, string? Status, RespRequest? Response, GatewayError? Error);

    /// <summary>An identifier the gateway sends with a patient: its type (<c>MOBILE</c>, <c>MR</c>, <c>ABHA_NUMBER</c>…) and value.</summary>
    public sealed record Identifier(string? Type, string? Value);

    /// <summary>
    /// The patient a discovery is for, as their ABHA has them: <c>id</c> is the ABHA address; the identifiers ABDM
    /// verified (the mobile number) and those the patient typed (the hospital's UHID, as <c>MR</c>). The year of birth
    /// comes as a number or, from NHA's wrapper, as a string.
    /// </summary>
    public sealed record DiscoverPatient(
        string? Id, string? Name, string? Gender, int? YearOfBirth,
        IReadOnlyList<Identifier>? VerifiedIdentifiers, IReadOnlyList<Identifier>? UnverifiedIdentifiers);

    /// <summary>The gateway's call <c>/api/v3/hip/patient/care-context/discover</c>: a patient looking for their records.</summary>
    public sealed record Discover(string? RequestId, string? TransactionId, DiscoverPatient? Patient);

    /// <summary>
    /// <c>POST /user-initiated-linking/v3/patient/care-context/on-discover</c>: the patient's unlinked records, one group per
    /// health-information type, and how the patient was matched; or an error.
    /// </summary>
    public sealed record OnDiscover(
        string? TransactionId, IReadOnlyList<CareContextGroup>? Patient, IReadOnlyList<string>? MatchedBy, RespRequest Response, GatewayError? Error);

    public sealed record ChosenCareContext(string? ReferenceNumber, string? Display);

    public sealed record ChosenGroup(string? ReferenceNumber, IReadOnlyList<ChosenCareContext>? CareContexts, string? HiType);

    /// <summary>The gateway's call <c>/api/v3/hip/link/care-context/init</c>: the records the patient chose from those offered.</summary>
    public sealed record LinkInit(string? TransactionId, string? AbhaAddress, IReadOnlyList<ChosenGroup>? Patient);

    /// <summary>How the patient is asked to prove it is them: a code sent to their mobile number, until the expiry.</summary>
    public sealed record LinkMeta(string CommunicationMedium, string CommunicationHint, string CommunicationExpiry);

    /// <summary>The link reference the confirm call will name, and how the patient is authenticated (<c>MEDIATE</c>: by the hospital).</summary>
    public sealed record LinkReference(string ReferenceNumber, string AuthenticationType, LinkMeta Meta);

    /// <summary><c>POST /user-initiated-linking/v3/link/care-context/on-init</c>.</summary>
    public sealed record OnLinkInit(string? TransactionId, LinkReference? Link, RespRequest Response, GatewayError? Error);

    public sealed record Confirmation(string? LinkRefNumber, string? Token);

    /// <summary>The gateway's call <c>/api/v3/hip/link/care-context/confirm</c>: the code the patient typed in their app.</summary>
    public sealed record LinkConfirm(string? RequestId, Confirmation? Confirmation);

    /// <summary><c>POST /user-initiated-linking/v3/link/care-context/on-confirm</c>: the linked care contexts, or an error.</summary>
    public sealed record OnLinkConfirm(IReadOnlyList<CareContextGroup>? Patient, RespRequest Response, GatewayError? Error);

    /// <summary>
    /// The gateway's error in an answer body. NHA's wrapper notes the v3 gateway is not consistent: an error comes as
    /// <c>{"error": {...}}</c> or as <c>{"errors": [{"error": {...}}]}</c>, and both are read here.
    /// </summary>
    public static GatewayError? ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                return Read(error);
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errors.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("error", out var inner) && inner.ValueKind == JsonValueKind.Object)
                    {
                        return Read(inner);
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        static GatewayError Read(JsonElement error) => new(Text(error, "code"), Text(error, "message"));

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            } : null;
    }

    /// <summary>
    /// Care contexts as the gateway takes them: one group per health-information type, each under the patient's
    /// reference (their HC360 id) and display (their name), with its care contexts and their count, as NHA's wrapper
    /// groups them for both kinds of linking.
    /// </summary>
    public static IReadOnlyList<CareContextGroup> Groups(Guid patientId, string patientDisplay, IEnumerable<(string Reference, string Display, AbdmRecordType Type)> careContexts) =>
    [
        .. careContexts
            .GroupBy(c => HiType(c.Type))
            .Select(g => new CareContextGroup(
                ReferenceNumber: patientId.ToString("D"),
                Display: patientDisplay,
                CareContexts: [.. g.Select(c => new CareContext(c.Reference, c.Display))],
                HiType: g.Key,
                Count: g.Count())),
    ];

    /// <summary>ABDM's gender codes.</summary>
    public static string Gender(Gender gender) => gender switch
    {
        SharedKernel.Gender.Male => "M",
        SharedKernel.Gender.Female => "F",
        SharedKernel.Gender.Other => "O",
        _ => "U",
    };

    /// <summary>The health-information type ABDM names each kind of record by.</summary>
    public static string HiType(AbdmRecordType type) => type switch
    {
        AbdmRecordType.DischargeSummary => "DischargeSummary",
        AbdmRecordType.OpConsultation => "OPConsultation",
        AbdmRecordType.Prescription => "Prescription",
        _ => "DiagnosticReport",
    };
}
