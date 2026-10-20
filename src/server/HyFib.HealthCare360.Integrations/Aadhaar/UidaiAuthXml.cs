using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using HyFib.HealthCare360.WindowsInterop.Biometrics;

namespace HyFib.HealthCare360.Integrations.Aadhaar;

/// <summary>UIDAI's answer to an authentication (<c>AuthRes</c>).</summary>
/// <param name="Authenticated"><c>ret="y"</c>.</param>
/// <param name="Code">UIDAI's code for the authentication, up to 40 characters; <c>NA</c> when the request was not processed.</param>
/// <param name="ErrorCode"><c>err</c>, when UIDAI refused.</param>
internal sealed record AuthResponse(bool Authenticated, string? Code, string? TransactionId, string? ErrorCode, string? Timestamp);

/// <summary>
/// Aadhaar Authentication API 2.5: the Auth XML HC360 signs and sends, and UIDAI's answer. A registered device has
/// already encrypted the capture for UIDAI (<c>Skey</c>, <c>Data</c>, <c>Hmac</c>, see <see cref="PidCapture"/>), so the
/// request only carries them, with the number, the device's details (<c>Meta</c>) and the AUA's licence, under the
/// AUA's signature. What could not be checked against UIDAI's own text is a setting or listed in docs/modules/integrations.md
/// for the ASA to confirm at onboarding.
/// </summary>
internal static class UidaiAuthXml
{
    public const string Version = "2.5";

    /// <summary>The namespace of UIDAI's Auth request schema.</summary>
    public const string Namespace = "http://www.uidai.gov.in/authentication/uid-auth-request/2.0";

    /// <summary>UIDAI's limit for the AUA's transaction id (<c>txn</c>).</summary>
    public const int MaxTransactionIdLength = 50;

    /// <summary>A transaction id unique to this request: letters and digits only, 37 characters.</summary>
    public static string NewTransactionId() => "HC360" + Guid.CreateVersion7().ToString("N").ToUpperInvariant();

    /// <summary>
    /// The address the request goes to: UIDAI's <c>https://&lt;host&gt;/&lt;ver&gt;/&lt;ac&gt;/&lt;uid[0]&gt;/&lt;uid[1]&gt;/&lt;asalk&gt;</c>,
    /// as the ASA's template says. A Virtual ID sends <c>0</c> and <c>0</c> for the number's first two digits.
    /// </summary>
    public static Uri Address(string template, string auaCode, string uid, string asaLicenceKey)
    {
        var (first, second) = AadhaarNumber.IsVirtualId(uid) ? ("0", "0") : (uid[..1], uid[1..2]);
        var address = template
            .Replace("{ac}", Uri.EscapeDataString(auaCode), StringComparison.Ordinal)
            .Replace("{uid0}", first, StringComparison.Ordinal)
            .Replace("{uid1}", second, StringComparison.Ordinal)
            .Replace("{asalk}", Uri.EscapeDataString(asaLicenceKey), StringComparison.Ordinal);
        return new Uri(address, UriKind.Absolute);
    }

    /// <summary>The Auth element, unsigned.</summary>
    public static XElement Build(AadhaarAuthRequest request, AadhaarSettings settings, string licenceKey, string transactionId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        var capture = request.Capture;
        var device = capture.Device ?? throw new ArgumentException("The capture names no device.", nameof(request));
        XNamespace ns = Namespace;
        return new XElement(
            ns + "Auth",
            new XAttribute("uid", request.Uid),
            new XAttribute("rc", "Y"),
            new XAttribute("tid", settings.RegisteredDeviceTid),
            new XAttribute("ac", settings.AuaCode ?? string.Empty),
            new XAttribute("sa", settings.SubAuaCode ?? string.Empty),
            new XAttribute("ver", Version),
            new XAttribute("txn", transactionId),
            new XAttribute("lk", licenceKey),
            new XElement(
                ns + "Uses",
                new XAttribute("pi", "n"),
                new XAttribute("pa", "n"),
                new XAttribute("pfa", "n"),
                new XAttribute("bio", "y"),
                new XAttribute("bt", RdService.BiometricType(capture.DataType ?? settings.FingerDataType)),
                new XAttribute("pin", "n"),
                new XAttribute("otp", "n")),
            new XElement(
                ns + "Meta",
                new XAttribute("udc", request.TerminalCode ?? string.Empty),
                new XAttribute("rdsId", device.ServiceId),
                new XAttribute("rdsVer", device.ServiceVersion),
                new XAttribute("dpId", device.ProviderId),
                new XAttribute("dc", device.DeviceCode),
                new XAttribute("mi", device.ModelId),
                new XAttribute("mc", device.Certificate)),
            new XElement(ns + "Skey", new XAttribute("ci", capture.CertificateIdentifier ?? string.Empty), capture.SessionKey),
            new XElement(ns + "Data", new XAttribute("type", capture.DataFormat ?? "X"), capture.Data),
            new XElement(ns + "Hmac", capture.Hmac));
    }

