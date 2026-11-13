using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.LabAgent.Tests;

/// <summary>
/// The lab agent keeps the lab PC awake while it relays an analyser: a PC asleep relays nothing, and an analyser may give
/// up on a result nobody takes. Before, the PC slept on Windows' own timer unless IT had turned sleep off by hand.
/// </summary>
public sealed class KeepAwakeTests
{
    private static readonly Assignment Xn550 =
        new("XN550", "Haematology analyser", "Astm", new SerialSettings("COM3", 9600, 8, "None", "One", "None", true, true));

    private readonly FakePowerRequests _power = new();
    private readonly ListLogger _logger = new();

    /// <summary>
    /// The agent itself: its relays, as the server's heartbeats start, move and stop them, hold the request, and once none
    /// runs (the analyser removed, the gateway turned off, the agent revoked or stopping) Windows may let the PC sleep.
    /// </summary>
    [Fact]
    public async Task The_agent_keeps_the_PC_awake_while_it_relays_an_analyser_and_lets_it_sleep_once_it_relays_none()
    {
        var paths = new AgentPaths(Path.Combine(Path.GetTempPath(), $"hc360-lab-agent-{Guid.NewGuid():N}.json"));
        using var worker = new AgentWorker(paths, new FakeSerial(), _power, TimeProvider.System, NullLoggerFactory.Instance);
        var server = new NeverConnects();

        await worker.ApplyAsync([Xn550], server);
        _power.Held.ShouldBe([$"{KeepAwake.ReasonPrefix}COM3"]);

        await worker.ApplyAsync([Xn550 with { Port = Xn550.Port with { PortName = "COM5" } }], server);
        _power.Held.ShouldBe([$"{KeepAwake.ReasonPrefix}COM5"], "an analyser moved to another port is named by its new one");

        await worker.ApplyAsync([], server);
        _power.Held.ShouldBeEmpty("no analyser is assigned any more");

        await worker.ApplyAsync([Xn550], server);
        await worker.StopAllAsync();
        _power.Held.ShouldBeEmpty("the agent stopped relaying");
    }

    [Fact]
    public void Holds_a_request_while_an_analyser_is_relayed_and_lets_it_go_once_none_is()
    {
        using var awake = new KeepAwake(_power, _logger);

        awake.Relaying(["COM3"]);
        _power.Held.ShouldBe(["HC360 lab agent: relaying analyser results from COM3"]);

        awake.Relaying(["COM3"]);
        _power.Taken.ShouldBe(1, "a heartbeat with the same analysers changes nothing");

        awake.Relaying([]);
        _power.Held.ShouldBeEmpty();
    }

    [Fact]
    public void Names_every_port_and_takes_the_new_request_before_it_lets_go_of_the_old()
    {
        using var awake = new KeepAwake(_power, _logger);
        awake.Relaying(["COM4", "com3", "COM3"]);
        _power.Held.ShouldBe(["HC360 lab agent: relaying analyser results from com3, COM4"]);

        awake.Relaying(["COM3"]);

        _power.Log.ShouldBe([
            "hold HC360 lab agent: relaying analyser results from com3, COM4",
            "hold HC360 lab agent: relaying analyser results from COM3",
            "release HC360 lab agent: relaying analyser results from com3, COM4"],
            "the PC is never left without a request while it relays");
        _power.Held.ShouldBe(["HC360 lab agent: relaying analyser results from COM3"]);
    }

    [Fact]
    public void Lets_the_PC_sleep_again_when_the_agent_stops()
    {
        var awake = new KeepAwake(_power, _logger);
        awake.Relaying(["COM3"]);

        awake.Dispose();

        _power.Held.ShouldBeEmpty();
    }

