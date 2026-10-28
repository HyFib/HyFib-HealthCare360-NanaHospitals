using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using HyFib.HealthCare360.Integrations.Aadhaar;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.WindowsInterop.Biometrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Aadhaar;

/// <summary>
/// Checking a fingerprint with UIDAI (INT-BIO) through the router other modules resolve as
/// <see cref="IAadhaarAuthenticator"/>: the training simulator, and Auth API 2.5 against a fake ASA. Nothing is sent
/// without the person's consent, a well-formed number and a complete capture; the number, the capture and the licence
/// never reach the integration log.
/// </summary>
public sealed class AadhaarAuthenticationTests : IDisposable
{
    private static readonly Guid DeviceId = Guid.Parse("01929a3b-7c4d-7e5f-8a6b-1c2d3e4f5a6b");

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Tracing _tracing = new();
    private readonly FakeAsa _asa = new();
    private readonly X509Certificate2 _aua = Certificates.Create("CN=Nana Hospitals AUA");
    private readonly X509Certificate2 _uidai = Certificates.Create("CN=UIDAI test signer");

    public AadhaarAuthenticationTests()
    {
        _user.DeviceId.Returns(DeviceId);
        Configure(UidaiAuthXmlTests.Settings);
        Secrets(new AadhaarSecrets
        {
            LicenceKey = "AUA-LICENCE-KEY-0001",
            AsaLicenceKey = "ASA-LICENCE-KEY-0001",
            SigningCertificate = Certificates.Pfx(_aua, "pfx-password"),
            SigningCertificatePassword = "pfx-password",
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AadhaarAuthRouter Router => new(
        [new UidaiAadhaarAuthenticator(new HttpClient(_asa), _tracing.Trace), new SimulatorAadhaarAuthenticator(_tracing.Trace)],
        new IntegrationSettingsReader(_settings, _user, Servers.Training, NullLogger<IntegrationSettingsReader>.Instance),
        _user);

    public void Dispose()
    {
        _aua.Dispose();
        _uidai.Dispose();
        _asa.Dispose();
    }

    [Fact]
    public async Task Says_why_until_it_is_set_up()
    {
        Configure(null);

        (await Router.SetupAsync(null, Ct)).ShouldBe(new AadhaarCaptureSetup(null, "Aadhaar authentication is not configured. Set it up in Administration → Integrations."));
        var result = await Router.AuthenticateAsync(Check(), Ct);

        result.Outcome.ShouldBe(AadhaarCheckOutcome.Failed);
        result.Message.ShouldContain("not configured");
        _asa.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tells_the_workstation_what_to_capture()
    {
        (await Router.SetupAsync(null, Ct)).ShouldBe(new AadhaarCaptureSetup("uidai", null, "P", 2, 1, 15, Training: false));

        Configure(UidaiAuthXmlTests.Settings with { Provider = "simulator", Environment = "PP", FingerData = "FMR", FingerCount = 2 });

        (await Router.SetupAsync(null, Ct)).ShouldBe(new AadhaarCaptureSetup("simulator", null, "PP", 0, 2, 15, Training: true));
    }

    [Fact]
    public async Task Refuses_capture_settings_no_reader_can_follow()
    {
        Configure(UidaiAuthXmlTests.Settings with { Environment = "PROD" });

        (await Router.SetupAsync(null, Ct)).Problem.ShouldBe("The Aadhaar authentication environment must be P, PP or S.");
    }

    [Fact]
    public void Asks_nothing_of_uidai_without_the_persons_consent()
    {
        // Without the person's consent there is no check to send: it cannot be made.
        Should.Throw<ArgumentException>(() => new AadhaarFingerprintCheck(UidaiAuthXmlTests.TestUid, UidaiAuthXmlTests.Capture, residentConsented: false))
            .ParamName.ShouldBe("residentConsented");
        _asa.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("9999 4105 7059")] // wrong check digit
    [InlineData("1234 5678 9012")] // cannot start with 1
    [InlineData("99994105705")] // 11 digits
    [InlineData("9999-4105-705A")]
    [InlineData("")]
    public async Task Asks_nothing_of_uidai_for_a_number_that_is_not_an_aadhaar_number_or_virtual_id(string number)
    {
        var result = await Router.AuthenticateAsync(Check(number), Ct);

        result.Message.ShouldBe("Enter the 12-digit Aadhaar number or the 16-digit Virtual ID.");
        _asa.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not a capture", "could not be read")]
    [InlineData("""<PidData><Resp errCode="700" errInfo="Capture timed out" /></PidData>""", "incomplete")]
    public async Task Asks_nothing_of_uidai_for_a_capture_that_is_not_complete(string capture, string words)
    {
        var result = await Router.AuthenticateAsync(Check() with { PidData = capture }, Ct);

        result.Message.ShouldContain(words);
        _asa.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Training_says_whether_the_training_finger_matches()
    {
        Configure(UidaiAuthXmlTests.Settings with { Provider = "simulator" });

        var matching = await Router.AuthenticateAsync(Check() with { PidData = RdService.TrainingCapture(matches: true) }, Ct);
        var other = await Router.AuthenticateAsync(Check() with { PidData = RdService.TrainingCapture(matches: false) }, Ct);
        var reader = await Router.AuthenticateAsync(Check(), Ct);

        matching.Outcome.ShouldBe(AadhaarCheckOutcome.Verified);
        other.Outcome.ShouldBe(AadhaarCheckOutcome.NotVerified);
        other.ErrorCode.ShouldBe("300");
        reader.Outcome.ShouldBe(AadhaarCheckOutcome.Verified);
        reader.Message.ShouldStartWith("Training:");
        _asa.Requests.ShouldBeEmpty();
        _tracing.Entries.Count.ShouldBe(3);
        _tracing.AllText.ShouldNotContain(UidaiAuthXmlTests.TestUid);
    }

    [Fact]
    public async Task Never_sends_a_training_capture_to_uidai()
    {
        var result = await Router.AuthenticateAsync(Check() with { PidData = RdService.TrainingCapture(matches: true) }, Ct);

        result.Outcome.ShouldBe(AadhaarCheckOutcome.Failed);
        result.Message.ShouldContain("training capture cannot be sent to UIDAI");
        _asa.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sends_only_from_an_enrolled_workstation()
    {
        _user.DeviceId.Returns((Guid?)null);

        var result = await Router.AuthenticateAsync(Check(), Ct);

        result.Message.ShouldBe("Aadhaar authentication works only from HC360 Desktop on an enrolled workstation.");
        _asa.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://asa.example.in/2.5/{ac}/{uid0}/{uid1}/{asalk}", "AUA", "NANA01", "https address")]
    [InlineData("https://asa.example.in/2.5/{ac}/{uid0}/{uid1}/{asalk}", "", "NANA01", "AUA code")]
    [InlineData("https://asa.example.in/2.5/{ac}/{uid0}/{uid1}/{asalk}", "AUA-CODE!", "NANA01", "AUA code")]
    public async Task Says_what_the_settings_lack(string url, string aua, string subAua, string words)
    {
        Configure(UidaiAuthXmlTests.Settings with { AuthUrl = url, AuaCode = aua, SubAuaCode = subAua });

        var result = await Router.AuthenticateAsync(Check(), Ct);

        result.Message.ShouldContain(words);
        _asa.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Says_when_the_licence_or_certificate_is_missing()
    {
        Secrets(new AadhaarSecrets { LicenceKey = "AUA-LICENCE-KEY-0001" });

        (await Router.AuthenticateAsync(Check(), Ct)).Message.ShouldContain("AUA signing certificate");
        _asa.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sends_the_signed_request_to_the_asa_and_believes_uidais_yes()
    {
        _asa.Answer(request => $"""<AuthRes ret="y" code="7c2c8a2b12f34b5d9e6f" txn="{request.Txn}" err="" ts="2026-10-20T12:30:00" />""");

        var result = await Router.AuthenticateAsync(Check(), Ct);

        result.Outcome.ShouldBe(AadhaarCheckOutcome.Verified);
        result.ResponseCode.ShouldBe("7c2c8a2b12f34b5d9e6f");
        result.DeviceProviderId.ShouldBe("MANTRA.MSIPL");
        var sent = _asa.Requests.ShouldHaveSingleItem();
        sent.Uri.ShouldBe(new Uri("https://asa.example.in/2.5/HC0000001/9/9/ASA-LICENCE-KEY-0001"));
        sent.ContentType.ShouldBe("application/xml");
        result.TransactionId.ShouldBe(sent.Txn);
        sent.Body.ShouldContain("""<Skey ci="20291231">U0tFWS1lbmNyeXB0ZWQtZm9yLVVJREFJ</Skey>""");
        sent.Body.ShouldContain("""<Data type="X">UElELWVuY3J5cHRlZC1mb3ItVUlEQUk=</Data>""");
        sent.Body.ShouldContain("udc=\"HC360" + DeviceId.ToString("N")[^15..].ToUpperInvariant() + "\"");
        SignatureOf(sent.Body).CheckSignature(_aua, verifySignatureOnly: true).ShouldBeTrue();

        var log = _tracing.Entries.ShouldHaveSingleItem();
        log.Success.ShouldBeTrue();
        log.Request!.ShouldContain("uid ****7058");
        _tracing.AllText.ShouldNotContain(UidaiAuthXmlTests.TestUid);
        _tracing.AllText.ShouldNotContain("UElELWVuY3J5cHRlZC1mb3ItVUlEQUk=");
        _tracing.AllText.ShouldNotContain("AUA-LICENCE-KEY-0001");
        _tracing.AllText.ShouldNotContain("ASA-LICENCE-KEY-0001");
    }

    [Fact]
    public async Task A_fingerprint_that_does_not_match_is_uidais_answer_not_a_failure()
    {
        _asa.Answer(request => $"""<AuthRes ret="n" code="5d1a" txn="{request.Txn}" err="300" />""");

        var result = await Router.AuthenticateAsync(Check(), Ct);

        result.Outcome.ShouldBe(AadhaarCheckOutcome.NotVerified);
        result.ErrorCode.ShouldBe("300");
        result.Message.ShouldContain("did not match");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Sends_a_virtual_id_as_uidai_asks()
    {
        _asa.Answer(request => $"""<AuthRes ret="y" code="C" txn="{request.Txn}" />""");

        await Router.AuthenticateAsync(Check("9134 5678 9012 3456"), Ct);

        _asa.Requests.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/2.5/HC0000001/0/0/ASA-LICENCE-KEY-0001");
    }

    [Fact]
    public async Task Does_not_believe_an_answer_to_another_request()
    {
        _asa.Answer(_ => """<AuthRes ret="y" code="C" txn="HC360SOMEOTHERREQUEST" />""");

        var result = await Router.AuthenticateAsync(Check(), Ct);

        result.Outcome.ShouldBe(AadhaarCheckOutcome.Failed);
        result.Message.ShouldContain("another request");
    }

    [Fact]
    public async Task With_uidais_certificate_believes_only_answers_it_signed()
    {
        Configure(UidaiAuthXmlTests.Settings with { UidaiCertificate = _uidai.ExportCertificatePem() });
        _asa.Answer(request => $"""<AuthRes ret="y" code="C" txn="{request.Txn}" />""");
        var unsigned = await Router.AuthenticateAsync(Check(), Ct);

        _asa.Answer(request => Certificates.SignWhole($"""<AuthRes ret="y" code="C" txn="{request.Txn}" />""", _uidai));
        var signed = await Router.AuthenticateAsync(Check(), Ct);

        unsigned.Outcome.ShouldBe(AadhaarCheckOutcome.Failed);
        unsigned.Message.ShouldContain("not signed with UIDAI's certificate");
        signed.Outcome.ShouldBe(AadhaarCheckOutcome.Verified);
    }

    [Fact]
    public async Task Says_nothing_is_known_when_the_asa_fails()
    {
        _asa.Fail(HttpStatusCode.ServiceUnavailable);

        var result = await Router.AuthenticateAsync(Check(), Ct);

        result.Outcome.ShouldBe(AadhaarCheckOutcome.Failed);
        result.Message.ShouldContain("HTTP 503");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }

    [Fact]
    public async Task Says_when_uidai_did_not_answer_in_time()
    {
        _asa.Throw(new TaskCanceledException("The request was canceled due to the configured timeout."));

        var result = await Router.AuthenticateAsync(Check(), Ct);

        result.Outcome.ShouldBe(AadhaarCheckOutcome.Failed);
        result.Message.ShouldContain("did not answer in time");
    }

    [Fact]
    public void Names_the_workstation_by_its_enrolment_in_twenty_letters_and_digits()
    {
        var code = AadhaarAuthRouter.TerminalCode(DeviceId)!;

        code.Length.ShouldBe(20);
        code.ShouldStartWith("HC360");
        code.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c));
        AadhaarAuthRouter.TerminalCode(null).ShouldBeNull();
    }

    private static AadhaarFingerprintCheck Check(string number = UidaiAuthXmlTests.TestUid) =>
        new(number, UidaiAuthXmlTests.Capture, residentConsented: true);

    private static SignedXml SignatureOf(string xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        var signed = new SignedXml(document);
        signed.LoadXml((XmlElement)document.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        return signed;
    }

    private void Configure(AadhaarSettings? settings) =>
        _settings.GetAsync<AadhaarSettings>("integrations.aadhaar", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);

    private void Secrets(AadhaarSecrets secrets) =>
        _settings.GetAsync<AadhaarSecrets>("integrations.aadhaar.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(secrets);

    /// <summary>The ASA: records each request and answers it from what it carried (its transaction id).</summary>
    private sealed class FakeAsa : HttpMessageHandler
    {
        private Func<SentRequest, HttpResponseMessage> _answer = _ => throw new InvalidOperationException("No answer set.");

        public List<SentRequest> Requests { get; } = [];

        public void Answer(Func<SentRequest, string> xml) =>
            _answer = request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml(request), Encoding.UTF8, "application/xml") };

        public void Fail(HttpStatusCode status) => _answer = _ => new HttpResponseMessage(status) { Content = new StringContent("<html>Unavailable</html>") };

        public void Throw(Exception exception) => _answer = _ => throw exception;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var document = new XmlDocument();
            document.LoadXml(body);
            var sent = new SentRequest(request.RequestUri!, body, request.Content.Headers.ContentType?.MediaType, document.DocumentElement!.GetAttribute("txn"));
            Requests.Add(sent);
            return _answer(sent);
        }
    }

    private sealed record SentRequest(Uri Uri, string Body, string? ContentType, string Txn);
}
