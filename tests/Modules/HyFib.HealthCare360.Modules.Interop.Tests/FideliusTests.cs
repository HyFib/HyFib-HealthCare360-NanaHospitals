using System.Text;
using HyFib.HealthCare360.Modules.Interop.Abdm;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Fidelius against NHA's reference implementation (<c>mgrmtech/fidelius-cli</c> 1.2.0): the key material and the
/// ciphertext below were made by its <c>gkm</c> and <c>se</c> commands. AES-GCM is deterministic for a key and IV, so the
/// same inputs must give the reference's ciphertext byte for byte, and its ciphertext must decrypt here.
/// </summary>
public sealed class FideliusTests
{
    private const string Document = "{\"resourceType\":\"Bundle\",\"type\":\"document\",\"entry\":[{\"resource\":{\"resourceType\":\"Patient\",\"name\":[{\"text\":\"மீனா குமார்\"}]}}]}";

    // The HIP (sender) and the HIU (requester), as the reference generated them.
    private const string HipPrivateKey = "BO1F1ipvlTI0LNB1G4xPbDTpBdZ+rBuQ/ngHzHO4E1w=";
    private const string HipPublicKey = "BA2fg5R6zIcljB6n9tt/5fCorL4dIID4QIKHkh278pLGULn1kgnju8NDQbL6OPQmoDSz3tvoVzS9gVZYixeJQgk=";
    private const string HipNonce = "j2/ASD3RTXwJZMtyoIcSuKsi8fzf6N+oppSF+EWnCNM=";
    private const string HiuPrivateKey = "B9O5XJL5tKlcDlrPSeozuagQB845o9B0hY7jys281Ws=";
    private const string HiuPublicKey = "BB7SEMPbIif2kpUQzJ7i5gBC/IQVDQp0SAjJWxp9y5tAQvyKHj5Yxq/ydcYfBEw/oqwo66L5fwg364o01fjnhLA=";
    private const string HiuNonce = "l4p/sx+ItbG5zyzNsp06o+nBCrUIvOt5KDK80LcpqdM=";

    private const string HiuX509PublicKey =
        "MIIBMTCB6gYHKoZIzj0CATCB3gIBATArBgcqhkjOPQEBAiB/////////////////////////////////////////7TBEBCAqqqqq"
        + "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqYSRShRAQge0Je0Je0Je0Je0Je0Je0Je0Je0Je0Je0JgtenHcQyGQEQQQqqqqqqqqqqqqq"
        + "qqqqqqqqqqqqqqqqqqqqqqqqqq0kWiCuGaG4oIa04B7dLHdI0UySPU1+bXxhsinpxaJ+ztPZAiAQAAAAAAAAAAAAAAAAAAAAFN75"
        + "3qL3nNZYEmMaXPXT7QIBCANCAAQe0hDD2yIn9pKVEMye4uYAQvyEFQ0KdEgIyVsafcubQEL8ih4+WMav8nXGHwRMP6KsKOui+X8I"
        + "N+uKNNX454Sw";

    /// <summary>The reference's encryption of <see cref="Document"/> from the HIP to the HIU (the same with either form of the HIU's key).</summary>
    private const string ReferenceCipherText =
        "oukS/lvWp/0OmNQHrERGTwvKox1pcJHCNd+7B4+eY340JjerVrB9XGFw3R42lsTT8XSRMhHMeV+J50qcU0BLhfHR7xE3OYwonpu/"
        + "ViSYfir2/ui4lNJSNM/F5sindjMrBRq5BiUCv8X7i5a29/yyJXItvJotpesMQhuHNCg+Ah6LrofU4uA6pqTGjTsPTD3Or5UtquYc"
        + "g6pbSBqWYorV3Cc=";

    [Fact]
    public void Encrypting_as_the_reference_does_gives_its_ciphertext_byte_for_byte()
    {
        Fidelius.Encrypt(Document, Convert.FromBase64String(HipPrivateKey), HipNonce, HiuPublicKey, HiuNonce).Value.ShouldBe(ReferenceCipherText);
        Fidelius.Encrypt(Document, Convert.FromBase64String(HipPrivateKey), HipNonce, HiuX509PublicKey, HiuNonce).Value.ShouldBe(ReferenceCipherText);
    }

