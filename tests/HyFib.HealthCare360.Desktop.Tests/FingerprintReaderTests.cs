using System.Text.Json;
using HyFib.HealthCare360.Desktop.Bridge;
using HyFib.HealthCare360.WindowsInterop.Biometrics;

namespace HyFib.HealthCare360.Desktop.Tests;

/// <summary>The bridge's side of the fingerprint reader (INT-BIO): the options a page may ask for, and the error codes it gets.</summary>
public sealed class FingerprintReaderTests
{
    [Fact]
    public void Without_options_asks_for_one_finger_as_minutiae_and_image_for_production()
    {
        FingerprintReader.Options(null).ShouldBe(new FingerprintCaptureOptions());
        FingerprintReader.Options(Parameters("{}")).ShouldBe(new FingerprintCaptureOptions());
    }

    [Fact]
    public void Takes_the_options_the_server_set_up()
    {
        var options = FingerprintReader.Options(Parameters("""{"fingerCount":2,"dataType":0,"timeoutSeconds":30,"environment":"PP"}"""));

        options.ShouldBe(new FingerprintCaptureOptions
        {
            FingerCount = 2,
            DataType = FingerDataType.Minutiae,
            Timeout = TimeSpan.FromSeconds(30),
            Environment = RdEnvironment.PreProduction,
        });
    }

    [Theory]
    [InlineData("""{"fingerCount":3}""")]
    [InlineData("""{"fingerCount":0}""")]
    [InlineData("""{"fingerCount":"1"}""")]
    [InlineData("""{"fingerCount":1.5}""")]
    [InlineData("""{"dataType":5}""")]
    [InlineData("""{"timeoutSeconds":1}""")]
    [InlineData("""{"timeoutSeconds":600}""")]
    [InlineData("""{"environment":"X"}""")]
    [InlineData("""{"environment":1}""")]
    public void Refuses_options_no_reader_should_be_asked_for(string json)
    {
        Should.Throw<BridgeException>(() => FingerprintReader.Options(Parameters(json))).Code.ShouldBe("bad_request");
    }

    [Theory]
    [InlineData(RdProblem.NoReader, "no_reader")]
    [InlineData(RdProblem.NotReady, "not_ready")]
    [InlineData(RdProblem.Busy, "busy")]
    [InlineData(RdProblem.TimedOut, "timeout")]
    [InlineData(RdProblem.CaptureFailed, "capture_failed")]
    [InlineData(RdProblem.BadAnswer, "reader_error")]
    public void Gives_the_staff_app_a_code_for_each_problem(RdProblem problem, string code)
    {
        FingerprintReader.Code(problem).ShouldBe(code);
    }

    [Fact]
    public void Every_problem_has_a_code_of_its_own()
    {
        Enum.GetValues<RdProblem>().Select(FingerprintReader.Code).ShouldBeUnique();
    }

    private static JsonElement Parameters(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
