using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using HyFib.HealthCare360.Integrations.Aadhaar;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.WindowsInterop.Biometrics;

namespace HyFib.HealthCare360.Integrations.Tests.Aadhaar;

/// <summary>
/// The Aadhaar Auth API 2.5 request HC360 signs and UIDAI's answer (INT-BIO). The capture is a registered device's
/// <c>PidData</c>; its encrypted parts must reach UIDAI exactly as the device made them.
/// </summary>
public sealed class UidaiAuthXmlTests
{
    /// <summary>UIDAI's published staging test number (it passes the Verhoeff check).</summary>
    internal const string TestUid = "999941057058";

    internal const string Capture =
        """
        <PidData>
          <Resp errCode="0" errInfo="Capture Success" fCount="1" fType="2" nmPoints="38" qScore="74" />
          <DeviceInfo dpId="MANTRA.MSIPL" rdsId="MANTRA.WIN.001" rdsVer="1.0.8" mi="MFS110" mc="MIIEGDCCAwCgAwIBAgIE" dc="b6a5c1f2-30c4-4f8e-9a0e-1f4a7c9e2d11" />
          <Skey ci="20291231">U0tFWS1lbmNyeXB0ZWQtZm9yLVVJREFJ</Skey>
          <Hmac>SE1BQy1lbmNyeXB0ZWQ=</Hmac>
          <Data type="X">UElELWVuY3J5cHRlZC1mb3ItVUlEQUk=</Data>
        </PidData>
        """;

    internal static readonly AadhaarSettings Settings = new()
    {
        Enabled = true,
        Provider = "uidai",
        AuthUrl = "https://asa.example.in/2.5/{ac}/{uid0}/{uid1}/{asalk}",
        AuaCode = "HC0000001",
        SubAuaCode = "NANA01",
    };

    private static AadhaarAuthRequest Request(string uid = TestUid) => new(uid, PidCapture.Parse(Capture), "HC360A1B2C3D4E5F6A7B");

    [Fact]
    public void Builds_the_request_from_the_number_the_capture_and_the_licence()
    {
        var auth = UidaiAuthXml.Build(Request(), Settings, "LICENCE-KEY", "HC360TXN1");
        XNamespace ns = UidaiAuthXml.Namespace;

        auth.Name.ShouldBe(ns + "Auth");
        Attributes(auth).ShouldBe(new Dictionary<string, string>
        {
            ["uid"] = TestUid,
            ["rc"] = "Y",
            ["tid"] = "registered",
            ["ac"] = "HC0000001",
            ["sa"] = "NANA01",
            ["ver"] = "2.5",
            ["txn"] = "HC360TXN1",
            ["lk"] = "LICENCE-KEY",
        });
        auth.Elements().Select(e => e.Name.LocalName).ShouldBe(["Uses", "Meta", "Skey", "Data", "Hmac"]);
        Attributes(auth.Element(ns + "Uses")!).ShouldBe(new Dictionary<string, string>
        {
            ["pi"] = "n",
            ["pa"] = "n",
            ["pfa"] = "n",
            ["bio"] = "y",
            ["bt"] = "FMR,FIR",
            ["pin"] = "n",
            ["otp"] = "n",
        });
        Attributes(auth.Element(ns + "Meta")!).ShouldBe(new Dictionary<string, string>
        {
            ["udc"] = "HC360A1B2C3D4E5F6A7B",
            ["rdsId"] = "MANTRA.WIN.001",
            ["rdsVer"] = "1.0.8",
            ["dpId"] = "MANTRA.MSIPL",
            ["dc"] = "b6a5c1f2-30c4-4f8e-9a0e-1f4a7c9e2d11",
            ["mi"] = "MFS110",
            ["mc"] = "MIIEGDCCAwCgAwIBAgIE",
        });
        auth.Element(ns + "Skey")!.Attribute("ci")!.Value.ShouldBe("20291231");
        auth.Element(ns + "Skey")!.Value.ShouldBe("U0tFWS1lbmNyeXB0ZWQtZm9yLVVJREFJ");
        auth.Element(ns + "Data")!.Attribute("type")!.Value.ShouldBe("X");
        auth.Element(ns + "Data")!.Value.ShouldBe("UElELWVuY3J5cHRlZC1mb3ItVUlEQUk=");
        auth.Element(ns + "Hmac")!.Value.ShouldBe("SE1BQy1lbmNyeXB0ZWQ=");
    }