    [Fact]
    public void The_references_ciphertext_decrypts_here_with_its_tamil_intact() =>
        Fidelius.Decrypt(ReferenceCipherText, Convert.FromBase64String(HiuPrivateKey), HiuNonce, HipPublicKey, HipNonce).Value.ShouldBe(Document);

    [Fact]
    public void Altered_data_or_the_wrong_nonce_is_refused()
    {
        var altered = Convert.FromBase64String(ReferenceCipherText);
        altered[5] ^= 1;
        Fidelius.Decrypt(Convert.ToBase64String(altered), Convert.FromBase64String(HiuPrivateKey), HiuNonce, HipPublicKey, HipNonce)
            .Error!.Code.ShouldBe("abdm.fidelius");
        Fidelius.Decrypt(ReferenceCipherText, Convert.FromBase64String(HiuPrivateKey), HipNonce, HipPublicKey, HiuNonce.Replace('l', 'm'))
            .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_key_that_is_not_a_curve25519_point_is_refused()
    {
        var offCurve = Convert.FromBase64String(HiuPublicKey);
        offCurve[40] ^= 1;
        Fidelius.Encrypt(Document, Convert.FromBase64String(HipPrivateKey), HipNonce, Convert.ToBase64String(offCurve), HiuNonce).IsFailure.ShouldBeTrue();
        Fidelius.Encrypt(Document, Convert.FromBase64String(HipPrivateKey), HipNonce, Convert.ToBase64String(new byte[33]), HiuNonce)
            .Error!.Message.ShouldContain("neither");
        Fidelius.Encrypt(Document, Convert.FromBase64String(HipPrivateKey), HipNonce, HiuPublicKey, Convert.ToBase64String(new byte[16]))
            .Error!.Message.ShouldContain("32 bytes");
    }

    [Fact]
    public void A_fresh_key_pair_works_both_ways_and_is_x509_encoded_as_the_reference_encodes_it()
    {
        var hip = Fidelius.Generate();
        hip.PublicKey.Length.ShouldBe(HiuX509PublicKey.Length);
        hip.PublicKey[..300].ShouldBe(HiuX509PublicKey[..300]);
        var sent = Fidelius.Encrypt(Document, hip.PrivateKey, hip.Nonce, HiuPublicKey, HiuNonce).Value;
        Fidelius.Decrypt(sent, Convert.FromBase64String(HiuPrivateKey), HiuNonce, hip.PublicKey, hip.Nonce).Value.ShouldBe(Document);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public void A_private_key_written_as_a_signed_integer_is_read_at_any_length(int length)
    {
        var scalar = new byte[length];
        scalar[^1] = 7;
        Fidelius.Pad(scalar).ShouldBe([.. new byte[31], 7]);
    }

    [Fact]
    public void The_cipher_encrypts_a_whole_transfer_under_one_key_pair_with_each_documents_md5()
    {
        var hiu = new AbdmKeyMaterial("ECDH", "Curve25519", HiuX509PublicKey, HiuNonce);
        var (entries, hip) = new FideliusCipher().Encrypt([("cc-1", Document), ("cc-2", "{}")], hiu).Value;
        entries.Select(e => e.CareContextReference).ShouldBe(["cc-1", "cc-2"]);
        entries[1].Checksum.ShouldBe("99914b932bd37a50b983c5e7c90ae93b");
        foreach (var (entry, plain) in entries.Zip([Document, "{}"]))
        {
            Fidelius.Decrypt(entry.Content, Convert.FromBase64String(HiuPrivateKey), HiuNonce, hip.PublicKey, hip.Nonce).Value.ShouldBe(plain);
        }

        new FideliusCipher().Encrypt([("cc", Document)], hiu with { Curve = "P-256" }).Error!.Message.ShouldContain("Curve25519");
    }
}
