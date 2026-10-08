using System.IO;
using System.IO.Ports;
using System.Text;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>
/// Whether HC360 is listening to the serial scanner: <c>off</c> (none set up), <c>listening</c>, <c>not_found</c> (the
/// port is not on this computer: unplugged), <c>busy</c> (another program has it) or <c>error</c>; with the time of
/// the last scan and why the last one was refused, if it was.
/// </summary>
internal sealed record ScannerStatus(string State, string Message, DateTimeOffset? LastScanAt = null, string? LastProblem = null)
{
    public static ScannerStatus Off { get; } = new("off", "No serial scanner is set up on this computer.");
}

/// <summary>An open serial line to a scanner, reporting the text that arrives.</summary>
internal interface IScannerPort : IDisposable
{
    event Action<string>? Received;

    /// <summary>Still open and still there: a USB scanner that was unplugged is not.</summary>
    bool IsAlive { get; }
}

/// <summary>
/// Listens to the workstation's serial barcode scanner (INT-BAR) and raises <see cref="Scanned"/> with each code, which
/// the bridge hands to the staff app's focused field as if the scanner had typed it. While the port is missing (a USB
/// scanner unplugged), in use by another program, or failed, it tries again every few seconds; a scan the port garbles
/// (the speed or parity does not match the scanner's) is refused and shown on Printers on this computer.
/// </summary>
internal sealed class SerialScannerService : IDisposable
{
    /// <summary>A scanner sends a whole code in a few milliseconds; this much silence ends one sent without a suffix.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    private readonly Func<WorkstationScanner, IScannerPort> _open;
    private readonly Func<IReadOnlyCollection<string>> _ports;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly ScanAssembler _assembler = new();
    private readonly ITimer _silence;
    private readonly ITimer _watch;
    private WorkstationScanner? _setup;
    private IScannerPort? _port;
    private ScannerStatus _status = ScannerStatus.Off;
    private DateTimeOffset? _lastScanAt;
    private string? _lastProblem;
    private bool _disposed;

    public SerialScannerService()
        : this(setup => new SerialScannerPort(setup), PortNames, TimeProvider.System)
    {
    }

