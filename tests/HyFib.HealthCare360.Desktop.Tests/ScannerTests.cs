using System.IO;
using HyFib.HealthCare360.Desktop.Bridge;

namespace HyFib.HealthCare360.Desktop.Tests;

public sealed class ScanAssemblerTests
{
    [Fact]
    public void A_scan_ends_at_the_suffix_the_scanner_adds_however_the_port_splits_it()
    {
        var assembler = new ScanAssembler();

        assembler.Append("PCM5").ShouldBeEmpty();
        assembler.Append("00#AB1234\r\nNH26").ShouldBe([new SerialScan("PCM500#AB1234", null)]);
        assembler.Append("000001\tNHLB260000123\n").ShouldBe([new SerialScan("NH26000001", null), new SerialScan("NHLB260000123", null)]);
        assembler.Flush().ShouldBeNull();
    }

    [Fact]
    public void A_scanner_that_sends_no_suffix_finishes_when_it_falls_silent()
    {
        var assembler = new ScanAssembler();

        assembler.Append("  8901234567890 ").ShouldBeEmpty();

        assembler.Flush().ShouldBe(new SerialScan("8901234567890", null));
        assembler.Flush().ShouldBeNull();
    }

    [Fact]
    public void Field_separators_and_nuls_are_dropped_as_a_keyboard_scanner_drops_them()
    {
        var assembler = new ScanAssembler();

        assembler.Append("\0]d2010890123456789017\u001D10AB1234\u001E\u0004\r")
            .ShouldBe([new SerialScan("]d201089012345678901710AB1234", null)]);
    }

    [Fact]
    public void A_garbled_line_is_refused_rather_than_typed()
    {
        var assembler = new ScanAssembler();

        assembler.Append("øà\u0080x\u0007\r").ShouldBe([new SerialScan(null, ScanAssembler.Unreadable)]);
        assembler.Append("PCM500\r").ShouldBe([new SerialScan("PCM500", null)]);
    }

    [Fact]
    public void Noise_with_no_end_is_refused_once_and_the_next_scan_reads()
    {
        var assembler = new ScanAssembler();

        assembler.Append(new string('A', ScanAssembler.MaxLength + 10)).ShouldBeEmpty();

        assembler.Append("\r").ShouldBe([new SerialScan(null, ScanAssembler.TooLong)]);
        assembler.Append("PCM500\r").ShouldBe([new SerialScan("PCM500", null)]);
    }
}

