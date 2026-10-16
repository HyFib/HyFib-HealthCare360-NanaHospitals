using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A WebAuthn authenticator in software, answering as a device's Windows Hello does: an ECDSA P-256 key pair (ES256), a
/// signature counter, and flags saying the person was present and verified. It makes the browser's part too, the client
/// data with the page's origin, so a test sends the server exactly what the staff app would.
/// </summary>
internal sealed class SoftwareAuthenticator : IDisposable
{
    private const byte UserPresent = 0x01;
    private const byte UserVerifiedFlag = 0x04;
    private const byte AttestedCredentialData = 0x40;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The page the browser says the ceremony came from: the test server's staff address.</summary>
    public string Origin { get; init; } = "http://localhost";

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>The signature counter, which goes up with each use (set it to replay an old count).</summary>
    public uint Counter { get; set; }

    /// <summary>Whether the device checked the person (face, finger or Windows Hello PIN), not only their presence.</summary>
    public bool VerifiesUser { get; set; } = true;

    public string CredentialIdText => Base64Url.EncodeToString(CredentialId);

    /// <summary>The answer to <c>navigator.credentials.create</c> with the server's options: "none" attestation.</summary>
    public JsonElement Register(JsonElement options)
    {
        var challenge = options.GetProperty("challenge").GetString()!;
        var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
        var clientData = ClientData("webauthn.create", challenge);

        var parameters = _key.ExportParameters(includePrivateParameters: false);
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1);
        cose.WriteInt32(2); // kty: EC2
        cose.WriteInt32(3);
        cose.WriteInt32(-7); // alg: ES256
        cose.WriteInt32(-1);
        cose.WriteInt32(1); // crv: P-256
        cose.WriteInt32(-2);
        cose.WriteByteString(parameters.Q.X!);
        cose.WriteInt32(-3);
        cose.WriteByteString(parameters.Q.Y!);
        cose.WriteEndMap();

        var credentialData = new byte[16 + 2 + CredentialId.Length];
        BinaryPrimitives.WriteUInt16BigEndian(credentialData.AsSpan(16), (ushort)CredentialId.Length);
        CredentialId.CopyTo(credentialData, 18);
        var authenticatorData = AuthenticatorData(rpId, AttestedCredentialData, [.. credentialData, .. cose.Encode()]);

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString(authenticatorData);
        attestation.WriteEndMap();

        return Answer(new JsonObject
        {
            ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
            ["attestationObject"] = Base64Url.EncodeToString(attestation.Encode()),
            ["transports"] = new JsonArray("internal"),
        });
    }

    /// <summary>
    /// The answer to <c>navigator.credentials.get</c> with the server's options: the authenticator data and the client
    /// data's hash, signed with the key. <paramref name="userHandle"/> is the account's id, as the device keeps it.
    /// </summary>
    public JsonElement Sign(JsonElement options, Guid? userHandle = null)
    {
        var challenge = options.GetProperty("challenge").GetString()!;
        var rpId = options.GetProperty("rpId").GetString()!;
        var clientData = ClientData("webauthn.get", challenge);
        var authenticatorData = AuthenticatorData(rpId, 0, []);
        var signature = _key.SignData([.. authenticatorData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var response = new JsonObject
        {
            ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
            ["authenticatorData"] = Base64Url.EncodeToString(authenticatorData),
            ["signature"] = Base64Url.EncodeToString(signature),
        };
        if (userHandle is { } id)
        {
            response["userHandle"] = Base64Url.EncodeToString(id.ToByteArray());
        }

        return Answer(response);
    }

    public void Dispose() => _key.Dispose();

    private byte[] ClientData(string type, string challenge) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["type"] = type, ["challenge"] = challenge, ["origin"] = Origin, ["crossOrigin"] = false }.ToJsonString());

    // rpIdHash, flags, signature counter (each use counts up), then the attested credential data when there is some.
    private byte[] AuthenticatorData(string rpId, byte flags, byte[] attested)
    {
        Counter++;
        var data = new byte[32 + 1 + 4];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpId)).CopyTo(data, 0);
        data[32] = (byte)(flags | UserPresent | (VerifiesUser ? UserVerifiedFlag : 0));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(33), Counter);
        return [.. data, .. attested];
    }

    private JsonElement Answer(JsonObject response) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["id"] = CredentialIdText,
        ["rawId"] = CredentialIdText,
        ["type"] = "public-key",
        ["response"] = response,
        ["clientExtensionResults"] = new JsonObject(),
    });
}
