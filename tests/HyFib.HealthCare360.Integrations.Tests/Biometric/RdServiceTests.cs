using System.Net;
using System.Text;
using HyFib.HealthCare360.WindowsInterop.Biometrics;

namespace HyFib.HealthCare360.Integrations.Tests.Biometric;

/// <summary>
/// The UIDAI RD service protocol (SRS INT-BIO) against answers shaped as the RD specification 2.0 and the vendors' own
/// services give them: Mantra's paths (<c>/rd/info</c>, <c>/rd/capture</c>) and Morpho's (<c>/getDeviceInfo</c>,
/// <c>/capture</c>). The encrypted parts of a capture are placeholders: HC360 never reads them, it passes them on.
/// </summary>
public sealed class RdServiceTests
{
    private const string MantraDiscovery =
        """
        <RDService status="READY" info="Mantra Authentication Vendor Device Manager">
          <Interface id="DEVICEINFO" path="/rd/info" />
          <Interface id="CAPTURE" path="/rd/capture" />
        </RDService>
        """;

    private const string MorphoDiscovery =
        """<?xml version="1.0"?><RDService status="READY" info="Morpho_RD_Service"><Interface id="CAPTURE" path="/capture"/><Interface id="DEVICEINFO" path="/getDeviceInfo"/></RDService>""";

    private const string Device =
        """
        <DeviceInfo dpId="MANTRA.MSIPL" rdsId="MANTRA.WIN.001" rdsVer="1.0.8" mi="MFS110" mc="MIIEGDCCAwCgAwIBAgIE" dc="b6a5c1f2-30c4-4f8e-9a0e-1f4a7c9e2d11">
          <additional_info>
            <Param name="srno" value="5134729" />
            <Param name="sysid" value="ABCDEF0123" />
          </additional_info>
        </DeviceInfo>
        """;

    private const string Captured =
        """
        <?xml version="1.0"?>
        <PidData>
          <Resp errCode="0" errInfo="Capture Success" fCount="1" fType="2" nmPoints="38" qScore="74" />
          <DeviceInfo dpId="MANTRA.MSIPL" rdsId="MANTRA.WIN.001" rdsVer="1.0.8" mi="MFS110" mc="MIIEGDCCAwCgAwIBAgIE" dc="b6a5c1f2-30c4-4f8e-9a0e-1f4a7c9e2d11">
            <additional_info><Param name="srno" value="5134729" /></additional_info>
          </DeviceInfo>
          <Skey ci="20291231">U0tFWS1lbmNyeXB0ZWQtZm9yLVVJREFJ</Skey>
          <Hmac>SE1BQy1lbmNyeXB0ZWQ=</Hmac>
          <Data type="X">UElELWVuY3J5cHRlZC1mb3ItVUlEQUk=</Data>
        </PidData>
        """;

    [Fact]
    public void Asks_for_one_finger_as_minutiae_and_image_in_xml_for_production_by_default()
    {
        new FingerprintCaptureOptions().ToXml().ShouldBe(
            """<?xml version="1.0"?><PidOptions ver="1.0"><Opts fCount="1" fType="2" iCount="0" pCount="0" format="0" pidVer="2.0" timeout="15000" posh="UNKNOWN" env="P" /></PidOptions>""");
    }

    [Theory]
    [InlineData(RdEnvironment.Production, "P")]
    [InlineData(RdEnvironment.PreProduction, "PP")]
    [InlineData(RdEnvironment.Staging, "S")]
    public void Names_the_uidai_environment_the_capture_is_encrypted_for(RdEnvironment environment, string code)
    {
        new FingerprintCaptureOptions { Environment = environment }.ToXml().ShouldContain($"env=\"{code}\"");
        RdService.ParseEnvironment(code).ShouldBe(environment);
        RdService.ParseEnvironment($" {code.ToLowerInvariant()} ").ShouldBe(environment);
    }