public sealed class ScannerSetupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 5, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-scanner-" + Guid.NewGuid().ToString("N"));

    private ScannerSetupStore Store(string? policy = null)
    {
        Directory.CreateDirectory(_root);
        var policyPath = Path.Combine(_root, "policy.json");
        if (policy is not null)
        {
            File.WriteAllText(policyPath, policy);
        }

        return new ScannerSetupStore(Path.Combine(_root, "Workstation"), policyPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_saved_scanner_is_read_back_for_everyone_on_the_computer()
    {
        var store = Store();

        var saved = store.Save(new WorkstationScanner("com3", 19200, 7, "even", "one", UpdatedBy: " Asha Rao\n"), Now);
        var (setup, locked, problems) = store.Load();

        saved.ShouldBe(new WorkstationScanner("COM3", 19200, 7, "even", "one", Now, "Asha Rao"));
        setup.ShouldBe(saved);
        locked.ShouldBeFalse();
        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Nothing_saved_means_no_serial_scanner()
    {
        Store().Load().Setup.ShouldBe(new WorkstationScanner());
    }

    [Fact]
    public void A_scanner_IT_fixed_in_the_policy_is_used_and_cannot_be_changed_here()
    {
        var store = Store("""{ "scanner": { "port": "COM4" } }""");

        var (setup, locked, _) = store.Load();

        setup.ShouldBe(new WorkstationScanner("COM4"));
        locked.ShouldBeTrue();
        Should.Throw<BridgeException>(() => store.Save(new WorkstationScanner("COM3"), Now)).Code.ShouldBe("not_allowed");
        File.Exists(store.FilePath).ShouldBeFalse();
    }

    [Fact]
    public void A_scanner_that_is_not_a_valid_setup_is_not_used_and_says_why()
    {
        var store = Store("""{ "scanner": { "port": "LPT1" } }""");

        var (setup, _, problems) = store.Load();

        setup.Port.ShouldBeNull();
        problems.ShouldBe(["The scanner is not used: Choose the scanner's COM port (COM1 to COM256)."]);
    }

    [Fact]
    public void An_unreadable_file_is_treated_as_nothing_saved()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{ not json");

        var (setup, _, problems) = store.Load();

        setup.ShouldBe(new WorkstationScanner());
        problems.ShouldBe(["The saved scanner could not be read. Set the scanner again."]);
    }

    [Theory]
    [InlineData("COM0", 9600, 8, "none", "one", "Choose the scanner's COM port (COM1 to COM256).")]
    [InlineData("COM257", 9600, 8, "none", "one", "Choose the scanner's COM port (COM1 to COM256).")]
    [InlineData("\\\\.\\COM3", 9600, 8, "none", "one", "Choose the scanner's COM port (COM1 to COM256).")]
    [InlineData("COM3", 9601, 8, "none", "one", "Choose the scanner's speed: 1200 to 115200 baud, as set on the scanner.")]
    [InlineData("COM3", 9600, 6, "none", "one", "Use 7 or 8 data bits, as set on the scanner.")]
    [InlineData("COM3", 9600, 8, "mark", "one", "Choose no parity, even or odd, as set on the scanner.")]
    [InlineData("COM3", 9600, 8, "none", "onePointFive", "Use 1 or 2 stop bits, as set on the scanner.")]
    public void A_setup_that_does_not_match_a_scanner_is_refused(string port, int baud, int dataBits, string parity, string stopBits, string message)
    {
        var store = Store();

        var refused = Should.Throw<BridgeException>(() => store.Save(new WorkstationScanner(port, baud, dataBits, parity, stopBits), Now));

        refused.Code.ShouldBe("bad_request");
        refused.Message.ShouldBe(message);
        File.Exists(store.FilePath).ShouldBeFalse();
    }

    [Fact]
    public void Saving_no_port_turns_the_serial_scanner_off()
    {
        var store = Store();
        store.Save(new WorkstationScanner("COM3"), Now);

        store.Save(new WorkstationScanner(), Now);

        store.Load().Setup.Port.ShouldBeNull();
    }
}

public sealed class SerialScannerServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 5, 0, 0, TimeSpan.Zero);

    private readonly List<FakePort> _opened = [];
    private readonly List<string> _scans = [];
    private readonly HashSet<string> _ports = new(StringComparer.OrdinalIgnoreCase) { "COM1", "COM3" };
    private readonly SerialScannerService _service;
    private Exception? _openFails;

    public SerialScannerServiceTests()
    {
        _service = new SerialScannerService(Open, () => _ports, new StillTime());
        _service.Scanned += _scans.Add;
    }

    public void Dispose() => _service.Dispose();

    private IScannerPort Open(WorkstationScanner setup)
    {
        if (_openFails is { } failure)
        {
            throw failure;
        }

        var port = new FakePort(setup);
        _opened.Add(port);
        return port;
    }

    [Fact]
    public void Each_code_the_scanner_sends_is_raised_once_complete()
    {
        _service.Use(new WorkstationScanner("COM3", 19200));

        _opened.ShouldHaveSingleItem().Setup.BaudRate.ShouldBe(19200);
        _service.Status.State.ShouldBe("listening");
        _opened[0].Send("PCM500#AB");
        _scans.ShouldBeEmpty();
        _opened[0].Send("1234\r");
        _opened[0].Send("NH26000001");
        _service.OnSilence();

        _scans.ShouldBe(["PCM500#AB1234", "NH26000001"]);
        _service.Status.LastScanAt.ShouldBe(Now);
        _service.Status.LastProblem.ShouldBeNull();
    }

    [Fact]
    public void A_garbled_scan_is_not_raised_and_the_status_says_why()
    {
        _service.Use(new WorkstationScanner("COM3"));

        _opened[0].Send("øàx\r");

        _scans.ShouldBeEmpty();
        _service.Status.LastProblem.ShouldBe(ScanAssembler.Unreadable);
    }

    [Fact]
    public void An_unplugged_scanner_is_listened_to_again_once_it_is_back()
    {
        _ports.Remove("COM3");
        _service.Use(new WorkstationScanner("COM3"));

        _opened.ShouldBeEmpty();
        _service.Status.State.ShouldBe("not_found");

        _ports.Add("COM3");
        _service.Watch();
        _service.Status.State.ShouldBe("listening");

        _opened[0].Alive = false;
        _service.Watch();

        _opened[0].Disposed.ShouldBeTrue();
        _opened.Count.ShouldBe(2);
        _opened[1].Send("NH26000001\r");
        _scans.ShouldBe(["NH26000001"]);
    }

    [Fact]
    public void A_port_another_program_holds_is_tried_again()
    {
        _openFails = new UnauthorizedAccessException();
        _service.Use(new WorkstationScanner("COM3"));

        _service.Status.ShouldBe(new ScannerStatus("busy", "Another program is using COM3. Close it: HC360 tries again every few seconds."));

        _openFails = null;
        _service.Watch();
        _service.Status.State.ShouldBe("listening");
    }

    [Fact]
    public void A_new_setup_closes_the_old_port_and_what_it_still_sends_is_ignored()
    {
        _service.Use(new WorkstationScanner("COM3"));
        var old = _opened[0];

        _service.Use(new WorkstationScanner("COM1"));
        old.Send("NH26000001\r");
        _service.Use(new WorkstationScanner());

        old.Disposed.ShouldBeTrue();
        _opened[1].Disposed.ShouldBeTrue();
        _scans.ShouldBeEmpty();
        _service.Status.ShouldBe(ScannerStatus.Off);
    }

    private sealed class FakePort(WorkstationScanner setup) : IScannerPort
    {
        public WorkstationScanner Setup { get; } = setup;

        public bool Alive { get; set; } = true;

        public bool Disposed { get; private set; }

        public event Action<string>? Received;

        public bool IsAlive => Alive && !Disposed;

        public void Send(string text) => Received?.Invoke(text);

        public void Dispose() => Disposed = true;
    }

    /// <summary>A clock that stands still and timers that never fire: the tests call Watch and OnSilence themselves.</summary>
    private sealed class StillTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Never();

        private sealed class Never : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
