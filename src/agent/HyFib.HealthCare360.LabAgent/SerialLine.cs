using System.IO.Ports;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>An open serial line to an analyser: its byte stream, kept open while relays come and go.</summary>
internal interface ISerialLine : IDisposable
{
    Stream Stream { get; }

    /// <summary>Drops what the analyser sent while no relay was open: E1381's timers make it send again.</summary>
    void DiscardInput();
}

internal interface ISerialLineFactory
{
    /// <summary>Opens the port; throws UnauthorizedAccessException (in use), IOException or ArgumentException.</summary>
    ISerialLine Open(SerialSettings settings);
}

/// <summary>
/// COM ports through System.IO.Ports. The settings come from the server, are checked again here (only COM1 to COM256),
/// and map one to one: baud rate, data bits, parity, stop bits, handshake, and DTR/RTS (RTS only when the handshake does
/// not drive it). Reads and writes use the port's stream; nothing is buffered or interpreted.
/// </summary>
internal sealed partial class SerialPortLineFactory : ISerialLineFactory
{
    public ISerialLine Open(SerialSettings settings)
    {
        var port = Configure(settings);
        try
        {
            port.Open();
            return new SerialPortLine(port);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    /// <summary>A configured, unopened port (separate so the mapping can be tested without hardware).</summary>
    internal static SerialPort Configure(SerialSettings settings)
    {
        if (!PortNamePattern().IsMatch(settings.PortName))
        {
            throw new ArgumentException($"'{settings.PortName}' is not a COM port.", nameof(settings));
        }

        var handshake = Enum.Parse<Handshake>(settings.Handshake);
        var port = new SerialPort(settings.PortName.ToUpperInvariant(), settings.BaudRate, Enum.Parse<Parity>(settings.Parity), settings.DataBits, Enum.Parse<StopBits>(settings.StopBits))
        {
            Handshake = handshake,
            DtrEnable = settings.DtrEnable,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 10_000,
            ReadBufferSize = 64 * 1024,
        };

        // With an RTS handshake the driver owns RTS, and setting it at all is refused.
        if (handshake is Handshake.None or Handshake.XOnXOff)
        {
            port.RtsEnable = settings.RtsEnable;
        }

        return port;
    }

    [GeneratedRegex("^COM([1-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-6])$", RegexOptions.IgnoreCase)]
    private static partial Regex PortNamePattern();

    private sealed class SerialPortLine(SerialPort port) : ISerialLine
    {
        public Stream Stream => port.BaseStream;

        public void DiscardInput() => port.DiscardInBuffer();

        public void Dispose() => port.Dispose();
    }
}
