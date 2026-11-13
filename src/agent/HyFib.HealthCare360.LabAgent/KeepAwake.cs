using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

// The agent's P/Invokes load Windows' own DLLs from System32, never a copy planted beside the agent.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace HyFib.HealthCare360.LabAgent;

/// <summary>Windows' power requests, which <c>powercfg /requests</c> lists. The tests stand in for them.</summary>
internal interface IPowerRequests
{
    /// <summary>
    /// Asks Windows not to sleep while the computer is idle, for <paramref name="reason"/>, until the returned request is
    /// disposed. Throws <see cref="Win32Exception"/> when Windows refuses.
    /// </summary>
    IDisposable Hold(string reason);
}

/// <summary>
/// Keeps the lab PC awake while the agent relays at least one serial analyser (DEPLOYMENT.md section 6, Lab analysers).
/// A PC asleep relays nothing, and an analyser may give up on a result nobody takes; before, the PC slept on Windows' own
/// timer unless IT had turned sleep off by hand. It holds one power request ("system required") whose reason names the COM
/// ports, which <c>powercfg /requests</c> shows under SYSTEM, and lets it go once no analyser is relayed or the agent
/// stops. It keeps Windows from sleeping when the PC is idle, not when someone puts it to sleep, and leaves the screen to
/// turn off as it does.
/// </summary>
internal sealed class KeepAwake(IPowerRequests power, ILogger logger) : IDisposable
{
    /// <summary>The words <c>powercfg /requests</c> shows before the ports.</summary>
    public const string ReasonPrefix = "HC360 lab agent: relaying analyser results from ";

    private IDisposable? _held;
    private string? _reason;

    /// <summary>The COM ports of the analysers relayed now, given after every change to them.</summary>
    public void Relaying(IEnumerable<string> ports)
    {
        var names = ports.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var reason = names.Count == 0 ? null : ReasonPrefix + string.Join(", ", names);
        if (string.Equals(reason, _reason, StringComparison.Ordinal))
        {
            return;
        }

        // The new request is taken before the old one is let go, so the PC is never without one while it relays. One that
        // Windows refused is not asked for again until the analysers change: not at every heartbeat.
        var previous = _held;
        _held = null;
        _reason = reason;
        if (reason is null)
        {
            logger.LogInformation("No analyser is relayed: Windows may let this PC sleep again");
        }
        else
        {
            try
            {
                _held = power.Hold(reason);
                logger.LogInformation("Keeping this PC awake while it relays analyser results from {Ports}", string.Join(", ", names));
            }
            catch (Exception ex) when (ex is Win32Exception or EntryPointNotFoundException or DllNotFoundException)
            {
                logger.LogWarning(ex,
                    "Windows did not take this lab agent's request to keep the PC awake while it relays {Ports}: set the PC never to sleep "
                    + "(powercfg /change standby-timeout-ac 0), or it relays nothing while asleep",
                    string.Join(", ", names));
            }
        }

        previous?.Dispose();
    }

    public void Dispose()
    {
        _held?.Dispose();
        _held = null;
        _reason = null;
    }
}

/// <summary>Windows' power requests (PowerCreateRequest), held as "system required": the PC does not sleep when idle.</summary>
internal sealed class WindowsPowerRequests : IPowerRequests
{
    private const uint ReasonContextVersion = 0; // POWER_REQUEST_CONTEXT_VERSION
    private const uint SimpleString = 0x1; // POWER_REQUEST_CONTEXT_SIMPLE_STRING

    public IDisposable Hold(string reason)
    {
        // Windows copies the reason; it is kept until the request is let go all the same.
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext { Version = ReasonContextVersion, Flags = SimpleString, SimpleReasonString = text };
            var request = PowerCreateRequest(ref context);
            if (request.IsInvalid)
            {
                var created = Marshal.GetLastPInvokeError();
                request.Dispose();
                throw new Win32Exception(created);
            }

            if (!PowerSetRequest(request, PowerRequestType.SystemRequired))
            {
                var set = Marshal.GetLastPInvokeError();
                request.Dispose();
                throw new Win32Exception(set);
            }

            return new Request(request, text);
        }
        catch
        {
            Marshal.FreeHGlobal(text);
            throw;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafePowerRequestHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafePowerRequestHandle request, PowerRequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafePowerRequestHandle request, PowerRequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum PowerRequestType
    {
        SystemRequired = 1, // PowerRequestSystemRequired
    }

    /// <summary>
    /// REASON_CONTEXT. A simple reason uses the union's first field; the detailed reason (module, string ID, count, strings)
    /// is laid out after it all the same, so the struct is the size Windows expects on 32 and 64 bits.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        public uint LocalizedReasonId;
        public uint ReasonStringCount;
        public IntPtr ReasonStrings;
    }

    private sealed class SafePowerRequestHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    /// <summary>A request held: cleared, closed, and its reason freed, once.</summary>
    private sealed class Request(SafePowerRequestHandle request, IntPtr reason) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            PowerClearRequest(request, PowerRequestType.SystemRequired);
            request.Dispose();
            Marshal.FreeHGlobal(reason);
        }
    }
}