    [Theory]
    [InlineData("PRODUCTION")]
    [InlineData("X")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_an_environment_uidai_does_not_have(string? code)
    {
        Should.Throw<FormatException>(() => RdService.ParseEnvironment(code));
    }

    [Theory]
    [InlineData(FingerDataType.Minutiae, "0", "FMR")]
    [InlineData(FingerDataType.Image, "1", "FIR")]
    [InlineData(FingerDataType.MinutiaeAndImage, "2", "FMR,FIR")]
    public void Tells_uidai_the_biometric_type_the_device_captured(FingerDataType type, string fType, string bt)
    {
        new FingerprintCaptureOptions { DataType = type }.ToXml().ShouldContain($"fType=\"{fType}\"");
        RdService.BiometricType(type).ShouldBe(bt);
    }

    [Fact]
    public void Refuses_options_no_reader_should_be_asked_for()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new FingerprintCaptureOptions { FingerCount = 0 }.ToXml());
        Should.Throw<ArgumentOutOfRangeException>(() => new FingerprintCaptureOptions { FingerCount = 3 }.ToXml());
        Should.Throw<ArgumentOutOfRangeException>(() => new FingerprintCaptureOptions { Timeout = TimeSpan.FromSeconds(2) }.ToXml());
        Should.Throw<ArgumentOutOfRangeException>(() => new FingerprintCaptureOptions { Timeout = TimeSpan.FromMinutes(5) }.ToXml());
        Should.Throw<ArgumentOutOfRangeException>(() => new FingerprintCaptureOptions { DataType = (FingerDataType)7 }.ToXml());
        Should.Throw<ArgumentOutOfRangeException>(() => new FingerprintCaptureOptions { Environment = (RdEnvironment)7 }.Validate());
        new FingerprintCaptureOptions { FingerCount = 2, Timeout = FingerprintCaptureOptions.MaxTimeout }.Validate();
    }

    [Fact]
    public void Reads_where_a_mantra_service_answers()
    {
        var endpoint = RdService.ParseEndpoint(11100, MantraDiscovery);

        endpoint.ShouldBe(new RdServiceEndpoint(11100, RdServiceStatus.Ready, "Mantra Authentication Vendor Device Manager", "/rd/info", "/rd/capture"));
        endpoint.CanCapture.ShouldBeTrue();
    }

    [Fact]
    public void Reads_where_a_morpho_service_answers()
    {
        var endpoint = RdService.ParseEndpoint(11101, MorphoDiscovery);

        endpoint.InfoPath.ShouldBe("/getDeviceInfo");
        endpoint.CapturePath.ShouldBe("/capture");
        endpoint.Info.ShouldBe("Morpho_RD_Service");
    }

    [Theory]
    [InlineData("NOTREADY", RdServiceStatus.NotReady)]
    [InlineData("USED", RdServiceStatus.Used)]
    [InlineData("ready", RdServiceStatus.Ready)]
    [InlineData("BUSY-ISH", RdServiceStatus.Unknown)]
    public void Reads_whether_the_service_can_capture_now(string status, RdServiceStatus expected)
    {
        var endpoint = RdService.ParseEndpoint(11100, MantraDiscovery.Replace("status=\"READY\"", $"status=\"{status}\"", StringComparison.Ordinal));

        endpoint.Status.ShouldBe(expected);
        endpoint.CanCapture.ShouldBe(expected == RdServiceStatus.Ready);
    }

    [Theory]
    [InlineData("http://evil.example/capture")]
    [InlineData("//evil.example/capture")]
    [InlineData("/rd/../../capture")]
    [InlineData("/rd/capture?redirect=http://evil.example")]
    [InlineData("rd/capture")]
    [InlineData("")]
    public void Never_takes_an_interface_path_that_could_lead_elsewhere(string path)
    {
        var endpoint = RdService.ParseEndpoint(11100, MantraDiscovery.Replace("/rd/capture", path, StringComparison.Ordinal));

        endpoint.CapturePath.ShouldBeNull();
        endpoint.CanCapture.ShouldBeFalse();
    }

    [Fact]
    public void Reads_the_device_and_its_serial_number()
    {
        RdService.ParseDevice(Device).ShouldBe(new RdDevice(
            "MANTRA.MSIPL", "MANTRA.WIN.001", "1.0.8", "MFS110", "b6a5c1f2-30c4-4f8e-9a0e-1f4a7c9e2d11", "MIIEGDCCAwCgAwIBAgIE", "5134729"));
    }

    [Fact]
    public void Reads_a_capture_and_keeps_what_uidai_needs_as_the_device_sent_it()
    {
        var capture = PidCapture.Parse(Captured);

        capture.Succeeded.ShouldBeTrue();
        capture.ErrorCode.ShouldBe(0);
        capture.FingerCount.ShouldBe(1);
        capture.DataType.ShouldBe(FingerDataType.MinutiaeAndImage);
        capture.Quality.ShouldBe(74);
        capture.Device!.ProviderId.ShouldBe("MANTRA.MSIPL");
        capture.CertificateIdentifier.ShouldBe("20291231");
        capture.SessionKey.ShouldBe("U0tFWS1lbmNyeXB0ZWQtZm9yLVVJREFJ");
        capture.Hmac.ShouldBe("SE1BQy1lbmNyeXB0ZWQ=");
        capture.Data.ShouldBe("UElELWVuY3J5cHRlZC1mb3ItVUlEQUk=");
        capture.DataFormat.ShouldBe("X");
        capture.Xml.ShouldBe(Captured);
    }

    [Fact]
    public void Takes_the_lowest_quality_of_two_fingers()
    {
        PidCapture.Parse(Captured.Replace("qScore=\"74\"", "qScore=\"74,61\"", StringComparison.Ordinal)).Quality.ShouldBe(61);
    }

    [Fact]
    public void Wraps_base64_lines_without_changing_them()
    {
        var wrapped = Captured.Replace("UElELWVuY3J5cHRlZC1m", "UElELWVuY3J5cHRlZC1m\r\n    ", StringComparison.Ordinal);

        PidCapture.Parse(wrapped).Data.ShouldBe("UElELWVuY3J5cHRlZC1mb3ItVUlEQUk=");
    }

    [Fact]
    public void The_staff_apps_training_capture_is_the_one_the_server_knows()
    {
        // trainingCapture in packages/core/src/bridge/fingerprint.ts (its test checks the same document).
        const string staffApp =
            """<PidData><Resp errCode="0" errInfo="Training capture" fCount="1" fType="2" nmPoints="0" qScore="80" />"""
            + """<DeviceInfo dpId="HC360.TRAINING" rdsId="HC360.TRAINING.RD" rdsVer="1.0" mi="TRAINING" mc="TRAINING" dc="TRAINING" />"""
            + """<Skey ci="20991231">TRAINING-MATCH</Skey><Hmac>TRAINING</Hmac><Data type="X">TRAINING</Data></PidData>""";

        RdService.TrainingCapture(matches: true).ShouldBe(staffApp);
        RdService.TrainingCapture(matches: false).ShouldBe(staffApp.Replace("TRAINING-MATCH", "TRAINING-NO-MATCH", StringComparison.Ordinal));
        var capture = PidCapture.Parse(staffApp);
        (capture.Succeeded, capture.IsTraining, capture.IsMatchingTrainingCapture).ShouldBe((true, true, true));
        PidCapture.Parse(RdService.TrainingCapture(matches: false)).IsMatchingTrainingCapture.ShouldBeFalse();
    }

    [Fact]
    public void A_capture_without_its_encrypted_parts_has_not_succeeded()
    {
        var capture = PidCapture.Parse(Captured.Replace("""<Hmac>SE1BQy1lbmNyeXB0ZWQ=</Hmac>""", string.Empty, StringComparison.Ordinal));

        capture.ErrorCode.ShouldBe(0);
        capture.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void A_failed_capture_is_explained_with_the_devices_own_words()
    {
        var capture = PidCapture.Parse("""<PidData><Resp errCode="700" errInfo="Capture timed out" fCount="0" /></PidData>""");

        capture.Succeeded.ShouldBeFalse();
        RdService.Problem(capture.ErrorCode, capture.ErrorInfo).ShouldBe(
            "The fingerprint reader waited for a finger and timed out. Place the finger on the reader and try again. The reader says: Capture timed out (code 700).");
    }

    [Theory]
    [InlineData("""<!DOCTYPE PidData [<!ENTITY x "boom">]><PidData><Resp errCode="0" errInfo="&x;" /></PidData>""")]
    [InlineData("not xml at all")]
    [InlineData("""<DeviceInfo dpId="x" />""")]
    [InlineData("""<PidData><Resp errInfo="no code" /></PidData>""")]
    [InlineData("")]
    public void Refuses_answers_that_are_not_a_capture(string xml)
    {
        Should.Throw<FormatException>(() => PidCapture.Parse(xml));
    }

    [Fact]
    public void Allows_a_byte_order_mark_and_blank_lines_before_the_document()
    {
        PidCapture.Parse("﻿\r\n" + Captured).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Refuses_an_answer_over_the_size_limit()
    {
        var huge = "<PidData><Resp errCode=\"0\" /><Data type=\"X\">" + new string('A', RdService.MaxDocumentChars) + "</Data></PidData>";

        Should.Throw<FormatException>(() => PidCapture.Parse(huge));
    }

    [Fact]
    public async Task Finds_the_service_among_the_ports_uidai_set_aside_and_calls_nothing_else()
    {
        var device = new FakeRdServices().Serve(11103, MantraDiscovery);

        var found = await new RdServiceClient(device.Client()).DiscoverAsync(TestContext.Current.CancellationToken);

        found.ShouldHaveSingleItem().Port.ShouldBe(11103);
        device.Requests.Count.ShouldBe(21);
        device.Requests.ShouldAllBe(r => r.Method.Method == "RDSERVICE" && r.RequestUri!.Host == "127.0.0.1" && r.RequestUri.AbsolutePath == "/");
        device.Requests.Select(r => r.RequestUri!.Port).Order().ShouldBe(Enumerable.Range(11100, 21));
    }

    [Fact]
    public async Task Captures_on_the_path_the_service_named_with_the_options_as_xml()
    {
        var device = new FakeRdServices().Serve(11100, MantraDiscovery, capture: Captured);
        var client = new RdServiceClient(device.Client());

        var capture = await client.CaptureFingerprintAsync(new FingerprintCaptureOptions { Environment = RdEnvironment.PreProduction }, TestContext.Current.CancellationToken);

        capture.Succeeded.ShouldBeTrue();
        var asked = device.Requests.Where(r => r.Method.Method == "CAPTURE").ShouldHaveSingleItem();
        asked.RequestUri.ShouldBe(new Uri("http://127.0.0.1:11100/rd/capture"));
        device.Bodies[asked].ShouldContain("env=\"PP\"");
        device.ContentTypes[asked].ShouldBe("text/xml");
    }

    [Fact]
    public async Task Asks_the_device_to_describe_itself()
    {
        var device = new FakeRdServices().Serve(11102, MorphoDiscovery, info: Device);
        var client = new RdServiceClient(device.Client());
        var endpoint = (await client.DiscoverAsync(TestContext.Current.CancellationToken)).Single();

        var described = await client.DeviceAsync(endpoint, TestContext.Current.CancellationToken);

        described.SerialNumber.ShouldBe("5134729");
        device.Requests.ShouldContain(r => r.Method.Method == "DEVICEINFO" && r.RequestUri == new Uri("http://127.0.0.1:11102/getDeviceInfo"));
    }

    [Fact]
    public async Task Says_when_no_reader_is_installed()
    {
        var error = await Should.ThrowAsync<RdServiceException>(() =>
            new RdServiceClient(new FakeRdServices().Client()).CaptureFingerprintAsync(new FingerprintCaptureOptions(), TestContext.Current.CancellationToken));

        error.Problem.ShouldBe(RdProblem.NoReader);
        error.Message.ShouldContain("No fingerprint reader was found");
    }

    [Fact]
    public async Task Says_when_another_application_has_the_reader()
    {
        var device = new FakeRdServices().Serve(11100, MantraDiscovery.Replace("READY", "USED", StringComparison.Ordinal));

        var error = await Should.ThrowAsync<RdServiceException>(() =>
            new RdServiceClient(device.Client()).CaptureFingerprintAsync(new FingerprintCaptureOptions(), TestContext.Current.CancellationToken));

        error.Problem.ShouldBe(RdProblem.Busy);
    }

    [Fact]
    public async Task Says_when_the_reader_is_unplugged()
    {
        var device = new FakeRdServices().Serve(11100, MantraDiscovery.Replace("status=\"READY\"", "status=\"NOTREADY\"", StringComparison.Ordinal));

        var error = await Should.ThrowAsync<RdServiceException>(() =>
            new RdServiceClient(device.Client()).CaptureFingerprintAsync(new FingerprintCaptureOptions(), TestContext.Current.CancellationToken));

        error.Problem.ShouldBe(RdProblem.NotReady);
        error.Message.ShouldBe("The fingerprint reader is not ready (Mantra Authentication Vendor Device Manager). Plug it in again, or restart its RD service.");
    }

    [Theory]
    [InlineData(700, RdProblem.TimedOut)]
    [InlineData(710, RdProblem.Busy)]
    [InlineData(720, RdProblem.NotReady)]
    [InlineData(730, RdProblem.CaptureFailed)]
    [InlineData(999, RdProblem.CaptureFailed)]
    public async Task Turns_a_failed_capture_into_its_problem(int code, RdProblem problem)
    {
        var device = new FakeRdServices().Serve(11100, MantraDiscovery,
            capture: $"""<PidData><Resp errCode="{code}" errInfo="Device says {code}" /></PidData>""");

        var error = await Should.ThrowAsync<RdServiceException>(() =>
            new RdServiceClient(device.Client()).CaptureFingerprintAsync(new FingerprintCaptureOptions(), TestContext.Current.CancellationToken));

        error.Problem.ShouldBe(problem);
        error.Message.ShouldContain($"Device says {code} (code {code})");
    }

    [Fact]
    public async Task A_success_without_a_fingerprint_is_not_passed_on()
    {
        var device = new FakeRdServices().Serve(11100, MantraDiscovery, capture: """<PidData><Resp errCode="0" errInfo="Success" /></PidData>""");

        var error = await Should.ThrowAsync<RdServiceException>(() =>
            new RdServiceClient(device.Client()).CaptureFingerprintAsync(new FingerprintCaptureOptions(), TestContext.Current.CancellationToken));

        error.Problem.ShouldBe(RdProblem.BadAnswer);
    }

    [Fact]
    public async Task Refuses_a_capture_answer_over_the_size_limit_without_reading_it_all()
    {
        var device = new FakeRdServices().Serve(11100, MantraDiscovery, capture: "<PidData>" + new string('A', RdService.MaxDocumentChars + 1) + "</PidData>");

        var error = await Should.ThrowAsync<RdServiceException>(() =>
            new RdServiceClient(device.Client()).CaptureFingerprintAsync(new FingerprintCaptureOptions(), TestContext.Current.CancellationToken));

        error.Problem.ShouldBe(RdProblem.BadAnswer);
        error.Message.ShouldContain("too large");
    }

    [Fact]
    public async Task A_service_that_stops_answering_mid_capture_times_out()
    {
        var device = new FakeRdServices().Serve(11100, MantraDiscovery, captureFails: new TaskCanceledException("The request was canceled due to the configured timeout."));

        var error = await Should.ThrowAsync<RdServiceException>(() =>
            new RdServiceClient(device.Client()).CaptureFingerprintAsync(new FingerprintCaptureOptions(), TestContext.Current.CancellationToken));

        error.Problem.ShouldBe(RdProblem.TimedOut);
    }

    /// <summary>RD services on 127.0.0.1: the ports given answer; every other port refuses the connection.</summary>
    private sealed class FakeRdServices : HttpMessageHandler
    {
        private readonly Dictionary<int, (string Discovery, string? Info, string? Capture, Exception? CaptureFails)> _services = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public Dictionary<HttpRequestMessage, string> Bodies { get; } = [];

        public Dictionary<HttpRequestMessage, string?> ContentTypes { get; } = [];

        public FakeRdServices Serve(int port, string discovery, string? info = null, string? capture = null, Exception? captureFails = null)
        {
            _services[port] = (discovery, info, capture, captureFails);
            return this;
        }

        public HttpClient Client() => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                lock (Requests)
                {
                    Bodies[request] = body;
                    ContentTypes[request] = request.Content.Headers.ContentType?.MediaType;
                }
            }

            if (!_services.TryGetValue(request.RequestUri!.Port, out var service))
            {
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            }

            var answer = request.Method.Method switch
            {
                "RDSERVICE" => service.Discovery,
                "DEVICEINFO" => service.Info,
                "CAPTURE" when service.CaptureFails is { } failure => throw failure,
                "CAPTURE" => service.Capture,
                _ => null,
            };
            return answer is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "text/xml") };
        }
    }
}
