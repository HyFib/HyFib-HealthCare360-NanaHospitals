using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Integrations.Laboratory;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>
/// The administrator's test of the worklist server: a DICOM C-ECHO from this machine to the server, calling its own AE
/// title (the server accepts that only from this machine, and only for echoes). It shows that the server is listening on
/// its port and answers to its AE title. The worklist is served only where the jobs run (SCAL-05); with several servers
/// that node answers no requests, so an API node, which has no worklist server and cannot send the echo, says what the
/// node that runs the jobs shares of its worklist server (<see cref="LabStatusBoard"/>), as the dashboard does.
/// </summary>
internal sealed class ModalityWorklistSelfTest(
    IntegrationSettingsReader settings,
    IDicomClientFactory clients,
    ModalityWorklistState state,
    LabStatusBoard shared,
    IOptions<PlatformOptions> platform,
    TimeProvider time,
    ILoggerFactory loggers)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<IntegrationTestResult> RunAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.ReadValueAsync<WorklistSettings>(WorklistSettings.Key, null, cancellationToken);
        if (stored is null)
        {
            return Fail("The modality worklist is not set up. Turn it on and list the modalities in its settings.");
        }

        if (!stored.Enabled)
        {
            return Fail("The modality worklist is turned off.");
        }

        var config = WorklistConfig.From(stored, []);
        if (config.Listener is not { } listener)
        {
            return Fail(config.Problems.FirstOrDefault() ?? "The worklist settings are not complete.");
        }

        if (!platform.Value.RunsJobs)
        {
            return await ServedElsewhereAsync(listener, stored, cancellationToken);
        }

        if (state.State == "failed")
        {
            return Fail(state.LastError ?? "The worklist server could not start.");
        }

        if (state.State != "listening")
        {
            return Fail("The worklist server has not started yet. It starts within a minute of being turned on, on the server that runs background jobs.");
        }

        var host = IPAddress.TryParse(listener.BindAddress, out var bind) && !bind.Equals(IPAddress.Any) && !bind.Equals(IPAddress.IPv6Any)
            ? bind.ToString()
            : IPAddress.Loopback.ToString();
        var client = clients.Create(host, listener.Port, false, listener.AeTitle, listener.AeTitle);
        client.Logger = new DemotedLogger(loggers.CreateLogger("HyFib.HealthCare360.Integrations.Pacs.Worklist.Dicom"));
        client.ClientOptions.ConnectionTimeoutInMs = (int)Timeout.TotalMilliseconds;
        client.ClientOptions.AssociationRequestTimeoutInMs = (int)Timeout.TotalMilliseconds;
        DicomStatus? status = null;
        await client.AddRequestAsync(new DicomCEchoRequest { OnResponseReceived = (_, response) => status = response.Status });

        var started = time.GetTimestamp();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            await client.SendAsync(timeout.Token);
        }
        catch (DicomAssociationRejectedException ex)
        {
            return Fail($"The worklist server refused its own test ({ex.RejectReason}). Check its AE title and the listen address.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or DicomNetworkException or TimeoutException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return Fail($"Nothing answered on {host}:{listener.Port.ToString(CultureInfo.InvariantCulture)}. Check that the port is not used by another program.");
        }

        var elapsed = time.GetElapsedTime(started).TotalMilliseconds;
        return status == DicomStatus.Success
            ? new IntegrationTestResult(true,
                string.Create(CultureInfo.InvariantCulture, $"The worklist server answered a DICOM echo as {listener.AeTitle} on port {listener.Port} in {elapsed:0} ms."),
                Details(listener, stored))
            : Fail("The worklist server did not answer the DICOM echo.");
    }

    /// <summary>
    /// On an API node of several: the worklist server of the node that runs the jobs, as that node shares it. The echo can be
    /// sent only from that node, which answers no requests.
    /// </summary>
    private async Task<IntegrationTestResult> ServedElsewhereAsync(WorklistListener listener, WorklistSettings stored, CancellationToken cancellationToken)
    {
        var served = (await shared.ReadAsync(cancellationToken)).Worklist;
        if (served.FirstOrDefault(s => s.State == "failed") is { } failed)
        {
            return Fail(failed.LastError ?? "The worklist server could not start.");
        }

        if (served.FirstOrDefault(s => s.State == "listening") is not { } listening)
        {
            return Fail("The worklist server has not started yet. It starts within a minute of being turned on, on the server that runs background jobs.");
        }

        if (!string.Equals(listening.InstrumentCode, listener.AeTitle, StringComparison.OrdinalIgnoreCase) || listening.Port != listener.Port)
        {
            return Fail(string.Create(CultureInfo.InvariantCulture,
                $"The server that runs background jobs still serves the worklist as {listening.InstrumentCode} on port {listening.Port}. It applies the new settings within a minute."));
        }

        return new IntegrationTestResult(true,
            string.Create(CultureInfo.InvariantCulture,
                $"The server that runs background jobs reports the worklist server listening as {listening.InstrumentCode} on port {listening.Port}. The DICOM echo can be sent only from that server."),
            Details(listener, stored));
    }

    private static Dictionary<string, string?> Details(WorklistListener listener, WorklistSettings stored) => new()
    {
        ["aeTitle"] = listener.AeTitle,
        ["port"] = listener.Port.ToString(CultureInfo.InvariantCulture),
        ["modalities"] = string.Join(", ", (stored.Modalities ?? []).Where(m => m.Enabled).Select(m => m.AeTitle?.Trim())),
    };

    private static IntegrationTestResult Fail(string message) => new(false, message);
}