    [Fact]
    public void Takes_the_device_type_the_asa_asks_for()
    {
        var auth = UidaiAuthXml.Build(Request(), Settings with { RegisteredDeviceTid = "R" }, "LICENCE-KEY", "HC360TXN1");

        auth.Attribute("tid")!.Value.ShouldBe("R");
    }

    [Fact]
    public void Names_the_biometric_type_the_device_captured()
    {
        var minutiae = new AadhaarAuthRequest(TestUid, PidCapture.Parse(Capture.Replace("fType=\"2\"", "fType=\"0\"", StringComparison.Ordinal)), "HC360X");

        UidaiAuthXml.Build(minutiae, Settings, "LK", "T").Element(XName.Get("Uses", UidaiAuthXml.Namespace))!.Attribute("bt")!.Value.ShouldBe("FMR");
    }

    [Fact]
    public void Signs_the_whole_request_with_the_aua_certificate()
    {
        using var aua = Certificates.Create("CN=Nana Hospitals AUA");

        var signed = UidaiAuthXml.Sign(UidaiAuthXml.Build(Request(), Settings, "LICENCE-KEY", "HC360TXN1"), aua);

        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(signed);
        var signature = (XmlElement)document.DocumentElement!.LastChild!;
        signature.LocalName.ShouldBe("Signature");
        var check = new SignedXml(document);
        check.LoadXml(signature);
        check.CheckSignature(aua, verifySignatureOnly: true).ShouldBeTrue();
        check.SignatureMethod.ShouldBe(SignedXml.XmlDsigRSASHA256Url);
        ((Reference)check.SignedInfo!.References[0]!).Uri.ShouldBe(string.Empty);
        signed.ShouldContain("<X509Certificate>");

        // Changing what was signed breaks the signature.
        document.DocumentElement.SetAttribute("uid", "999971658847");
        check.CheckSignature(aua, verifySignatureOnly: true).ShouldBeFalse();
    }

    [Fact]
    public void Addresses_the_request_by_the_numbers_first_two_digits()
    {
        UidaiAuthXml.Address(Settings.AuthUrl!, "HC0000001", TestUid, "ASA/KEY+1").ShouldBe(
            new Uri("https://asa.example.in/2.5/HC0000001/9/9/ASA%2FKEY%2B1"));
    }

    [Fact]
    public void Addresses_a_virtual_id_with_zeros()
    {
        UidaiAuthXml.Address(Settings.AuthUrl!, "HC0000001", "9134567890123456", "KEY").ShouldBe(
            new Uri("https://asa.example.in/2.5/HC0000001/0/0/KEY"));
    }

    [Fact]
    public void Reads_uidais_answer()
    {
        UidaiAuthXml.ParseResponse("""<AuthRes ret="y" code="7c2c8a2b12f34b5d9e6f" txn="HC360TXN1" err="" ts="2026-10-20T12:30:00.000+05:30" actn="" info="04{...}" />""")
            .ShouldBe(new AuthResponse(true, "7c2c8a2b12f34b5d9e6f", "HC360TXN1", null, "2026-10-20T12:30:00.000+05:30"));
        UidaiAuthXml.ParseResponse("""<AuthRes xmlns="http://www.uidai.gov.in/authentication/uid-auth-response/1.0" ret="n" code="NA" txn="T" err="300" />""")
            .ShouldBe(new AuthResponse(false, "NA", "T", "300", null));
    }

    [Theory]
    [InlineData("""<!DOCTYPE AuthRes [<!ENTITY x "y">]><AuthRes ret="&x;" txn="T" />""")]
    [InlineData("""<Error code="999" />""")]
    [InlineData("""<AuthRes ret="maybe" txn="T" />""")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("")]
    public void Refuses_an_answer_that_is_not_uidais(string xml)
    {
        Should.Throw<FormatException>(() => UidaiAuthXml.ParseResponse(xml));
    }

