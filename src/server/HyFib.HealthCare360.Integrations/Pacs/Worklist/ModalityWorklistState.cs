using HyFib.HealthCare360.Integrations.Laboratory;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>
/// Live status of the worklist server for the integrations dashboard, shared by the server (writer) and the admin
/// endpoints (reader). It is shown in the same shape as an analyser listener: the AE title, the port, the state, open
/// associations, queries answered and refused, and the last problem (including settings that could not be used).
/// </summary>
internal sealed class ModalityWorklistState(TimeProvider time)
{
    private readonly Lock _lock = new();
    private string? _aeTitle;
    private int _port;
    private string? _state;
    private int _associations;
    private long _answered;
    private long _refused;
    private DateTimeOffset? _lastQueryAt;
    private DateTimeOffset? _lastErrorAt;
    private string? _lastError;
    private IReadOnlyList<string> _problems = [];

    public int Associations
    {
        get
        {
            lock (_lock)
            {
                return _associations;
            }
        }
    }

    public string? State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_lock)
            {
                return _lastError;
            }
        }
    }

    public void Listening(string aeTitle, int port) => Update(() =>
    {
        _aeTitle = aeTitle;
        _port = port;
        _state = "listening";
    });

    public void Failed(string aeTitle, int port, string error) => Update(() =>
    {
        _aeTitle = aeTitle;
        _port = port;
        _state = "failed";
        Error(error);
    });

    public void Stopped() => Update(() =>
    {
        _state = null;
        _associations = 0;
    });

    /// <summary>Settings entries that could not be used; the first is shown as the last problem.</summary>
    public void Problems(IReadOnlyList<string> problems) => Update(() =>
    {
        if (problems.Count > 0 && !_problems.SequenceEqual(problems, StringComparer.Ordinal))
        {
            Error(problems[0] + (problems.Count > 1 ? $" (and {problems.Count - 1} more)" : string.Empty));
        }

        _problems = problems;
    });

    public void Opened() => Update(() => _associations++);

    public void Closed() => Update(() => _associations = Math.Max(0, _associations - 1));

    public void Answered() => Update(() =>
    {
        _answered++;
        _lastQueryAt = time.GetUtcNow();
    });

    public void Refused(string reason) => Update(() =>
    {
        _refused++;
        Error(reason);
    });

    public IReadOnlyList<LabListenerStatus> Snapshot()
    {
        lock (_lock)
        {
            return _state is null || _aeTitle is null
                ? []
                : [new LabListenerStatus(_aeTitle, "DICOM", _port, _state, _associations, _answered, _refused, _lastQueryAt, _lastErrorAt, _lastError)];
        }
    }

    private void Error(string error)
    {
        _lastError = IntegrationTrace.Truncate(error, 500);
        _lastErrorAt = time.GetUtcNow();
    }

    private void Update(Action change)
    {
        lock (_lock)
        {
            change();
        }
    }
}
