using System.Net;
using System.Net.Sockets;
using HyFib.HealthCare360.Integrations.Biometric;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;
using static HyFib.HealthCare360.Integrations.Biometric.ZkTecoProtocol;

namespace HyFib.HealthCare360.Integrations.Tests.Biometric;

public sealed class ZkTecoTerminalTests : IAsyncLifetime
{
    private static readonly BiometricSettings Settings = new() { Enabled = true, Provider = "zkteco", TimeoutSeconds = 10 };

    /// <summary>The commands that only read, plus connecting and leaving.</summary>
    private static readonly ushort[] ReadOnlyCommands =
        [CmdConnect, CmdAuth, CmdOptionsRead, CmdGetTime, CmdGetFreeSizes, CmdPrepareBuffer, CmdReadBuffer, CmdFreeData, CmdExit];

    private readonly FakeZkTecoTerminal _device = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ZkTecoTerminal Provider => new(_tracing.Trace);

    private AttendanceTerminal Terminal => new(Guid.NewGuid(), "CQZ7232460163", "127.0.0.1", _device.Port);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _device.DisposeAsync();

    [Fact]
    public async Task Reads_every_punch_in_pieces_and_only_reads()
    {
        // 2,000 punches do not fit one piece (65,472 bytes), so they come in two, each as announced data packets.
        var start = new DateTime(2026, 9, 1, 8, 0, 0);
        _device.Records = [.. Enumerable.Range(0, 2000).SelectMany(i => FakeZkTecoTerminal.LongRecord((ushort)i, $"{1000 + (i % 50)}", start.AddMinutes(i * 30)))];

        var read = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets(), Ct);

        read.Success.ShouldBeTrue(read.Error);
        read.TerminalTime.ShouldBe(new DateTime(2026, 10, 8, 9, 30, 0));
        read.Records.Count.ShouldBe(2000);
        read.Records[0].ShouldBe(new TerminalPunchRecord("1000", start, 0, 1, null));
        read.Records[^1].ShouldBe(new TerminalPunchRecord("1049", start.AddMinutes(1999 * 30), 0, 1, null));
        _device.Commands.Count(c => c == CmdReadBuffer).ShouldBe(2);
        _device.Commands.ShouldAllBe(c => ReadOnlyCommands.Contains(c));
        _device.Commands[^1].ShouldBe(CmdExit);

        var log = _tracing.Entries.ShouldHaveSingleItem();
        (log.Integration, log.Provider, log.Operation, log.Success, log.Reference).ShouldBe(("biometric", "zkteco", "terminal.read", true, "CQZ7232460163"));
        log.Response.ShouldBe("punches=2000 unreadable=0");
    }

    [Fact]
    public async Task Reads_a_small_buffer_sent_at_once_and_skips_the_buffer_when_there_are_no_punches()
    {
        _device.SendBufferAtOnce = true;
        _device.Records = FakeZkTecoTerminal.LongRecord(1, "1001", new DateTime(2026, 10, 8, 9, 2, 15));

        var read = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets(), Ct);

        read.Records.ShouldHaveSingleItem().Pin.ShouldBe("1001");
        _device.Commands.ShouldNotContain(CmdReadBuffer);

        _device.Records = [];
        var empty = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets(), Ct);

        empty.Success.ShouldBeTrue();
        empty.Records.ShouldBeEmpty();
        _device.Commands.Count(c => c == CmdPrepareBuffer).ShouldBe(1);
    }

    [Fact]
    public async Task Gives_the_communication_key_the_terminal_asks_for()
    {
        _device.CommKey = 123456;

        var read = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets { CommKey = " 123456 " }, Ct);

        read.Success.ShouldBeTrue(read.Error);
        _device.Commands.ShouldContain(CmdAuth);
        _tracing.AllText.ShouldNotContain("123456");
    }

    [Fact]
    public async Task Says_when_the_terminal_wants_a_key_HC360_does_not_have_or_refuses_the_one_it_has()
    {
        _device.CommKey = 123456;

        var none = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets(), Ct);
        var wrong = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets { CommKey = "654321" }, Ct);
        var invalid = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets { CommKey = "12ab" }, Ct);

        none.Error.ShouldBe("The terminal asks for a communication key (COMM Key). Enter it in Administration › Integrations › Biometric attendance.");
        wrong.Error!.ShouldStartWith("The terminal refused the communication key.");
        invalid.Error!.ShouldContain("must be a number from 0 to 999999");
        _tracing.Entries.Count(e => !e.Success).ShouldBe(3);
    }

    [Fact]
    public async Task Does_not_read_a_terminal_that_reports_another_serial_number()
    {
        _device.SerialNumber = "OTHER0001";
        _device.Records = FakeZkTecoTerminal.LongRecord(1, "1001", new DateTime(2026, 10, 8, 9, 2, 15));

        var read = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets(), Ct);

        read.Success.ShouldBeFalse();
        read.Error.ShouldBe($"The terminal at 127.0.0.1:{_device.Port} has serial number OTHER0001, not CQZ7232460163. Check the address registered for this terminal.");
        _device.Commands.ShouldNotContain(CmdPrepareBuffer);
    }

    [Fact]
    public async Task Says_what_is_wrong_when_nothing_or_something_else_answers()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        _device.Banner = "SSH-2.0-OpenSSH_9.6\r\n"u8.ToArray();

        var refused = await Provider.ReadAsync(Terminal with { Port = port }, Settings, new BiometricSecrets(), Ct);
        var other = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets(), Ct);
        var name = await Provider.ReadAsync(Terminal with { Host = "terminal.local" }, Settings, new BiometricSecrets(), Ct);

        refused.Error.ShouldBe($"The terminal at 127.0.0.1:{port} refused the connection. Check the address and that the terminal's TCP port is {port}.");
        other.Error.ShouldBe("The device at this address does not answer like an attendance terminal.");
        name.Error.ShouldBe("Enter the terminal's IP address in HR › Attendance terminals.");
    }

    [Fact]
    public async Task Says_which_firmware_cannot_be_read()
    {
        _device.Records = new byte[80];
        _device.RecordCount = 10;

        var read = await Provider.ReadAsync(Terminal, Settings, new BiometricSecrets(), Ct);

        read.Error!.ShouldStartWith("This terminal's firmware keeps attendance in a form too old to read.");
    }

    [Fact]
    public async Task Sets_the_clock_only_on_the_registered_terminal()
    {
        var error = await Provider.SetClockAsync(Terminal, new DateTime(2026, 10, 8, 9, 31, 5), Settings, new BiometricSecrets(), Ct);

        error.ShouldBeNull();
        _device.ClockSetTo.ShouldBe(new DateTime(2026, 10, 8, 9, 31, 5));
        _tracing.Entries.ShouldHaveSingleItem().Operation.ShouldBe("terminal.set_clock");

        _device.SerialNumber = "OTHER0001";
        var other = await Provider.SetClockAsync(Terminal, new DateTime(2026, 10, 8, 10, 0, 0), Settings, new BiometricSecrets(), Ct);

        other.ShouldNotBeNull();
        _device.ClockSetTo.ShouldBe(new DateTime(2026, 10, 8, 9, 31, 5));
    }
}