    internal SerialScannerService(Func<WorkstationScanner, IScannerPort> open, Func<IReadOnlyCollection<string>> ports, TimeProvider time)
    {
        _open = open;
        _ports = ports;
        _time = time;
        _silence = time.CreateTimer(_ => OnSilence(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watch = time.CreateTimer(_ => Watch(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A code the scanner read, on the thread that read it.</summary>
    public event Action<string>? Scanned;

    public ScannerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status with { LastScanAt = _lastScanAt, LastProblem = _lastProblem };
            }
        }
    }

    /// <summary>The COM ports this computer has now, in number order.</summary>
    public static IReadOnlyList<string> PortNames() =>
        SerialPort.GetPortNames()
            .Select(p => p.Trim().ToUpperInvariant())
            .Where(ScannerSetupStore.IsComPort)
            .Distinct()
            .OrderBy(p => int.Parse(p.AsSpan(3), System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

    /// <summary>Listens to this scanner, closing the one before; a setup with no port stops listening.</summary>
    public void Use(WorkstationScanner setup)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Close();
            _setup = setup.Port is null ? null : setup;
            _lastProblem = null;
            if (_setup is null)
            {
                SetStatus(ScannerStatus.Off);
                _watch.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            Open();
            _watch.Change(RetryEvery, RetryEvery);
        }
    }

    /// <summary>Every few seconds: opens the port again when it went away, was busy or failed.</summary>
    internal void Watch()
    {
        lock (_gate)
        {
            if (_disposed || _setup?.Port is not { } name)
            {
                return;
            }

            if (_port is { IsAlive: true } && _ports().Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            Close();
            Open();
        }
    }

    /// <summary>The scanner fell silent: the code in progress is complete.</summary>
    internal void OnSilence()
    {
        SerialScan? scan;
        lock (_gate)
        {
            scan = _port is null ? null : _assembler.Flush();
            Note(scan is { } s ? [s] : []);
        }

        Deliver(scan is { } done ? [done] : []);
    }

    private void OnReceived(IScannerPort source, string chunk)
    {
        IReadOnlyList<SerialScan> scans;
        lock (_gate)
        {
            if (!ReferenceEquals(source, _port))
            {
                return;
            }

            scans = _assembler.Append(chunk);
            _silence.Change(Silence, Timeout.InfiniteTimeSpan);
            Note(scans);
        }

        Deliver(scans);
    }

    private void Note(IReadOnlyList<SerialScan> scans)
    {
        foreach (var scan in scans)
        {
            if (scan.Problem is { } problem)
            {
                _lastProblem = problem;
                Log.Warning("A serial scan on {Port} was refused: {Problem}", _setup?.Port, problem);
            }
            else
            {
                _lastScanAt = _time.GetUtcNow();
                _lastProblem = null;
            }
        }
    }

    private void Deliver(IReadOnlyList<SerialScan> scans)
    {
        foreach (var scan in scans)
        {
            if (scan.Text is { } text)
            {
                Scanned?.Invoke(text);
            }
        }
    }

    private void Open()
    {
        var setup = _setup!;
        var name = setup.Port!;
        if (!_ports().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            SetStatus(new ScannerStatus("not_found", $"{name} is not on this computer. Check that the scanner is plugged in: HC360 looks for it again every few seconds."));
            return;
        }

        try
        {
            var port = _open(setup);
            port.Received += chunk => OnReceived(port, chunk);
            _port = port;
            SetStatus(new ScannerStatus("listening", $"Listening to the scanner on {name}."));
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus(new ScannerStatus("busy", $"Another program is using {name}. Close it: HC360 tries again every few seconds."));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            Log.Warning(ex, "The scanner port {Port} could not be opened", name);
            SetStatus(new ScannerStatus("error", $"{name} could not be opened ({ex.Message}). HC360 tries again every few seconds."));
        }
    }

    private void Close()
    {
        _silence.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _assembler.Flush();
        var port = _port;
        _port = null;
        port?.Dispose();
    }

    private void SetStatus(ScannerStatus status)
    {
        if (status.State != _status.State)
        {
            Log.Information("Serial scanner: {Message}", status.Message);
        }

        _status = status;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Close();
        }

        _watch.Dispose();
        _silence.Dispose();
    }
}

/// <summary>
/// A scanner's COM port through System.IO.Ports, set as the scanner is (speed, data bits, parity, stop bits), with DTR
/// and RTS raised, which scanners that wait for a ready host need. Bytes are read as Latin-1, so a garbled line stays
/// visible to <see cref="ScanAssembler"/> rather than turning into question marks.
/// </summary>
internal sealed class SerialScannerPort : IScannerPort
{
    private readonly SerialPort _port;

    public SerialScannerPort(WorkstationScanner setup)
    {
        _port = new SerialPort(
            setup.Port!.ToUpperInvariant(),
            setup.BaudRate,
            setup.Parity switch { "even" => Parity.Even, "odd" => Parity.Odd, _ => Parity.None },
            setup.DataBits,
            setup.StopBits == "two" ? StopBits.Two : StopBits.One)
        {
            Encoding = Encoding.Latin1,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 500,
        };
        _port.DataReceived += OnData;
        try
        {
            _port.Open();
        }
        catch
        {
            _port.Dispose();
            throw;
        }
    }

    public event Action<string>? Received;

    public bool IsAlive
    {
        get
        {
            try
            {
                // Asks the driver, which fails once the device is gone; IsOpen alone stays true.
                _ = _port.BytesToRead;
                return _port.IsOpen;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var text = _port.ReadExisting();
            if (text.Length > 0)
            {
                Received?.Invoke(text);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            // Closing, or the device went away: Watch opens it again.
        }
    }

    public void Dispose()
    {
        _port.DataReceived -= OnData;
        try
        {
            _port.Dispose();
        }
        catch (IOException)
        {
            // A port whose device is gone may fail to close; it is released all the same.
        }
    }
}
