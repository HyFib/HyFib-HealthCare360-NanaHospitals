using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using HyFib.HealthCare360.Integrations.Features.Admin;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>
/// The administrator's test of the worklist server: a DICOM C-ECHO from this machine to the server, calling its own AE
/// title (the server accepts that only from this machine, and only for echoes). It shows that the server is listening on
/// its port and answers to its AE title. It runs on the server that answers the request, so in a split deployment run it
/// on the node that runs background jobs, where the worklist is served.
/// </summary>
internal sealed class ModalityWorklistSelfTest(
    IntegrationSettingsReader settings,
    IDicomClientFactory clients,
    ModalityWorklistState state,
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
                new Dictionary<string, string?>
                {
                    ["aeTitle"] = listener.AeTitle,
                    ["port"] = listener.Port.ToString(CultureInfo.InvariantCulture),
                    ["modalities"] = string.Join(", ", (stored.Modalities ?? []).Where(m => m.Enabled).Select(m => m.AeTitle?.Trim())),
                })
            : Fail("The worklist server did not answer the DICOM echo.");
    }

    private static IntegrationTestResult Fail(string message) => new(false, message);
}
