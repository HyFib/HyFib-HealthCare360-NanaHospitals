namespace HyFib.HealthCare360.Integrations.Laboratory;

/// <summary>
/// Live status of one analyser connection for the integration dashboard and Lab setup. <c>Transport</c> is <c>tcp</c>,
/// <c>serial</c> or <c>simulator</c>. <c>State</c> is <c>starting</c>, <c>listening</c> or <c>failed</c> for a network
/// port, <c>waiting</c> (no relay from its agent), <c>connected</c> or <c>port_error</c> (the agent could not open the COM
/// port) for a serial analyser, and <c>training</c> for the training analyser. <c>Agent</c> names the lab agent relaying a
/// serial analyser.
/// </summary>
public sealed record LabListenerStatus(
    string InstrumentCode,
    string Protocol,
    int Port,
    string State,
    int ActiveConnections,
    long MessagesAccepted,
    long MessagesRefused,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset? LastErrorAt,
    string? LastError)
{
    public string Transport { get; init; } = "tcp";

    public string? Agent { get; init; }

    public string? SerialPort { get; init; }

    public DateTimeOffset? ConnectedSince { get; init; }
}

/// <summary>In-memory connection status shared by the gateway and relays (writers) and the admin endpoints (reader).</summary>
internal sealed class LabGatewayState(TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Listening(string code, LabProtocol protocol, int port) => Update(code, entry =>
    {
        entry.Transport = "tcp";
        entry.Protocol = protocol.ToString();
        entry.Port = port;
        entry.State = "listening";
    });

    public void Failed(string code, LabProtocol protocol, int port, string error) => Update(code, entry =>
    {
        entry.Transport = "tcp";
        entry.Protocol = protocol.ToString();
        entry.Port = port;
        entry.State = "failed";
        entry.LastError = error;
        entry.LastErrorAt = time.GetUtcNow();
    });

    public void Stopped(string code)
    {
        lock (_lock)
        {
            _entries.Remove(code);
        }
    }

    /// <summary>A serial analyser is configured: it shows as waiting until its agent's relay connects.</summary>
    public void SerialExpected(string code, LabProtocol protocol, string serialPort) => Update(code, entry =>
    {
        entry.Transport = "serial";
        entry.Protocol = protocol.ToString();
        entry.Port = 0;
        entry.SerialPort = serialPort;
        if (entry.State is not ("connected" or "port_error"))
        {
            entry.State = "waiting";
        }
    });

    /// <summary>A training analyser is set up: it is always ready, since results are entered on screen.</summary>
    public void TrainingReady(string code, LabProtocol protocol) => Update(code, entry =>
    {
        entry.Transport = "simulator";
        entry.Protocol = protocol.ToString();
        entry.Port = 0;
        entry.State = "training";
    });

    /// <summary>Keeps only the serial analysers still configured (network ports are managed by their listeners).</summary>
    public void RetainSerial(IReadOnlyCollection<string> codes) => Retain("serial", codes);

    /// <summary>Keeps only the training analysers still configured.</summary>
    public void RetainTraining(IReadOnlyCollection<string> codes) => Retain("simulator", codes);

    private void Retain(string transport, IReadOnlyCollection<string> codes)
    {
        lock (_lock)
        {
            foreach (var code in _entries.Where(e => e.Value.Transport == transport && !codes.Contains(e.Key, StringComparer.OrdinalIgnoreCase)).Select(e => e.Key).ToList())
            {
                _entries.Remove(code);
            }
        }
    }

    public void RelayConnected(string code, LabProtocol protocol, string agent, string? serialPort) => Update(code, entry =>
    {
        entry.Transport = "serial";
        entry.Protocol = protocol.ToString();
        entry.State = "connected";
        entry.Agent = agent;
        entry.SerialPort = serialPort ?? entry.SerialPort;
        entry.ConnectedSince = time.GetUtcNow();
        entry.Connections = 1;
    });

    public void RelayClosed(string code) => Update(code, entry =>
    {
        entry.State = "waiting";
        entry.ConnectedSince = null;
        entry.Connections = 0;
    });

    /// <summary>What the agent reported about the COM port in its last heartbeat.</summary>
    public void SerialPortReported(string code, string agent, bool open, string? error) => Update(code, entry =>
    {
        entry.Transport = "serial";
        entry.Agent = agent;
        if (open)
        {
            if (entry.State == "port_error")
            {
                entry.State = entry.ConnectedSince is null ? "waiting" : "connected";
            }

            return;
        }

        entry.State = "port_error";
        entry.LastError = IntegrationTrace.Truncate(error ?? "The lab agent could not open the port.", 300);
        entry.LastErrorAt = time.GetUtcNow();
    });

    public void ConnectionOpened(string code) => Update(code, entry => entry.Connections++);

    public void ConnectionClosed(string code) => Update(code, entry => entry.Connections = Math.Max(0, entry.Connections - 1));

    public void RecordMessage(string code, bool accepted, string? error) => Update(code, entry =>
    {
        var now = time.GetUtcNow();
        entry.LastMessageAt = now;
        if (accepted)
        {
            entry.Accepted++;
        }
        else
        {
            entry.Refused++;
            entry.LastError = IntegrationTrace.Truncate(error, 500);
            entry.LastErrorAt = now;
        }
    });

    public IReadOnlyList<LabListenerStatus> Snapshot()
    {
        lock (_lock)
        {
            return _entries.Select(e => ToStatus(e.Key, e.Value))
                .OrderBy(s => s.InstrumentCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static LabListenerStatus ToStatus(string code, Entry entry) =>
        new(code, entry.Protocol, entry.Port, entry.State, entry.Connections, entry.Accepted, entry.Refused, entry.LastMessageAt, entry.LastErrorAt, entry.LastError)
        {
            Transport = entry.Transport,
            Agent = entry.Agent,
            SerialPort = entry.SerialPort,
            ConnectedSince = entry.ConnectedSince,
        };

    private void Update(string code, Action<Entry> change)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(code, out var entry))
            {
                entry = new Entry();
                _entries[code] = entry;
            }

            change(entry);
        }
    }

    private sealed class Entry
    {
        public string Transport { get; set; } = "tcp";
        public string Protocol { get; set; } = string.Empty;
        public int Port { get; set; }
        public string State { get; set; } = "starting";
        public int Connections { get; set; }
        public long Accepted { get; set; }
        public long Refused { get; set; }
        public DateTimeOffset? LastMessageAt { get; set; }
        public DateTimeOffset? LastErrorAt { get; set; }
        public string? LastError { get; set; }
        public string? Agent { get; set; }
        public string? SerialPort { get; set; }
        public DateTimeOffset? ConnectedSince { get; set; }
    }
}
