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