    [Fact]
    public void Says_so_once_when_Windows_refuses_and_tries_again_when_the_analysers_change()
    {
        _power.Refuse = true;
        using var awake = new KeepAwake(_power, _logger);

        awake.Relaying(["COM3"]);
        awake.Relaying(["COM3"]);

        _power.Taken.ShouldBe(1, "not again at every heartbeat");
        _logger.Warnings.Count.ShouldBe(1);
        _logger.Warnings[0].ShouldContain("COM3");
        _logger.Warnings[0].ShouldContain("powercfg /change standby-timeout-ac 0", Case.Insensitive, "it says how to keep the PC awake by hand");

        _power.Refuse = false;
        awake.Relaying(["COM3", "COM4"]);
        _power.Held.ShouldBe(["HC360 lab agent: relaying analyser results from COM3, COM4"]);
    }

    /// <summary>
    /// The analysers change while a request is held, and Windows refuses the new one: the one held is kept, so the PC stays
    /// awake while its analysers are relayed, and it is still let go once none is.
    /// </summary>
    [Fact]
    public void Keeps_the_request_it_holds_when_Windows_refuses_the_new_one()
    {
        using var awake = new KeepAwake(_power, _logger);
        awake.Relaying(["COM3"]);

        _power.Refuse = true;
        awake.Relaying(["COM3", "COM4"]);

        _power.Held.ShouldBe([$"{KeepAwake.ReasonPrefix}COM3"], "the PC stays awake while it relays");
        _logger.Warnings.Count.ShouldBe(1);
        _logger.Warnings[0].ShouldContain("kept", Case.Insensitive, "it says the request it held is kept");

        _power.Refuse = false;
        awake.Relaying([]);
        _power.Held.ShouldBeEmpty();
    }

    /// <summary>
    /// Windows' own list of what keeps a computer awake (<c>powercfg /requests</c>, under SYSTEM) names the request while it
    /// is held, and not once it is let go: what IT checks on the lab PC. powercfg needs an elevated prompt, which the
    /// Windows CI job has.
    /// </summary>
    [Fact]
    public async Task Windows_lists_the_request_while_it_is_held_and_not_once_it_is_let_go()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Power requests are a Windows feature; the Windows CI job runs this.");
        var before = await PowerCfgRequestsAsync();
        Assert.SkipWhen(before is null, "powercfg /requests needs an elevated prompt; the Windows CI job has one.");
        var reason = $"{KeepAwake.ReasonPrefix}COM9 (test {Guid.NewGuid():N})";
        before.ShouldNotContain(reason);

        using (new WindowsPowerRequests().Hold(reason))
        {
            var held = await PowerCfgRequestsAsync();
            held.ShouldNotBeNull();
            held.ShouldContain(reason);
        }

        var after = await PowerCfgRequestsAsync();
        after.ShouldNotBeNull();
        after.ShouldNotContain(reason);
    }

    /// <summary>What <c>powercfg /requests</c> prints, or null when it does not run (it needs an elevated prompt).</summary>
    private static async Task<string?> PowerCfgRequestsAsync()
    {
        var start = new ProcessStartInfo("powercfg.exe", "/requests")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process.ExitCode == 0 ? output : null;
    }

    /// <summary>A server the relay never reaches: it waits until the relay stops.</summary>
    private sealed class NeverConnects : IRelayConnector
    {
        public async Task<WebSocket> ConnectAsync(string instrumentCode, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>Windows' power requests, recorded: what is held now, and every hold and release in order.</summary>
    private sealed class FakePowerRequests : IPowerRequests
    {
        public List<string> Held { get; } = [];

        public List<string> Log { get; } = [];

        public int Taken { get; private set; }

        public bool Refuse { get; set; }

        public IDisposable Hold(string reason)
        {
            Taken++;
            if (Refuse)
            {
                throw new Win32Exception(5); // Access is denied.
            }

            Held.Add(reason);
            Log.Add($"hold {reason}");
            return new Request(this, reason);
        }

        private sealed class Request(FakePowerRequests owner, string reason) : IDisposable
        {
            public void Dispose()
            {
                if (owner.Held.Remove(reason))
                {
                    owner.Log.Add($"release {reason}");
                }
            }
        }
    }

    /// <summary>The warnings logged, as they would read.</summary>
    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
