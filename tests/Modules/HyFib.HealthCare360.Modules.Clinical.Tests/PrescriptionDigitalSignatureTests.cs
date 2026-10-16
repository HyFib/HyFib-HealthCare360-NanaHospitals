using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;
using Microsoft.Extensions.Configuration;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// Signing prescriptions with the doctor's DSC (ERX-04): the detached CMS signature HC360 Desktop makes, the server's
/// check of it, the certificates that may sign, and how a signed prescription stays signed. The certificates are
/// throwaway ones made here: self-signed ones, and DSCs from <see cref="TestPki"/>, a root and a licensed CA shaped
/// like the CCA India hierarchy.
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
        check.Chain.ShouldNotBeNull().Trusted.ShouldBeFalse("without DSC roots nothing chains; the service refuses such a signature");
        check.Chain.Problems.ShouldBe([CertificateChainStatus.NoTrustAnchors]);
    }

    [Fact]
    public void Only_a_certificate_issued_under_an_installed_dsc_root_chains_to_it()
    {
        using var pki = new TestPki();
        using var other = new TestPki("Some Other Root");
        using var doctor = pki.Doctor();
        using var selfSigned = Certificate();
        var content = "{}"u8.ToArray();
        var signature = DetachedCms.Sign(content, doctor, Now, silent: true, issuers: [pki.Ca]);

        var trusted = DetachedCms.Verify(content, signature, pki.Anchors);
        trusted.IsValid.ShouldBeTrue(trusted.Message);
        trusted.Chain!.Trusted.ShouldBeTrue(trusted.Chain.Summary);
        trusted.Chain.Summary.ShouldBe(CertificateChainStatus.TrustedSummary);

        DetachedCms.Verify(content, signature, other.Anchors).Chain!.Trusted.ShouldBeFalse("another root is not this DSC's");
        DetachedCms.Verify(content, signature).Chain!.Trusted.ShouldBeFalse("no roots installed: nothing is trusted, whatever the OS trusts");
        DetachedCms.Verify(content, DetachedCms.Sign(content, selfSigned, Now, silent: true), pki.Anchors).Chain!.Trusted
            .ShouldBeFalse("a self-signed certificate (New-SelfSignedCertificate) is not a DSC");

        // A root the signature carries itself is never trusted, only the server's.
        var withOwnRoot = DetachedCms.Sign(content, doctor, Now, silent: true, issuers: [pki.Ca, pki.Root]);
        DetachedCms.Verify(content, withOwnRoot, other.Anchors).Chain!.Trusted.ShouldBeFalse();
    }

    [Fact]
    public void The_licensed_cas_certificate_comes_from_the_signature_or_from_the_trust_folder()
    {
        using var pki = new TestPki();
        using var doctor = pki.Doctor();
        var content = "{}"u8.ToArray();
        var endCertOnly = DetachedCms.Sign(content, doctor, Now, silent: true);

        var withCa = new SignedCms();
        withCa.Decode(DetachedCms.Sign(content, doctor, Now, silent: true, issuers: [pki.Ca]));

        withCa.Certificates.Count.ShouldBe(2);
        DetachedCms.Verify(content, endCertOnly, pki.Anchors).Chain!.Trusted.ShouldBeFalse("the CA certificate is missing");
        DetachedCms.Verify(content, endCertOnly, DscTrustAnchors.Of([pki.Root, pki.Ca])).Chain!.Trusted.ShouldBeTrue();
    }

    [Fact]
    public void Desktop_includes_the_cas_above_the_dsc_but_not_the_root()
    {
        using var pki = new TestPki();
        using var doctor = pki.Doctor();

        var issuers = SigningCertificates.Issuers(doctor, [pki.Ca, pki.Root]);

        issuers.Select(c => c.Thumbprint).ShouldBe([pki.Ca.Thumbprint]);
        foreach (var issuer in issuers)
        {
            issuer.Dispose();
        }
    }

    [Fact]
    public void A_cas_certificate_is_not_a_persons_dsc()
    {
        using var pki = new TestPki();
        using var signingCa = pki.SigningCa();
        var content = "{}"u8.ToArray();

        SigningCertificates.IsEndEntity(pki.Ca).ShouldBeFalse();
        SigningCertificates.Problem(signingCa, Now)!.ShouldContain("Certifying Authority");
        var signature = DetachedCms.Sign(content, signingCa, Now, silent: true);
        DetachedCms.Verify(content, signature, pki.Anchors).Problem.ShouldBe(CmsProblem.CertificateAuthority);
    }

    [Fact]
    public void The_trust_folder_takes_der_and_pem_files_and_says_which_it_could_not_read()
    {
        using var pki = new TestPki();
        var folder = Directory.CreateTempSubdirectory("hc360-dsc-trust-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "cca-india-2022.cer"), pki.Root.RawData);
            File.WriteAllText(Path.Combine(folder, "licensed-ca.pem"), pki.Ca.ExportCertificatePem());
            File.WriteAllText(Path.Combine(folder, "broken.crt"), "not a certificate");
            File.WriteAllText(Path.Combine(folder, "readme.txt"), "ignored");

            var anchors = DscTrustAnchors.Load(folder);

            anchors.Roots.Select(c => c.Thumbprint).ShouldBe([pki.Root.Thumbprint]);
            anchors.Intermediates.Select(c => c.Thumbprint).ShouldBe([pki.Ca.Thumbprint]);
            anchors.Problems.ShouldHaveSingleItem().ShouldStartWith("broken.crt: ");
            DscTrustAnchors.Load(Path.Combine(folder, "missing")).IsEmpty.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_trust_folder_the_server_cannot_read_trusts_nothing_and_says_why_instead_of_failing_every_check()
    {
        using var pki = new TestPki();
        var root = Directory.CreateTempSubdirectory("hc360-dsc-data-").FullName;
        try
        {
            var denied = true;
            var store = new DscTrustStore(new ConfigurationBuilder().Build(), new DataDirectories(new PlatformOptions { DataDirectory = root }))
            {
                ListFiles = folder => denied ? throw new UnauthorizedAccessException($"Access to the path '{folder}' is denied.") : DscTrustAnchors.Files(folder),
            };
            Directory.CreateDirectory(store.Folder);
            File.WriteAllBytes(Path.Combine(store.Folder, "cca-india-2022.cer"), pki.Root.RawData);

            var unreadable = store.Current;

            unreadable.IsEmpty.ShouldBeTrue();
            unreadable.FolderProblem.ShouldBe($"Access to the path '{store.Folder}' is denied.");
            var signed = SignedWithDsc(pki);
            PrescriptionDigitalSignatureService.Check(signed, PrescriptionSignaturePolicy.Required, unreadable, signedForCurrentPatient: true)
                .State.ShouldBe(DigitalSignatureState.Untrusted, "the prescription is as signed; its DSC cannot be confirmed");
            var warning = DscTrustHealthWarnings.Warnings(PrescriptionSignaturePolicy.Required, unreadable, store.Folder).ShouldHaveSingleItem();
            warning.Code.ShouldBe(DscTrustHealthWarnings.UnreadableCode);
            warning.Severity.ShouldBe("critical");
            warning.Message.ShouldContain("cannot read the DSC trust folder");

            // Once the service's account may read it again, the next check reads the roots.
            denied = false;
            store.Current.Roots.Select(c => c.Thumbprint).ShouldBe([pki.Root.Thumbprint]);
            store.Current.FolderProblem.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_trust_folder_without_read_permission_trusts_nothing_and_says_why()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions; on Windows the test above covers the same path with a listing that is refused.");
            return;
        }

        var folder = Directory.CreateTempSubdirectory("hc360-dsc-trust-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "root.cer"), "unused");
            File.SetUnixFileMode(folder, UnixFileMode.None);
            Assert.SkipWhen(CanList(folder), "This account reads any folder whatever its permissions (root).");

            var anchors = DscTrustAnchors.Load(folder);

            anchors.IsEmpty.ShouldBeTrue();
            anchors.FolderProblem.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(folder, recursive: true);
        }

        static bool CanList(string folder)
        {
            try
            {
                _ = Directory.EnumerateFiles(folder).ToList();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
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

    [Theory]
    [InlineData(new byte[] { 0x17, 0x03, 0x41, 0x42, 0x43 })] // a UTCTime that is not a time
    [InlineData(new byte[] { 0x04, 0x02, 0x00, 0x00 })] // not a time at all
    public void A_signing_time_that_cannot_be_read_is_refused_not_thrown(byte[] signingTime)
    {
        using var certificate = Certificate();
        var content = "{}"u8.ToArray();
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") };
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.5"), signingTime));
        cms.ComputeSignature(signer, silent: true);

        var check = DetachedCms.Verify(content, cms.Encode());

        check.IsValid.ShouldBeFalse();
        check.Problem.ShouldBe(CmsProblem.NoSigningTime);
        check.Message.ShouldBe("The signature's signing time cannot be read.");
    }

    [Fact]
    public void A_certificate_whose_key_usage_cannot_be_read_is_refused_not_thrown()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dr Meera Shah, O=Personal, C=IN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509Extension("2.5.29.15", [0x05, 0x00], critical: true)); // key usage that is not a bit string
        using var certificate = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(2));
        var content = "{}"u8.ToArray();

        var check = DetachedCms.Verify(content, DetachedCms.Sign(content, certificate, Now, silent: true));

        check.IsValid.ShouldBeFalse();
        check.Problem.ShouldBe(CmsProblem.NotForSigning);
        SigningCertificates.CanSign(certificate, Now).ShouldBeFalse("nor is it offered on the desktop");
    }

    [Fact]
    public void A_certificate_whose_basic_constraints_cannot_be_read_is_not_taken_for_a_persons()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dr Meera Shah, O=Personal, C=IN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509Extension("2.5.29.19", [0x05, 0x00], critical: true)); // basic constraints that are not a sequence
        using var certificate = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(2));
        var content = "{}"u8.ToArray();

        DetachedCms.Verify(content, DetachedCms.Sign(content, certificate, Now, silent: true)).Problem.ShouldBe(CmsProblem.CertificateAuthority);
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
        using var pki = new TestPki();
        using var doctor = pki.Doctor();
        var prescription = PrescriptionCanonicalFormTests.Signed();
        var content = PrescriptionCanonicalForm.Build(prescription, PrescriptionCanonicalFormTests.Parties);
        var signature = DetachedCms.Sign(content, doctor, Now, silent: true, issuers: [pki.Ca]);
        PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional, pki.Anchors, signedForCurrentPatient: true).State
            .ShouldBe(DigitalSignatureState.NotSigned);

        prescription.AttachDigitalSignature(Evidence(content, signature)).IsSuccess.ShouldBeTrue();
        var check = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional, pki.Anchors, signedForCurrentPatient: true);
        check.State.ShouldBe(DigitalSignatureState.Valid, check.Message);
        check.ContentMatchesRecord.ShouldBeTrue();
        check.ChainTrusted.ShouldBeTrue();
        check.ContentHash.ShouldBe(PrescriptionCanonicalForm.Sha256(content));

        // Moved to another patient's record other than by a merge (SignedForCurrentPatientAsync follows merges).
        var moved = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional, pki.Anchors, signedForCurrentPatient: false);
        moved.State.ShouldBe(DigitalSignatureState.Invalid);
        moved.ContentMatchesRecord.ShouldBeFalse();

        // A change made in the database behind HC360's back: the signature still matches the bytes, the record does not.
        prescription.Items.First().Quantity = 600m;
        var tampered = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional, pki.Anchors, signedForCurrentPatient: true);
        tampered.State.ShouldBe(DigitalSignatureState.Invalid);
        tampered.ContentMatchesRecord.ShouldBeFalse();
        tampered.Message.ShouldBe("The prescription was changed after it was signed. Do not dispense it.");
    }

    [Fact]
    public void A_merge_that_moves_the_prescription_to_the_surviving_record_leaves_the_signature_valid()
    {
        using var pki = new TestPki();
        using var doctor = pki.Doctor();
        var prescription = PrescriptionCanonicalFormTests.Signed();
        var content = PrescriptionCanonicalForm.Build(prescription, PrescriptionCanonicalFormTests.Parties);
        prescription.AttachDigitalSignature(Evidence(content, DetachedCms.Sign(content, doctor, Now, silent: true, issuers: [pki.Ca]))).IsSuccess.ShouldBeTrue();

        prescription.PatientId = Guid.NewGuid(); // As PatientsMergedHandler re-points it.

        PrescriptionDigitalSignatureService.RecordMatches(prescription, content).ShouldBeTrue();
        PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional, pki.Anchors, signedForCurrentPatient: true).State
            .ShouldBe(DigitalSignatureState.Valid);
    }

    [Fact]
    public void A_signature_whose_certificate_no_longer_chains_to_an_installed_root_is_not_shown_as_valid()
    {
        using var pki = new TestPki();
        using var doctor = pki.Doctor();
        var prescription = PrescriptionCanonicalFormTests.Signed();
        var content = PrescriptionCanonicalForm.Build(prescription, PrescriptionCanonicalFormTests.Parties);
        prescription.AttachDigitalSignature(Evidence(content, DetachedCms.Sign(content, doctor, Now, silent: true, issuers: [pki.Ca]))).IsSuccess.ShouldBeTrue();

        var check = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Required, DscTrustAnchors.None, signedForCurrentPatient: true);

        check.State.ShouldBe(DigitalSignatureState.Untrusted);
        check.ChainTrusted.ShouldBeFalse();
        check.ContentMatchesRecord.ShouldBeTrue();
        check.Message!.ShouldContain("could not confirm the certificate as a DSC from a licensed Certifying Authority");
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

    [Fact]
    public void Only_a_signature_that_checks_out_is_printed_with_the_registration_number_the_doctor_signed()
    {
        using var pki = new TestPki();
        using var doctor = pki.Doctor();
        var prescription = PrescriptionCanonicalFormTests.Signed();
        var content = PrescriptionCanonicalForm.Build(prescription, PrescriptionCanonicalFormTests.Parties);
        prescription.AttachDigitalSignature(Evidence(content, DetachedCms.Sign(content, doctor, Now, silent: true, issuers: [pki.Ca]))).IsSuccess.ShouldBeTrue();

        var valid = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional, pki.Anchors, signedForCurrentPatient: true);
        var printed = PrescriptionFinalCopies.PrintSignature(prescription, valid).ShouldNotBeNull();
        printed.RegistrationNumber.ShouldBe("MMC-55821", "the number in the signed bytes, whatever the practitioner master says today");
        printed.SerialNumber.ShouldBe("0A1B");

        // The CCA roots are gone from the trust folder (a restore, a new server): not printed as a DSC signature.
        var untrusted = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional, DscTrustAnchors.None, true);
        untrusted.State.ShouldBe(DigitalSignatureState.Untrusted);
        PrescriptionFinalCopies.PrintSignature(prescription, untrusted).ShouldBeNull();

        // Changed behind HC360's back: not printed as signed either.
        prescription.Items.First().Quantity = 600m;
        var invalid = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional, pki.Anchors, true);
        invalid.State.ShouldBe(DigitalSignatureState.Invalid);
        PrescriptionFinalCopies.PrintSignature(prescription, invalid).ShouldBeNull();
    }

    [Fact]
    public void Where_the_dsc_is_required_only_a_valid_signature_lets_the_prescription_be_issued()
    {
        using var pki = new TestPki();
        using var doctor = pki.Doctor();
        var unsigned = PrescriptionCanonicalFormTests.Signed();
        var prescription = PrescriptionCanonicalFormTests.Signed();
        var content = PrescriptionCanonicalForm.Build(prescription, PrescriptionCanonicalFormTests.Parties);
        prescription.AttachDigitalSignature(Evidence(content, DetachedCms.Sign(content, doctor, Now, silent: true, issuers: [pki.Ca]))).IsSuccess.ShouldBeTrue();
        PrescriptionDigitalSignatureDto Check(Prescription p, PrescriptionSignaturePolicy policy, DscTrustAnchors anchors) =>
            PrescriptionDigitalSignatureService.Check(p, policy, anchors, signedForCurrentPatient: true);

        PrescriptionFinalCopies.Problem(Check(prescription, PrescriptionSignaturePolicy.Required, pki.Anchors)).ShouldBeNull();
        Check(prescription, PrescriptionSignaturePolicy.Required, pki.Anchors).FinalCopyAvailable.ShouldBeTrue();
        PrescriptionFinalCopies.Problem(Check(unsigned, PrescriptionSignaturePolicy.Required, pki.Anchors))!.Code.ShouldBe(PrescriptionFinalCopies.RequiredCode);
        PrescriptionFinalCopies.Problem(Check(prescription, PrescriptionSignaturePolicy.Required, DscTrustAnchors.None))!.Code
            .ShouldBe(PrescriptionFinalCopies.UntrustedCode, "a DSC HC360 cannot confirm counts as none");

        // Where it is optional the prescription prints as signed in HC360, whatever became of the DSC.
        PrescriptionFinalCopies.Problem(Check(unsigned, PrescriptionSignaturePolicy.Optional, pki.Anchors)).ShouldBeNull();
        PrescriptionFinalCopies.Problem(Check(prescription, PrescriptionSignaturePolicy.Optional, DscTrustAnchors.None)).ShouldBeNull();

        prescription.Items.First().Quantity = 600m;
        PrescriptionFinalCopies.Problem(Check(prescription, PrescriptionSignaturePolicy.Required, pki.Anchors))!.Code.ShouldBe(PrescriptionFinalCopies.InvalidCode);
        PrescriptionFinalCopies.Problem(Check(prescription, PrescriptionSignaturePolicy.Optional, pki.Anchors)).ShouldBeNull();
    }

    [Fact]
    public void The_patient_is_told_in_a_few_words_under_the_same_code()
    {
        var pending = PrescriptionDigitalSignatureService.Check(PrescriptionCanonicalFormTests.Signed(), PrescriptionSignaturePolicy.Required, DscTrustAnchors.None, true);

        var problem = PrescriptionFinalCopies.PatientProblem(pending).ShouldNotBeNull();

        problem.Code.ShouldBe(PrescriptionFinalCopies.RequiredCode);
        problem.Message.ShouldBe("Your doctor has not digitally signed this prescription yet.");
        problem.Message.ShouldNotContain("HC360 Desktop");
        PrescriptionFinalCopies.PatientProblem(pending with { Policy = PrescriptionSignaturePolicy.Optional, FinalCopyAvailable = true }).ShouldBeNull();
    }

    [Fact]
    public void A_signature_that_does_not_check_out_reports_no_chain()
    {
        var prescription = PrescriptionCanonicalFormTests.Signed();
        prescription.AttachDigitalSignature(Evidence() with { ChainStatus = CertificateChainStatus.TrustedSummary }).IsSuccess.ShouldBeTrue();

        var check = PrescriptionDigitalSignatureService.Check(prescription, PrescriptionSignaturePolicy.Optional, DscTrustAnchors.None, true);

        check.State.ShouldBe(DigitalSignatureState.Invalid);
        check.ChainTrusted.ShouldBeFalse();
        check.ChainStatus.ShouldBe(PrescriptionDigitalSignatureService.NotChecked, "not the \"Trusted\" stored when it was attached");
    }

    [Theory]
    [InlineData("MEERA SHAH", "Dr. Meera Shah", true)]
    [InlineData("Meera  Shah", "Dr Meera Shah", true)]
    [InlineData("SHAH MEERA", "Dr. Meera Shah", true)]
    [InlineData("MEERA RAJESH SHAH", "Dr. Meera Shah", true)]
    [InlineData("Dr. Meera Shah", "MEERA SHAH", true)]
    [InlineData("मीरा शाह", "Dr मीरा शाह", true)]
    [InlineData("ANIL KUMAR", "Dr. Meera Shah", false)]
    [InlineData("MEERA PATEL", "Dr. Meera Shah", false)]
    [InlineData("NANA HOSPITALS PRIVATE LIMITED", "Dr. Meera Shah", false)]
    [InlineData("MEERA SHAH", "Dr. M. Shah", false)]
    [InlineData("MEERA SHAH", "Dr.", false)]
    [InlineData(null, "Dr. Meera Shah", false)]
    public void A_dsc_is_the_doctors_own_when_it_bears_every_word_of_their_name(string? certificate, string doctor, bool matches) =>
        DscSignerName.Matches(certificate, doctor).ShouldBe(matches);

    [Fact]
    public void A_certificate_whose_details_do_not_fit_their_columns_is_refused_before_saving()
    {
        var fits = new SignerCertificate("CN=Dr Meera Shah, C=IN", "Dr Meera Shah", "0A1B", "CN=Test CA", "ABCDEF", Now, Now.AddYears(1));

        PrescriptionDigitalSignatureService.Unstorable(fits).ShouldBeNull();
        PrescriptionDigitalSignatureService.Unstorable(fits with { SignerName = new string('A', Prescription.SignerNameMaxLength + 1) }).ShouldBe("name");
        PrescriptionDigitalSignatureService.Unstorable(fits with { Subject = "CN=" + new string('A', Prescription.SignerDistinguishedNameMaxLength) })
            .ShouldBe("subject");
        PrescriptionDigitalSignatureService.Unstorable(fits with { Issuer = "CN=" + new string('A', Prescription.SignerDistinguishedNameMaxLength) })
            .ShouldBe("issuer");
        PrescriptionDigitalSignatureService.Unstorable(fits with { SerialNumber = new string('0', Prescription.SignerSerialNumberMaxLength + 1) })
            .ShouldBe("serial number");
    }

    [Fact]
    public void The_patient_is_told_once_that_the_prescription_is_ready()
    {
        var prescription = PrescriptionCanonicalFormTests.Signed();

        prescription.MarkPatientNotified(Now).ShouldBeTrue();
        prescription.MarkPatientNotified(Now.AddMinutes(5)).ShouldBeFalse();

        prescription.PatientNotifiedAt.ShouldBe(Now);
        var shared = prescription.DomainEvents.OfType<PrescriptionSharedWithPatient>().ShouldHaveSingleItem();
        shared.PrescriptionId.ShouldBe(prescription.Id);
        shared.SignedAt.ShouldBe(prescription.SignedAt!.Value);
        new Prescription { PrescriptionNumber = "NHRX1" }.MarkPatientNotified(Now).ShouldBeFalse("a draft is not ready");
    }

    [Fact]
    public void A_message_held_for_the_dsc_is_let_go_once_whether_it_was_sent_or_not()
    {
        var prescription = PrescriptionCanonicalFormTests.Signed();
        prescription.HoldPatientNotice(Now);
        prescription.HoldPatientNotice(Now.AddMinutes(1));
        prescription.PatientNoticeHeldAt.ShouldBe(Now, "held from the first time");

        prescription.MarkPatientNotified(Now.AddMinutes(5)).ShouldBeTrue();
        prescription.PatientNoticeHeldAt.ShouldBeNull("sent: no longer held");
        prescription.HoldPatientNotice(Now.AddMinutes(6));
        prescription.PatientNoticeHeldAt.ShouldBeNull("the patient was told already");

        var noPhone = PrescriptionCanonicalFormTests.Signed();
        noPhone.HoldPatientNotice(Now);
        noPhone.ReleasePatientNotice();
        noPhone.PatientNoticeHeldAt.ShouldBeNull();
        noPhone.PatientNotifiedAt.ShouldBeNull("let go without a message");

        var cancelled = PrescriptionCanonicalFormTests.Signed();
        cancelled.HoldPatientNotice(Now);
        cancelled.Cancel("Wrong patient", Now);
        cancelled.PatientNoticeHeldAt.ShouldBeNull("a cancelled prescription is never announced");

        var draft = new Prescription { PrescriptionNumber = "NHRX1" };
        draft.HoldPatientNotice(Now);
        draft.PatientNoticeHeldAt.ShouldBeNull();
        var inpatient = new Prescription { PrescriptionNumber = "NHRX2", IsInpatient = true };
        inpatient.HoldPatientNotice(Now);
        inpatient.PatientNoticeHeldAt.ShouldBeNull("in-patients are not sent the message");
    }

    [Fact]
    public void System_health_warns_when_dsc_signing_is_on_and_the_trust_folder_is_empty()
    {
        using var pki = new TestPki();
        const string folder = @"C:\ProgramData\HyFib\HealthCare360\certs\dsc-trust";

        DscTrustHealthWarnings.Warnings(PrescriptionSignaturePolicy.Off, DscTrustAnchors.None, folder).ShouldBeEmpty();
        DscTrustHealthWarnings.Warnings(PrescriptionSignaturePolicy.Required, pki.Anchors, folder).ShouldBeEmpty();
        var optional = DscTrustHealthWarnings.Warnings(PrescriptionSignaturePolicy.Optional, DscTrustAnchors.None, folder).ShouldHaveSingleItem();
        optional.Code.ShouldBe(DscTrustHealthWarnings.EmptyCode);
        optional.Severity.ShouldBe("warning");
        optional.Message.ShouldContain(folder);
        var required = DscTrustHealthWarnings.Warnings(PrescriptionSignaturePolicy.Required, DscTrustAnchors.None, folder).ShouldHaveSingleItem();
        required.Severity.ShouldBe("critical");
        required.Message.ShouldContain("cannot be printed or shared");
        DscTrustHealthWarnings.Warnings(PrescriptionSignaturePolicy.Optional, DscTrustAnchors.Of([pki.Root], ["broken.crt: not a certificate"]), folder)
            .ShouldHaveSingleItem().Code.ShouldBe(DscTrustHealthWarnings.UnreadableCode);
    }

    /// <summary>A signed prescription with a DSC signature from <paramref name="pki"/>'s doctor, as the server keeps it.</summary>
    private static Prescription SignedWithDsc(TestPki pki)
    {
        using var doctor = pki.Doctor();
        var prescription = PrescriptionCanonicalFormTests.Signed();
        var content = PrescriptionCanonicalForm.Build(prescription, PrescriptionCanonicalFormTests.Parties);
        prescription.AttachDigitalSignature(Evidence(content, DetachedCms.Sign(content, doctor, Now, silent: true, issuers: [pki.Ca]))).IsSuccess
            .ShouldBeTrue();
        return prescription;
    }

    private static PrescriptionDigitalSignature Evidence(byte[]? content = null, byte[]? signature = null) => new(
        signature ?? [1], content ?? [2], PrescriptionCanonicalForm.Sha256(content ?? [2]), Now, Guid.NewGuid(), "Dr Meera Shah", "CN=Dr Meera Shah",
        "0A1B", "CN=Test CA", "ABCDEF", "Not trusted");

    /// <summary>A throwaway self-signed RSA certificate for "Dr Meera Shah", with the key usage a DSC has unless told otherwise.</summary>
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