    [Fact]
    public void Believes_only_an_answer_uidai_signed_whole()
    {
        using var uidai = Certificates.Create("CN=UIDAI");
        using var other = Certificates.Create("CN=Someone else");
        const string answer = """<AuthRes ret="y" code="C1" txn="T1" err="" ts="2026-10-20T12:30:00" />""";
        var signed = Certificates.SignWhole(answer, uidai);

        UidaiAuthXml.HasUidaiSignature(signed, uidai).ShouldBeTrue();
        UidaiAuthXml.HasUidaiSignature(answer, uidai).ShouldBeFalse();
        UidaiAuthXml.HasUidaiSignature(Certificates.SignWhole(answer, other), uidai).ShouldBeFalse();
        UidaiAuthXml.HasUidaiSignature(signed.Replace("ret=\"y\"", "ret=\"n\"", StringComparison.Ordinal), uidai).ShouldBeFalse();
        UidaiAuthXml.HasUidaiSignature(signed.Replace("code=\"C1\"", "code=\"C2\"", StringComparison.Ordinal), uidai).ShouldBeFalse();
    }

    [Fact]
    public void Does_not_take_a_signature_moved_into_the_answer_or_a_second_one()
    {
        using var uidai = Certificates.Create("CN=UIDAI");
        var signed = Certificates.SignWhole("""<AuthRes ret="n" code="C1" txn="T1" err="300" />""", uidai);
        var signature = signed[signed.IndexOf("<Signature", StringComparison.Ordinal)..signed.IndexOf("</AuthRes>", StringComparison.Ordinal)];

        // The genuine signature of a refusal, wrapped inside an element of a forged acceptance.
        var wrapped = $"""<AuthRes ret="y" code="C1" txn="T1" err=""><Extra>{signature}</Extra></AuthRes>""";
        UidaiAuthXml.HasUidaiSignature(wrapped, uidai).ShouldBeFalse();
        UidaiAuthXml.HasUidaiSignature(signed.Replace("</AuthRes>", signature + "</AuthRes>", StringComparison.Ordinal), uidai).ShouldBeFalse();
    }

    [Theory]
    [InlineData("300", AadhaarCheckOutcome.NotVerified, "did not match")]
    [InlineData("330", AadhaarCheckOutcome.NotVerified, "locked their biometrics")]
    [InlineData("998", AadhaarCheckOutcome.NotVerified, "cannot authenticate this Aadhaar number")]
    [InlineData("527", AadhaarCheckOutcome.Failed, "could not read the capture")]
    [InlineData("520", AadhaarCheckOutcome.Failed, "Tell your administrator")]
    [InlineData("812", AadhaarCheckOutcome.Failed, "UIDAI code 812")]
    [InlineData(null, AadhaarCheckOutcome.Failed, "UIDAI code none")]
    public void Says_what_a_refusal_means_for_the_person(string? code, AadhaarCheckOutcome outcome, string words)
    {
        var (judged, message) = UidaiAuthXml.Refusal(code);

        judged.ShouldBe(outcome);
        message.ShouldContain(words);
    }

    [Fact]
    public void Transaction_ids_are_unique_and_within_uidais_limit()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => UidaiAuthXml.NewTransactionId()).ToList();

        ids.Distinct().Count().ShouldBe(100);
        ids.ShouldAllBe(id => id.Length <= UidaiAuthXml.MaxTransactionIdLength && id.All(char.IsAsciiLetterOrDigit));
    }

    private static Dictionary<string, string> Attributes(XElement element) =>
        element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToDictionary(a => a.Name.LocalName, a => a.Value);
}

/// <summary>Throwaway certificates for the AUA and for UIDAI, and UIDAI's way of signing an answer.</summary>
internal static class Certificates
{
    public static X509Certificate2 Create(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Exportable, for Pfx: Windows refuses to export a private key loaded without it ("Key not valid for use in
        // specified state"); Linux does not check.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "pw"), "pw",
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }

    /// <summary>The certificate with its private key as the Aadhaar settings take it: a .pfx file, base64.</summary>
    public static string Pfx(X509Certificate2 certificate, string password) => Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password));

    /// <summary>An enveloped signature over the whole document, as UIDAI signs its answers.</summary>
    public static string SignWhole(string xml, X509Certificate2 certificate)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        using var key = certificate.GetRSAPrivateKey()!;
        var signed = new SignedXml(document) { SigningKey = key };
        signed.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference(string.Empty) { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        signed.AddReference(reference);
        signed.ComputeSignature();
        document.DocumentElement!.AppendChild(document.ImportNode(signed.GetXml(), deep: true));
        return document.OuterXml;
    }
}
