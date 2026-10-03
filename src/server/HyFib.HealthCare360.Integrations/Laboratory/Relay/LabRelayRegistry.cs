namespace HyFib.HealthCare360.Integrations.Laboratory.Relay;

/// <summary>
/// The live relays, one per serial analyser. A new relay for an analyser replaces the old one (an agent that lost its
/// connection reconnects before the server notices the old one is dead), and ending a lease ends that relay's session.
/// </summary>
internal sealed class LabRelayRegistry(LabGatewayState state, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RelayLease> _live = new(StringComparer.OrdinalIgnoreCase);

    public RelayLease Open(LabInstrumentSettings instrument, Guid deviceId, string agentName)
    {
        var lease = new RelayLease(this, instrument.Code, deviceId, time.GetUtcNow());
        RelayLease? replaced;
        lock (_lock)
        {
            _live.TryGetValue(instrument.Code, out replaced);
            _live[instrument.Code] = lease;
        }

        replaced?.End(RelayLease.Replaced);
        state.RelayConnected(instrument.Code, instrument.Protocol, agentName, instrument.Serial?.PortName.ToUpperInvariant());
        return lease;
    }

    public bool IsLive(string code)
    {
        lock (_lock)
        {
            return _live.ContainsKey(code);
        }
    }

    /// <summary>Agents relaying at least one analyser now.</summary>
    public IReadOnlySet<Guid> ConnectedAgents()
    {
        lock (_lock)
        {
            return _live.Values.Select(l => l.DeviceId).ToHashSet();
        }
    }

    /// <summary>Ends the relays of an agent that may no longer relay them (revoked, or the analyser was reassigned).</summary>
    public void EndForAgent(Guid deviceId, Func<string, bool> stillAssigned)
    {
        List<RelayLease> ending;
        lock (_lock)
        {
            ending = _live.Values.Where(l => l.DeviceId == deviceId && !stillAssigned(l.Code)).ToList();
        }

        foreach (var lease in ending)
        {
            lease.End(RelayLease.Unassigned);
        }
    }

    internal void Release(RelayLease lease)
    {
        lock (_lock)
        {
            if (!_live.TryGetValue(lease.Code, out var current) || !ReferenceEquals(current, lease))
            {
                return;
            }

            _live.Remove(lease.Code);
        }

        state.RelayClosed(lease.Code);
    }
}

/// <summary>
/// One live relay. <see cref="Ended"/> fires when a newer relay replaced it or its agent may no longer relay, and
/// <see cref="EndReason"/> says which, for the close frame the agent logs.
/// </summary>
internal sealed class RelayLease(LabRelayRegistry registry, string code, Guid deviceId, DateTimeOffset since) : IDisposable
{
    public const string Replaced = "Replaced by a newer relay of this analyser.";
    public const string Unassigned = "The analyser is no longer assigned to this lab agent.";
    public const string Revoked = "This lab agent was revoked.";
    public const string Changed = "The analyser's connection settings changed.";
    public const string GatewayOff = "The laboratory gateway was switched off.";

    private readonly CancellationTokenSource _ended = new();
    private string? _endReason;
    private int _disposed;

    public string Code => code;

    public Guid DeviceId => deviceId;

    public DateTimeOffset Since => since;

    public CancellationToken Ended => _ended.Token;

    /// <summary>Why the relay was ended (the first reason given), or null while it is not.</summary>
    public string? EndReason => Volatile.Read(ref _endReason);

    public void End(string reason)
    {
        Interlocked.CompareExchange(ref _endReason, reason, null);
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _ended.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Released at the same moment.
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            registry.Release(this);
            _ended.Dispose();
        }
    }
}
