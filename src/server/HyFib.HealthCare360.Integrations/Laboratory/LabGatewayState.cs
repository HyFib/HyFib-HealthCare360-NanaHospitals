namespace HyFib.HealthCare360.Integrations.Laboratory;

/// <summary>Live status of one analyser listener for the integration dashboard.</summary>
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
    string? LastError);

/// <summary>In-memory listener status shared by the gateway (writer) and the admin endpoints (reader).</summary>
internal sealed class LabGatewayState(TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Listening(string code, LabProtocol protocol, int port) => Update(code, entry =>
    {
        entry.Protocol = protocol.ToString();
        entry.Port = port;
        entry.State = "listening";
    });

    public void Failed(string code, LabProtocol protocol, int port, string error) => Update(code, entry =>
    {
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
            return _entries.Select(e => new LabListenerStatus(e.Key, e.Value.Protocol, e.Value.Port, e.Value.State, e.Value.Connections,
                    e.Value.Accepted, e.Value.Refused, e.Value.LastMessageAt, e.Value.LastErrorAt, e.Value.LastError))
                .OrderBy(s => s.InstrumentCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

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
        public string Protocol { get; set; } = string.Empty;
        public int Port { get; set; }
        public string State { get; set; } = "starting";
        public int Connections { get; set; }
        public long Accepted { get; set; }
        public long Refused { get; set; }
        public DateTimeOffset? LastMessageAt { get; set; }
        public DateTimeOffset? LastErrorAt { get; set; }
        public string? LastError { get; set; }
    }
}
