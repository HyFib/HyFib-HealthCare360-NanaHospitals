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

    public sealed record IdRef(string? Id);

    public sealed record DateRange(string? From, string? To);

    public sealed record ConsentCareContext(string? PatientReference, string? CareContextReference);

    public sealed record Purpose(string? Text, string? Code, string? RefUri);

    public sealed record Frequency(string? Unit, int? Value, int? Repeats);

    public sealed record Permission(string? AccessMode, DateRange? DateRange, string? DataEraseAt, Frequency? Frequency);

    /// <summary>The consent artefact: whose records, which care contexts, for whom, why, and for which dates and how long.</summary>
    public sealed record ConsentDetail(
        string? SchemaVersion, string? ConsentId, string? CreatedAt, IdRef? Patient, IReadOnlyList<ConsentCareContext>? CareContexts,
        Purpose? Purpose, IdRef? Hip, IdRef? Hiu, IdRef? ConsentManager, IReadOnlyList<string>? HiTypes, Permission? Permission);

    public sealed record ConsentNotification(string? Status, string? ConsentId, ConsentDetail? ConsentDetail, string? Signature);

    /// <summary>The gateway's call <c>/api/v3/consent/request/hip/notify</c>: a consent granted, or revoked, expired or denied.</summary>
    public sealed record ConsentNotify(string? RequestId, ConsentNotification? Notification);

    public sealed record Acknowledgement(string Status, string ConsentId);

    /// <summary><c>POST /consent/v3/request/hip/on-notify</c>.</summary>
    public sealed record OnConsentNotify(Acknowledgement Acknowledgement, RespRequest Response, GatewayError? Error);

    public sealed record DhPublicKey(string? Expiry, string? Parameters, string? KeyValue);

    /// <summary>The requester's (HIU's) Fidelius key material: ECDH on Curve25519, its public key and nonce.</summary>
    public sealed record KeyMaterial(string? CryptoAlg, string? Curve, DhPublicKey? DhPublicKey, string? Nonce);

    public sealed record HiRequest(IdRef? Consent, DateRange? DateRange, string? DataPushUrl, KeyMaterial? KeyMaterial);

    /// <summary>The gateway's call <c>/api/v3/hip/health-information/request</c>: send the consented records to the HIU.</summary>
    public sealed record HealthInformationRequest(string? RequestId, string? TransactionId, HiRequest? HiRequest);

    public sealed record HiRequestStatus(string TransactionId, string SessionStatus);

    /// <summary><c>POST /data-flow/v3/health-information/hip/on-request</c>: <c>ACKNOWLEDGED</c>, or an error.</summary>
    public sealed record OnHealthInformationRequest(HiRequestStatus? HiRequest, RespRequest Response, GatewayError? Error);

    public sealed record PushEntry(string Content, string Media, string Checksum, string CareContextReference);

    /// <summary>One page of the encrypted documents, posted to the HIU's <c>dataPushUrl</c> (no gateway session).</summary>
    public sealed record DataPush(int PageNumber, int PageCount, string TransactionId, IReadOnlyList<PushEntry> Entries, KeyMaterial KeyMaterial);

    public sealed record Notifier(string Type, string Id);

    public sealed record CareContextStatus(string CareContextReference, string HiStatus, string Description);

    public sealed record StatusNotification(string SessionStatus, string HipId, IReadOnlyList<CareContextStatus> StatusResponses);

    public sealed record TransferNotification(string ConsentId, string TransactionId, string DoneAt, Notifier Notifier, StatusNotification StatusNotification);

    /// <summary><c>POST /data-flow/v3/health-information/notify</c>: what was delivered, per care context.</summary>
    public sealed record TransferNotify(TransferNotification Notification);

    /// <summary>The requesting doctor's identifier: their medical-council registration.</summary>
    public sealed record RequesterIdentifier(string Type, string Value, string System);

    public sealed record Requester(string Name, RequesterIdentifier Identifier);

    /// <summary>What the HIU asks the patient to consent to (ABDM M3): whose records, why, which kinds, which dates, how long.</summary>
    public sealed record ConsentRequestBody(
        Purpose Purpose, IdRef Patient, IdRef Hiu, Requester Requester, IReadOnlyList<string> HiTypes, Permission Permission);

    /// <summary><c>POST /consent/v3/request/init</c>, as the HIU; the gateway answers at <c>/api/v3/hiu/consent/request/on-init</c>.</summary>
    public sealed record ConsentInit(string RequestId, string Timestamp, ConsentRequestBody Consent);

    /// <summary><c>/api/v3/hiu/consent/request/on-init</c>: the consent manager's id for the request, or why it was refused.</summary>
    public sealed record ConsentOnInit(IdRef? ConsentRequest, GatewayError? Error, RespRequest? Response);

    public sealed record ArtefactRef(string? Id);

    public sealed record HiuNotification(string? ConsentRequestId, string? Status, IReadOnlyList<ArtefactRef>? ConsentArtefacts);

    /// <summary><c>/api/v3/hiu/consent/request/notify</c>: the patient granted, denied, or later revoked or let expire, a request.</summary>
    public sealed record HiuNotify(string? RequestId, string? Timestamp, HiuNotification? Notification, GatewayError? Error);

    /// <summary><c>POST /consent/v3/request/hiu/on-notify</c>: one acknowledgement per artefact (none for a denial).</summary>
    public sealed record HiuOnNotify(IReadOnlyList<Acknowledgement> Acknowledgement, RespRequest Response, GatewayError? Error);

    /// <summary><c>POST /consent/v3/fetch</c>: a granted artefact, answered at <c>/api/v3/hiu/consent/on-fetch</c>.</summary>
    public sealed record ConsentFetch(string RequestId, string Timestamp, string ConsentId);

    public sealed record NamedRef(string? Id, string? Name);

    /// <summary>A consent artefact as the HIU receives it: which provider (HIP) holds which of the patient's records.</summary>
    public sealed record HiuConsentDetail(
        string? SchemaVersion, string? ConsentId, string? CreatedAt, IdRef? Patient, IReadOnlyList<ConsentCareContext>? CareContexts,
        Purpose? Purpose, NamedRef? Hip, IdRef? Hiu, IdRef? ConsentManager, IReadOnlyList<string>? HiTypes, Permission? Permission);

    public sealed record FetchedConsent(string? Status, HiuConsentDetail? ConsentDetail, string? Signature);

    /// <summary><c>/api/v3/hiu/consent/on-fetch</c>: the artefact the HIU asked for, or why not.</summary>
    public sealed record ConsentOnFetch(FetchedConsent? Consent, GatewayError? Error, RespRequest? Response);

    /// <summary>
    /// <c>POST /data-flow/v3/health-information/request</c>, as the HIU: the records under a granted artefact, for its dates,
    /// to be pushed to the hospital's <c>dataPushUrl</c> encrypted for the key material sent; answered at
    /// <c>/api/v3/hiu/health-information/on-request</c>.
    /// </summary>
    public sealed record HiuHealthInformationRequest(string RequestId, string Timestamp, HiRequest HiRequest);

    public sealed record ReceivedHiRequestStatus(string? TransactionId, string? SessionStatus);

    /// <summary><c>/api/v3/hiu/health-information/on-request</c>: the transfer's id, which the HIP's pushes name, or why not.</summary>
    public sealed record HiuOnHealthInformationRequest(ReceivedHiRequestStatus? HiRequest, GatewayError? Error, RespRequest? Response);

    /// <summary>One encrypted document in a push: its content (or a link to it, which HC360 does not follow), its checksum and care context.</summary>
    public sealed record ReceivedEntry(string? Content, string? Media, string? Checksum, string? CareContextReference, string? Link);

    /// <summary>A page of encrypted documents a HIP posts to the hospital's <c>dataPushUrl</c> (no gateway token), with the HIP's key material.</summary>
    public sealed record ReceivedPush(int? PageNumber, int? PageCount, string? TransactionId, IReadOnlyList<ReceivedEntry>? Entries, KeyMaterial? KeyMaterial);

    /// <summary>The patient's address on their ABHA, as a profile share carries it.</summary>
    public sealed record ShareAddress(string? Line, string? District, string? State, string? Pincode);

    /// <summary>
    /// The patient's ABHA profile in a scan-and-share call. The wrapper types the date parts as strings; ABDM may send them
    /// as numbers or strings, and an empty or odd one is read as missing (<see cref="LenientIntConverter"/>).
    /// </summary>
    public sealed record SharePatient(
        string? AbhaAddress,
        string? AbhaNumber,
        string? Name,
        string? Gender,
        ShareAddress? Address,
        [property: JsonConverter(typeof(LenientIntConverter))] int? YearOfBirth,
        [property: JsonConverter(typeof(LenientIntConverter))] int? DayOfBirth,
        [property: JsonConverter(typeof(LenientIntConverter))] int? MonthOfBirth,
        string? PhoneNumber,
        IReadOnlyList<Identifier>? Identifiers);

    public sealed record ShareProfile(SharePatient? Patient);

    /// <summary>Where the patient scanned the hospital's QR code: <c>context</c> is the counter's code in the QR.</summary>
    public sealed record ShareMetaData(string? HipId, string? Context, string? HprId, string? Latitude, string? Longitude);

    /// <summary><c>POST /api/v3/hip/patient/share</c>: a patient scanned the hospital's QR code and shared their ABHA profile.</summary>
    public sealed record ProfileShare(string? Intent, ShareMetaData? MetaData, ShareProfile? Profile);

    /// <summary>The token number the patient is given at the counter, and until when it holds.</summary>
    public sealed record ShareToken(string Context, string TokenNumber, string Expiry);

    public sealed record ShareAcknowledgement(string Status, string AbhaAddress, ShareToken Profile);

    /// <summary><c>POST /patient-share/v3/on-share</c>: the token for the patient's app, or why not.</summary>
    public sealed record OnShare(ShareAcknowledgement? Acknowledgement, RespRequest Response, GatewayError? Error);

    /// <summary>Reads a number given as a number or a string; anything else, including an empty string, as null.</summary>
    public sealed class LenientIntConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var number) => number,
            JsonTokenType.String when int.TryParse(reader.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
            JsonTokenType.StartObject or JsonTokenType.StartArray => Skip(ref reader),
            _ => null,
        };

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value is { } number)
            {
                writer.WriteNumberValue(number);
            }
            else
            {
                writer.WriteNullValue();
            }
        }

        private static int? Skip(ref Utf8JsonReader reader)
        {
            reader.Skip();
            return null;
        }
    }

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