/// <summary>
/// A throwaway PKI shaped like India's for DSCs: a root (the CCA India root's place), a licensed CA under it, and doctors'
/// certificates under that, each with the key usage and basic constraints a real one has.
/// </summary>
internal sealed class TestPki : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public TestPki(string rootName = "Test CCA India Root")
    {
        using var rootKey = RSA.Create(2048);
        var root = new CertificateRequest($"CN={rootName}, O=Test, C=IN", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(root, issuer: null);
        Root = root.CreateSelfSigned(Now.AddYears(-1), Now.AddYears(10));

        using var caKey = RSA.Create(2048);
        var ca = new CertificateRequest($"CN=Test Licensed CA under {rootName}, O=Test, C=IN", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(ca, Root);
        using var caPublic = ca.Create(Root, Now.AddMonths(-11), Now.AddYears(5), Serial());
        Ca = caPublic.CopyWithPrivateKey(caKey);
    }

    /// <summary>The root, as installed in the server's DSC trust folder.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>The licensed CA (an intermediate), with its private key.</summary>
    public X509Certificate2 Ca { get; }

    /// <summary>The server's trust folder holding just <see cref="Root"/>.</summary>
    public DscTrustAnchors Anchors => DscTrustAnchors.Of([Root]);

    /// <summary>A doctor's DSC from <see cref="Ca"/>, with its private key.</summary>
    public X509Certificate2 Doctor(string name = "Dr Meera Shah")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}, O=Personal, C=IN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        using var publicOnly = request.Create(Ca, Now.AddDays(-1), Now.AddYears(2), Serial());
        return publicOnly.CopyWithPrivateKey(key);
    }

    /// <summary><see cref="Ca"/> with a key usage that would also allow signing documents, as some CAs' do.</summary>
    public X509Certificate2 SigningCa()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test CA that signs, O=Test, C=IN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        using var publicOnly = request.Create(Root, Now.AddDays(-1), Now.AddYears(2), Serial());
        return publicOnly.CopyWithPrivateKey(key);
    }

    public void Dispose()
    {
        Root.Dispose();
        Ca.Dispose();
    }

    private static void AddCaExtensions(CertificateRequest request, X509Certificate2? issuer)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, issuer is not null, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (issuer is not null)
        {
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        }
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        return serial;
    }
}