    /// <summary>
    /// Signs the Auth element with the AUA's certificate: an enveloped XML signature over the whole document (RSA with
    /// SHA-256, inclusive canonicalisation), carrying the certificate, as the last child.
    /// </summary>
    public static string Sign(XElement auth, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(certificate);
        using var key = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("The AUA certificate has no RSA private key.");
        var document = Load(auth.ToString(SaveOptions.DisableFormatting));
        var signed = new SignedXml(document) { SigningKey = key };
        signed.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        signed.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        var reference = new Reference(string.Empty) { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        signed.AddReference(reference);
        var data = new KeyInfoX509Data(certificate);
        data.AddSubjectName(certificate.SubjectName.Name);
        var info = new KeyInfo();
        info.AddClause(data);
        signed.KeyInfo = info;
        signed.ComputeSignature();
        document.DocumentElement!.AppendChild(document.ImportNode(signed.GetXml(), deep: true));
        return document.OuterXml;
    }

    /// <summary>Reads UIDAI's answer.</summary>
    /// <exception cref="FormatException">Not an <c>AuthRes</c> document.</exception>
    public static AuthResponse ParseResponse(string? xml)
    {
        var root = Load(xml).DocumentElement;
        if (root?.LocalName != "AuthRes")
        {
            throw new FormatException("The answer is not UIDAI's authentication response.");
        }

        string? Attribute(string name) => root.HasAttribute(name) ? root.GetAttribute(name).Trim() : null;
        var ret = Attribute("ret");
        if (ret is null || !(ret.Equals("y", StringComparison.OrdinalIgnoreCase) || ret.Equals("n", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FormatException("UIDAI's answer says neither yes nor no.");
        }

        return new AuthResponse(
            ret.Equals("y", StringComparison.OrdinalIgnoreCase),
            Attribute("code"),
            Attribute("txn"),
            string.IsNullOrEmpty(Attribute("err")) ? null : Attribute("err"),
            Attribute("ts"));
    }

    /// <summary>
    /// Whether the answer carries UIDAI's signature over the whole answer, made with <paramref name="uidai"/>'s key:
    /// exactly one signature, a child of the answer, with one reference to the whole document. A signature over part of
    /// the answer, or with another key, does not count.
    /// </summary>
    public static bool HasUidaiSignature(string xml, X509Certificate2 uidai)
    {
        ArgumentNullException.ThrowIfNull(uidai);
        XmlDocument document;
        try
        {
            document = Load(xml);
        }
        catch (FormatException)
        {
            return false;
        }

        var signatures = document.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        if (signatures.Count != 1 || signatures[0] is not XmlElement element || element.ParentNode != document.DocumentElement)
        {
            return false;
        }

        var signed = new SignedXml(document);
        try
        {
            signed.LoadXml(element);
            if (signed.SignedInfo?.References.Count != 1 || signed.SignedInfo.References[0] is not Reference { Uri: "" })
            {
                return false;
            }

            return signed.CheckSignature(uidai, verifySignatureOnly: true);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Parses XML without DTDs or external entities.</summary>
    private static XmlDocument Load(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 1024 * 1024)
        {
            throw new FormatException("The XML is empty or too large.");
        }

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿')), settings);
            document.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The answer is not XML.", ex);
        }

        return document;
    }

    /// <summary>
    /// Staff-facing words for UIDAI's refusal, and whether it is about the person (the fingerprint or the number) rather
    /// than the request. Only codes whose meaning could be checked against UIDAI's error list are told apart; any other
    /// is shown as it is.
    /// </summary>
    public static (AadhaarCheckOutcome Outcome, string Message) Refusal(string? errorCode) => errorCode switch
    {
        "300" => (AadhaarCheckOutcome.NotVerified, "The fingerprint did not match this Aadhaar number (UIDAI code 300)."),
        "330" => (AadhaarCheckOutcome.NotVerified,
            "The Aadhaar holder has locked their biometrics (UIDAI code 330). They can unlock them in the mAadhaar app or on UIDAI's website, or be verified another way."),
        "996" or "997" or "998" => (AadhaarCheckOutcome.NotVerified,
            string.Create(CultureInfo.InvariantCulture, $"UIDAI cannot authenticate this Aadhaar number or Virtual ID (UIDAI code {errorCode}). Check the number with the person.")),
        "501" or "503" or "521" or "527" => (AadhaarCheckOutcome.Failed,
            string.Create(CultureInfo.InvariantCulture, $"UIDAI could not read the capture (UIDAI code {errorCode}). Capture the finger again; if it happens again, the reader or its RD service needs attention.")),
        "510" or "512" or "520" or "540" or "554" or "588" or "590" => (AadhaarCheckOutcome.Failed,
            string.Create(CultureInfo.InvariantCulture, $"UIDAI refused HC360's request (UIDAI code {errorCode}): the Aadhaar authentication settings or licence need attention. Tell your administrator.")),
        _ => (AadhaarCheckOutcome.Failed,
            string.Create(CultureInfo.InvariantCulture, $"UIDAI did not authenticate the fingerprint (UIDAI code {errorCode ?? "none"}). Capture the finger again; if it happens again, tell your administrator.")),
    };
}
