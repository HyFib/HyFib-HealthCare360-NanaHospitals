using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;

namespace HyFib.HealthCare360.Modules.Interop.Tests.Abdm;

/// <summary>
/// ABDM's ABHA service takes identifiers and codes encrypted with its public key under RSA-OAEP with SHA-1, base64
/// (<see cref="AbhaClient.Encrypt"/>); its key comes as a base64 or PEM public key or certificate, bare or in JSON
/// (<see cref="AbhaClient.ReadPublicKey"/>); and an account's date of birth in more than one form (<see cref="AbhaVerificationService.YearOf"/>).
/// </summary>
public sealed class AbhaClientTests
{
    [Fact]
    public void A_value_is_encrypted_for_the_holder_of_the_private_key_under_oaep_sha1()
    {
        using var key = RSA.Create(2048);
        var encrypted = AbhaClient.Encrypt(key.ExportSubjectPublicKeyInfo(), "91234567890123");

        Encoding.UTF8.GetString(key.Decrypt(Convert.FromBase64String(encrypted), RSAEncryptionPadding.OaepSHA1)).ShouldBe("91234567890123");
        Should.Throw<CryptographicException>(() => key.Decrypt(Convert.FromBase64String(encrypted), RSAEncryptionPadding.OaepSHA256));
        AbhaClient.Encrypt(key.ExportSubjectPublicKeyInfo(), "91234567890123").ShouldNotBe(encrypted);
    }

    [Fact]
    public void The_public_key_is_read_from_json_base64_pem_or_a_certificate()
    {
        using var key = RSA.Create(2048);
        var spki = key.ExportSubjectPublicKeyInfo();
        var base64 = Convert.ToBase64String(spki);
        var request = new CertificateRequest("CN=abha.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        AbhaClient.ReadPublicKey($$"""{"publicKey":"{{base64}}"}""").ShouldBe(spki);
        AbhaClient.ReadPublicKey(base64).ShouldBe(spki);
        AbhaClient.ReadPublicKey(key.ExportSubjectPublicKeyInfoPem()).ShouldBe(spki);
        AbhaClient.ReadPublicKey(certificate.ExportCertificatePem()).ShouldBe(spki);
        AbhaClient.ReadPublicKey($$"""{"certificate":"{{Convert.ToBase64String(certificate.RawData)}}"}""").ShouldBe(spki);
        AbhaClient.ReadPublicKey("not a key").ShouldBeNull();
        AbhaClient.ReadPublicKey("""{"other":"x"}""").ShouldBeNull();
        AbhaClient.ReadPublicKey(Convert.ToBase64String([1, 2, 3])).ShouldBeNull();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"yes\"", null)]
    [InlineData("null", null)]
    [InlineData("{}", null)]
    public void An_accounts_kyc_flag_is_read_as_a_boolean_or_a_string(string json, bool? expected)
    {
        var result = System.Text.Json.JsonSerializer.Deserialize<AbhaLoginResult>(
            $$$"""{"authResult":"success","accounts":[{"ABHANumber":"91-2345-6789-0123","kycVerified":{{{json}}}}]}""", AbdmGatewayMessages.Json)!;
        var account = result.Accounts.ShouldNotBeNull().ShouldHaveSingleItem();
        account.KycVerified.ShouldBe(expected);
        account.AbhaNumber.ShouldBe("91-2345-6789-0123");
        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("15-06-1990", 1990)]
    [InlineData("1990-06-15", 1990)]
    [InlineData("1990", 1990)]
    [InlineData("15/6/2001", 2001)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("15-06-90", null)]
    public void An_accounts_year_of_birth_is_read_from_its_date(string? dob, int? year) =>
        AbhaVerificationService.YearOf(dob).ShouldBe(year);
}
