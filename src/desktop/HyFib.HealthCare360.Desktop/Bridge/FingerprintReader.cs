using System.Net.Http;
using System.Text.Json;
using HyFib.HealthCare360.WindowsInterop.Biometrics;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>What <c>fingerprint.status</c> answers: whether a reader can capture now, or why not.</summary>
internal sealed record FingerprintReaderStatus(bool Ready, string? Code, string? Problem, FingerprintDevice? Device);

/// <summary>The certified device an RD service serves, as staff and IT need to see it.</summary>
internal sealed record FingerprintDevice(string ProviderId, string ModelId, string? SerialNumber, string ServiceId, string ServiceVersion)
{
    public static FingerprintDevice Of(RdDevice device) =>
        new(device.ProviderId, device.ModelId, device.SerialNumber, device.ServiceId, device.ServiceVersion);
}

/// <summary>What <c>fingerprint.capture</c> answers: the capture as the device encrypted it, for the server to send to UIDAI.</summary>
internal sealed record FingerprintCaptured(string PidData, int? Quality, FingerprintDevice? Device);

/// <summary>
/// The fingerprint reader on this workstation (SRS INT-BIO, bridge methods <c>fingerprint.status</c> and
/// <c>fingerprint.capture</c>): a UIDAI-certified device whose RD service runs on this computer
/// (<see cref="RdServiceClient"/>). HC360 Desktop asks it for a capture with the options the server set up
/// (<c>integrations.aadhaar</c>) and gives the staff app the capture as the device encrypted it, for UIDAI alone; the
/// server sends it to UIDAI through the hospital's AUA. Nothing of the capture is logged or kept on this computer.
/// </summary>
internal static class FingerprintReader
{
    // One call at a time: an RD service serves one caller, and a page cannot stack prompts on the reader.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly RdServiceClient Client = new(new HttpMessageInvoker(RdServiceClient.CreateHandler()));

    public static async Task<FingerprintReaderStatus> StatusAsync()
    {
        if (!await Gate.WaitAsync(0))
        {
            throw new BridgeException("busy", "HC360 Desktop is capturing a fingerprint.");
        }

        try
        {
            var services = await Client.DiscoverAsync(CancellationToken.None);
            var service = services.FirstOrDefault(s => s.CanCapture);
            if (service is null)
            {
                var problem = RdServiceClient.NotReady(services);
                return new FingerprintReaderStatus(false, Code(problem.Problem), problem.Message, null);
            }

            var device = await Client.DeviceAsync(service, CancellationToken.None);
            return new FingerprintReaderStatus(true, null, null, FingerprintDevice.Of(device));
        }
        catch (RdServiceException ex)
        {
            return new FingerprintReaderStatus(false, Code(ex.Problem), ex.Message, null);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<FingerprintCaptured> CaptureAsync(JsonElement? parameters)
    {
        var options = Options(parameters);
        if (!await Gate.WaitAsync(0))
        {
            throw new BridgeException("busy", "HC360 Desktop is already capturing a fingerprint.");
        }

        try
        {
            var capture = await Client.CaptureFingerprintAsync(options, CancellationToken.None);
            Log.Information("Captured a fingerprint with a {Provider} {Model} reader (quality {Quality})",
                capture.Device?.ProviderId, capture.Device?.ModelId, capture.Quality);
            return new FingerprintCaptured(capture.Xml, capture.Quality, capture.Device is { } device ? FingerprintDevice.Of(device) : null);
        }
        catch (RdServiceException ex)
        {
            Log.Warning("A fingerprint capture failed ({Problem}): {Message}", ex.Problem, ex.Message);
            throw new BridgeException(Code(ex.Problem), ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The bridge error code for a reader problem (<c>FingerprintErrorCode</c> in packages/core/src/bridge/fingerprint.ts).</summary>
    internal static string Code(RdProblem problem) => problem switch
    {
        RdProblem.NoReader => "no_reader",
        RdProblem.NotReady => "not_ready",
        RdProblem.Busy => "busy",
        RdProblem.TimedOut => "timeout",
        RdProblem.CaptureFailed => "capture_failed",
        _ => "reader_error",
    };

    /// <summary>
    /// The capture the staff app asks for: what the server set up (<c>AadhaarCaptureSetup</c>), each value checked here
    /// too, since a page could ask for anything.
    /// </summary>
    internal static FingerprintCaptureOptions Options(JsonElement? parameters)
    {
        try
        {
            var options = new FingerprintCaptureOptions
            {
                FingerCount = Int(parameters, "fingerCount") ?? 1,
                DataType = (FingerDataType)(Int(parameters, "dataType") ?? (int)FingerDataType.MinutiaeAndImage),
                Timeout = TimeSpan.FromSeconds(Int(parameters, "timeoutSeconds") ?? 15),
                Environment = RdService.ParseEnvironment(Text(parameters, "environment") ?? "P"),
            };
            options.Validate();
            return options;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new BridgeException("bad_request", "The fingerprint capture options are not valid.");
        }
    }

    private static int? Int(JsonElement? parameters, string name) =>
        parameters is { } p && p.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw new FormatException($"{name} is not a whole number.")
            : null;

    private static string? Text(JsonElement? parameters, string name) =>
        parameters is { } p && p.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"{name} is not text.")
            : null;
}
