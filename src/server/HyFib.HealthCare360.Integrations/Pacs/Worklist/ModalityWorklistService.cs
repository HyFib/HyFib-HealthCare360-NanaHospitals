using System.Net;
using System.Net.Sockets;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>
/// One connection to the modality worklist server (fo-dicom makes one per TCP connection). It serves the DICOM
/// Modality Worklist C-FIND and Verification (C-ECHO) and nothing else.
/// <list type="bullet">
/// <item>A connection from an address no allowed modality has is closed before anything is read.</item>
/// <item>An association must call the server's AE title, from an allowed modality's AE title and one of its addresses;
/// others are refused (called or calling AE title not recognised). The server's own test (its AE title, from this machine)
/// may only echo.</item>
/// <item>Every query is checked against the settings in force: a modality removed from the list or from that
/// address meanwhile gets nothing and its connection is closed, and one moved to another facility reads that
/// facility's worklist.</item>
/// <item>Associations beyond the limit are refused as a temporary local limit, and a connection idle for the configured time
/// is closed.</item>
/// </list>
/// </summary>
internal sealed class ModalityWorklistService : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCFindProvider
{
    private static readonly DicomTransferSyntax[] TransferSyntaxes = [DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian];
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);

    /// <summary>A refused association is closed this long after the refusal was sent.</summary>
    private static readonly TimeSpan CloseAfterRefusal = TimeSpan.FromSeconds(2);

    private readonly INetworkStream _stream;
    private readonly ModalityWorklistGateway _gateway;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _closing = new();
    private readonly IPAddress? _remote;
    private readonly IPAddress? _local;
    private ITimer? _watchdog;
    private long _lastActivity;
    private long _refusedAt;
    private int _busy;
    private int _closed;
    private bool _counted;
    private string? _callingAe;

    public ModalityWorklistService(
        INetworkStream stream, Encoding fallbackEncoding, ILogger logger, DicomServiceDependencies dependencies, ModalityWorklistGateway gateway, TimeProvider time)
        : base(stream, fallbackEncoding, logger, dependencies)
    {
        _stream = stream;
        _gateway = gateway;
        _time = time;
        _remote = IPAddress.TryParse(stream.RemoteHost, out var remote) ? WorklistConfig.Normalize(remote) : null;
        _local = IPAddress.TryParse(stream.LocalHost, out var local) ? WorklistConfig.Normalize(local) : null;
        Touch();
    }

    public override Task RunAsync()
    {
        // Strangers never get to send a PDU (whose length header alone could make the server allocate memory).
        if (!_gateway.Admits(_remote, _local))
        {
            _ = _gateway.RefuseAsync(WorklistRefusals.UnknownAddress, null, _remote,
                $"A connection from {_remote?.ToString() ?? "an unknown address"} was closed: no modality on the worklist's allow-list has that address.");
            Close();
            return Task.CompletedTask;
        }

        _watchdog = _time.CreateTimer(_ => Watch(), null, WatchInterval, WatchInterval);
        return base.RunAsync();
    }

    public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        Touch();
        var decision = _gateway.Decide(association.CallingAE, association.CalledAE, _remote, _local);
        if (decision.Reject is { } reason)
        {
            await RefuseAsync(association, decision.Kind!, decision.Reason!, decision.Result,
                DicomRejectSource.ServiceUser, reason);
            return;
        }

        if (!_gateway.TryOpen())
        {
            await RefuseAsync(association, WorklistRefusals.Busy,
                $"{association.CallingAE} at {_remote} was refused: {_gateway.Config.Listener?.MaxAssociations} associations are already open. It may try again.",
                DicomRejectResult.Transient, DicomRejectSource.ServiceProviderPresentation, DicomRejectReason.LocalLimitExceeded);
            return;
        }

        _counted = true;
        _callingAe = decision.Peer?.AeTitle;
        foreach (var context in association.PresentationContexts)
        {
            if (context.AbstractSyntax == DicomUID.Verification
                || (!decision.SelfTest && context.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind))
            {
                context.AcceptTransferSyntaxes(TransferSyntaxes);
            }
            else
            {
                context.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
            }
        }

        await SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        Touch();
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) => Touch();

    public void OnConnectionClosed(Exception exception)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _watchdog?.Dispose();
        _closing.Cancel();
        if (_counted)
        {
            _gateway.Close();
        }
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        Touch();
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        Touch();
        Interlocked.Increment(ref _busy);
        try
        {
            if (_callingAe is not { } callingAe || request.SOPClassUID != DicomUID.ModalityWorklistInformationModelFind)
            {
                yield return new DicomCFindResponse(request, DicomStatus.SOPClassNotSupported);
                yield break;
            }

            // The settings in force now, not those at association time: a modality removed from the list, or moved to
            // another address, reads nothing more, and one moved to another facility reads that facility's worklist.
            if (_gateway.CurrentPeer(callingAe, _remote) is not { } peer)
            {
                await _gateway.RefuseAsync(WorklistRefusals.Withdrawn, callingAe, _remote,
                    $"Modality {callingAe} at {_remote} asked for the worklist on an open association, but it is no "
                    + "longer allowed to read it from there; the association was closed.");
                Interlocked.Exchange(ref _refusedAt, _time.GetTimestamp());
                yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveUnableToProcess);
                yield break;
            }

            var answer = await _gateway.QueryAsync(peer, _remote, request.Dataset, _closing.Token);
            foreach (var dataset in answer.Datasets)
            {
                Touch();
                yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = dataset };
            }

            yield return new DicomCFindResponse(request, answer.Status);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            Touch();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watchdog?.Dispose();
            _closing.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RefuseAsync(
        DicomAssociation association,
        string kind,
        string why,
        DicomRejectResult result,
        DicomRejectSource source,
        DicomRejectReason reason)
    {
        await _gateway.RefuseAsync(kind, association.CallingAE, _remote, why);
        Interlocked.Exchange(ref _refusedAt, _time.GetTimestamp());
        await SendAssociationRejectAsync(result, source, reason);
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, _time.GetTimestamp());

    /// <summary>Closes a connection that was refused, or that sent nothing for the idle time while no query was being answered.</summary>
    private void Watch()
    {
        var refusedAt = Interlocked.Read(ref _refusedAt);
        var refused = refusedAt != 0 && _time.GetElapsedTime(refusedAt) > CloseAfterRefusal;
        var idle = Volatile.Read(ref _busy) == 0 && _time.GetElapsedTime(Interlocked.Read(ref _lastActivity)) > _gateway.Config.IdleTimeout;
        if (refused || idle)
        {
            _watchdog?.Dispose();
            Close();
        }
    }

    /// <summary>Ends the TCP connection; fo-dicom's reading loop then ends the service.</summary>
    private void Close()
    {
        try
        {
            _stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Already closed.
        }
    }
}
