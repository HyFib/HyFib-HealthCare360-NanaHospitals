using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// Signing prescriptions with the doctor's DSC (ERX-04): the detached CMS signature HC360 Desktop makes, the server's
/// check of it, the certificates that may sign, and how a signed prescription stays signed. The certificates are
/// throwaway self-signed ones made here; a real DSC differs only in its issuer.
/// </summary>
public sealed class PrescriptionDigitalSignatureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_signature_made_with_a_signing_certificate_verifies_against_the_same_bytes()
    {
        using var certificate = Certificate();
        var content = PrescriptionCanonicalForm.Build(PrescriptionCanonicalFormTests.Signed(), PrescriptionCanonicalFormTests.Parties);

        var signature = DetachedCms.Sign(content, certificate, Now, silent: true);
        var check = DetachedCms.Verify(content, signature);

        check.IsValid.ShouldBeTrue(check.Message);
        check.Problem.ShouldBeNull();
        check.Signer!.SignerName.ShouldBe("Dr Meera Shah");
        check.Signer.SerialNumber.ShouldBe(certificate.SerialNumber);
        check.Signer.Thumbprint.ShouldBe(certificate.Thumbprint);
        check.SignedAt!.Value.ShouldBe(Now, TimeSpan.FromSeconds(1));
        check.Chain.ShouldNotBeNull().Trusted.ShouldBeFalse("a self-signed certificate does not chain to a trusted root, which is reported, not refused");
    }

    [Fact]
    public void The_signature_is_detached_sha256_with_the_signers_certificate_and_signing_time()
    {
        using var certificate = Certificate();
        var content = "{\"format\":\"hc360.prescription.v1\"}"u8.ToArray();

        var cms = new SignedCms();
        cms.Decode(DetachedCms.Sign(content, certificate, Now, silent: true));

        cms.ContentInfo.Content.ShouldBeEmpty("the signature does not carry the prescription");
        cms.SignerInfos.Count.ShouldBe(1);
        cms.SignerInfos[0].DigestAlgorithm.Value.ShouldBe("2.16.840.1.101.3.4.2.1");
        cms.Certificates.Count.ShouldBe(1);
        cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().SelectMany(a => a.Values.Cast<AsnEncodedData>())
            .OfType<Pkcs9SigningTime>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Changed_bytes_do_not_verify()
    {
        using var certificate = Certificate();
        var content = PrescriptionCanonicalForm.Build(PrescriptionCanonicalFormTests.Signed(), PrescriptionCanonicalFormTests.Parties);
        var signature = DetachedCms.Sign(content, certificate, Now, silent: true);
        var changed = PrescriptionCanonicalForm.Build(PrescriptionCanonicalFormTests.Signed(advice: "Rest."), PrescriptionCanonicalFormTests.Parties);

        var check = DetachedCms.Verify(changed, signature);

        check.IsValid.ShouldBeFalse();
        check.Problem.ShouldBe(CmsProblem.BadSignature);
    }

    [Fact]
    public void Anything_but_a_cms_signature_is_refused() =>
        DetachedCms.Verify([1, 2, 3], [0x30, 0x03, 0x02, 0x01, 0x01]).Problem.ShouldBe(CmsProblem.Unreadable);

    [Fact]
    public void A_certificate_that_was_not_valid_when_signing_is_refused()
    {
        using var certificate = Certificate(notBefore: Now.AddYears(-3), notAfter: Now.AddYears(-1));
        var content = "{}"u8.ToArray();

        var check = DetachedCms.Verify(content, DetachedCms.Sign(content, certificate, Now, silent: true));

        check.Problem.ShouldBe(CmsProblem.CertificateNotValidAtSigningTime);
    }

    [Fact]
    public void A_certificate_not_meant_for_signing_is_refused()
    {
        using var certificate = Certificate(usage: X509KeyUsageFlags.KeyEncipherment);
        var content = "{}"u8.ToArray();

        DetachedCms.Verify(content, DetachedCms.Sign(content, certificate, Now, silent: true)).Problem.ShouldBe(CmsProblem.NotForSigning);
    }

    [Fact]
    public void A_sha1_signature_is_refused()
    {
        using var certificate = Certificate();
        var content = "{}"u8.ToArray();
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(certificate) { DigestAlgorithm = new Oid("1.3.14.3.2.26") };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(Now.UtcDateTime));
        cms.ComputeSignature(signer, silent: true);

        DetachedCms.Verify(content, cms.Encode()).Problem.ShouldBe(CmsProblem.WeakDigest);
    }

    [Fact]
    public void A_signature_without_a_signing_time_is_refused()
    {
        using var certificate = Certificate();
        var content = "{}"u8.ToArray();
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.ComputeSignature(new CmsSigner(certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") }, silent: true);

        DetachedCms.Verify(content, cms.Encode()).Problem.ShouldBe(CmsProblem.NoSigningTime);
    }

    [Fact]
    public void Only_certificates_with_a_private_key_valid_now_and_meant_for_signing_are_offered()
    {
        using var signing = Certificate();
        using var nonRepudiation = Certificate(usage: X509KeyUsageFlags.NonRepudiation);
        using var encryption = Certificate(usage: X509KeyUsageFlags.KeyEncipherment);
        using var expired = Certificate(notBefore: Now.AddYears(-3), notAfter: Now.AddDays(-1));
        using var noUsage = Certificate(usage: null);
        using var publicOnly = X509CertificateLoader.LoadCertificate(signing.RawData);

        SigningCertificates.Problem(signing, Now).ShouldBeNull();
        SigningCertificates.Problem(nonRepudiation, Now).ShouldBeNull();
        SigningCertificates.Problem(encryption, Now).ShouldNotBeNull();
        SigningCertificates.Problem(expired, Now).ShouldNotBeNull();
        SigningCertificates.Problem(noUsage, Now).ShouldNotBeNull();
        SigningCertificates.Problem(publicOnly, Now)!.ShouldContain("private key");
        SigningCertificates.Usable([encryption, signing, expired, publicOnly, nonRepudiation], Now).Select(c => c.Thumbprint)
            .ShouldBe([signing.Thumbprint, nonRepudiation.Thumbprint], ignoreOrder: true);
    }

    [Fact]
    public void Desktop_reads_what_it_signs_from_the_bytes_themselves()
    {
        var content = PrescriptionCanonicalForm.Build(PrescriptionCanonicalFormTests.Signed(), PrescriptionCanonicalFormTests.Parties);

        var request = PrescriptionSigningRequest.Parse(Convert.ToBase64String(content));

        request.Content.ShouldBe(content);
        request.PrescriptionNumber.ShouldBe("NHRX260000123");
        request.PatientName.ShouldBe("Asha Rao");
        request.Medicines.ShouldBe(["Metformin 500 mg", "Tramadol \"SR\" 50 mg"]);
        request.Summary.ShouldStartWith("Prescription NHRX260000123 for Asha Rao (UHID NH-000123), by Dr Meera Shah.");
        request.Summary.ShouldContain("• Metformin 500 mg");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not base64!")]
    [InlineData("e30=")] // {}
    [InlineData("eyJmb3JtYXQiOiJvdGhlciJ9")] // {"format":"other"}
    public void Desktop_refuses_anything_but_a_prescription(string? content) =>
        Should.Throw<FormatException>(() => PrescriptionSigningRequest.Parse(content));

    [Fact]
    public void A_signed_prescription_takes_one_dsc_signature_and_keeps_it()
    {
        var prescription = PrescriptionCanonicalFormTests.Signed();

        prescription.AttachDigitalSignature(Evidence()).IsSuccess.ShouldBeTrue();
        prescription.IsDigitallySigned.ShouldBeTrue();
        prescription.SignerSerialNumber.ShouldBe("0A1B");

        var again = prescription.AttachDigitalSignature(Evidence() with { SignerSerialNumber = "FFFF" });
        again.Error!.Code.ShouldBe("prescription.already_digitally_signed");
        prescription.SignerSerialNumber.ShouldBe("0A1B");
        prescription.ReplaceItems([]).Error!.Code.ShouldBe("prescription.not_draft", "a signed prescription's medicines never change");
    }

    [Fact]
    public void A_draft_or_cancelled_prescription_takes_no_dsc_signature()
    {
        var draft = new Prescription { PrescriptionNumber = "NHRX1" };
        draft.AttachDigitalSignature(Evidence()).Error!.Code.ShouldBe("prescription.not_signed");

        var cancelled = PrescriptionCanonicalFormTests.Signed();
        cancelled.Cancel("Wrong patient", Now);
        cancelled.AttachDigitalSignature(Evidence()).Error!.Code.ShouldBe("prescription.not_signed");
    }

    [Fact]
    public void The_stored_signature_checks_out_until_the_record_is_changed()
    {
        using var certificate = Certificate();
        var prescription = PrescriptionCanonicalFormTests.Signed();
        var content = PrescriptionCanonicalForm.Build(prescription, PrescriptionCanonicalFormTests.Parties);
        var signature = DetachedCms.Sign(content, certificate, Now, silent: true);
        PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional).State.ShouldBe(DigitalSignatureState.NotSigned);

        prescription.AttachDigitalSignature(Evidence(content, signature)).IsSuccess.ShouldBeTrue();
        var check = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional);
        check.State.ShouldBe(DigitalSignatureState.Valid, check.Message);
        check.ContentMatchesRecord.ShouldBeTrue();
        check.ContentHash.ShouldBe(PrescriptionCanonicalForm.Sha256(content));

        // A change made in the database behind HC360's back: the signature still matches the bytes, the record does not.
        prescription.Items.First().Quantity = 600m;
        var tampered = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional);
        tampered.State.ShouldBe(DigitalSignatureState.Invalid);
        tampered.ContentMatchesRecord.ShouldBeFalse();
        tampered.Message.ShouldBe("The prescription was changed after it was signed. Do not dispense it.");
    }

    [Theory]
    [InlineData(null, PrescriptionSignaturePolicy.Off)]
    [InlineData("\"off\"", PrescriptionSignaturePolicy.Off)]
    [InlineData("\"optional\"", PrescriptionSignaturePolicy.Optional)]
    [InlineData("\" Required \"", PrescriptionSignaturePolicy.Required)]
    [InlineData("\"2\"", PrescriptionSignaturePolicy.Off)]
    [InlineData("true", PrescriptionSignaturePolicy.Off)]
    [InlineData("\"mandatory\"", PrescriptionSignaturePolicy.Off)]
    public void The_facility_setting_reads_leniently_and_defaults_to_off(string? json, PrescriptionSignaturePolicy expected) =>
        PrescriptionSignatureSettings.Parse(json is null ? null : JsonDocument.Parse(json).RootElement).ShouldBe(expected);

    [Fact]
    public void The_printed_copy_names_the_signer_the_certificate_and_the_hash()
    {
        var model = new PrescriptionPrintModel(Guid.NewGuid(), "NHRX260000123", PrescriptionStatus.Signed, Now, null, null,
            new PrescriptionPrintPatient(Guid.NewGuid(), "NH-000123", "Asha Rao", "34 y", Gender.Female, null), [], [], [], null, null, null, 0,
            "Zk3_9aB-qR7tLm2xWv5cAbCd", null, null,
            new PrescriptionPrintSignature("Dr Meera Shah", "MMC-55821", "0A1B2C", new DateTimeOffset(2026, 9, 25, 10, 15, 0, TimeSpan.Zero), new string('a', 64)));

        var body = PrescriptionDocument.Body(model, null, null).ToString();

        body.ShouldContain("Digitally signed by Dr Meera Shah (MMC-55821) — certificate 0A1B2C, 25 Sep 2026, 3:45 pm");
        body.ShouldContain($"SHA-256 {new string('a', 64)}");
        PrescriptionDocument.Body(model with { DigitalSignature = null }, null, null).ToString().ShouldContain("Signed electronically on");
    }

    private static PrescriptionDigitalSignature Evidence(byte[]? content = null, byte[]? signature = null) => new(
        signature ?? [1], content ?? [2], PrescriptionCanonicalForm.Sha256(content ?? [2]), Now, Guid.NewGuid(), "Dr Meera Shah", "CN=Dr Meera Shah",
        "0A1B", "CN=Test CA", "ABCDEF", "Not trusted");

    /// <summary>A throwaway RSA certificate for "Dr Meera Shah", with the key usage a DSC has unless told otherwise.</summary>
    internal static X509Certificate2 Certificate(
        X509KeyUsageFlags? usage = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dr Meera Shah, O=Personal, C=IN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (usage is { } flags)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(flags, critical: true));
        }

        return request.CreateSelfSigned(notBefore ?? Now.AddDays(-1), notAfter ?? Now.AddYears(2));
    }
}
