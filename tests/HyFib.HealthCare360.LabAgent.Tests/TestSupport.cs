using System.IO.Pipelines;
using System.Net.WebSockets;

namespace HyFib.HealthCare360.LabAgent.Tests;

/// <summary>Two connected in-memory streams, like the two ends of a cable or a socket.</summary>
internal sealed class Wire
{
    private readonly Pipe _toA = new();
    private readonly Pipe _toB = new();

    public Wire()
    {
        A = new Duplex(_toA.Reader.AsStream(), _toB.Writer.AsStream());
        B = new Duplex(_toB.Reader.AsStream(), _toA.Writer.AsStream());
    }

    public Stream A { get; }

    public Stream B { get; }

    /// <summary>A WebSocket pair over the wire, as the agent (client) and the server would hold it.</summary>
    public static (WebSocket Agent, WebSocket Server) Sockets()
    {
        var wire = new Wire();
        return (
            WebSocket.CreateFromStream(wire.A, new WebSocketCreationOptions { IsServer = false, SubProtocol = AgentProtocol.Subprotocol }),
            WebSocket.CreateFromStream(wire.B, new WebSocketCreationOptions { IsServer = true, SubProtocol = AgentProtocol.Subprotocol }));
    }

    private sealed class Duplex(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            input.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            output.WriteAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>A COM port in memory: the agent holds <see cref="Line"/>, the test plays the analyser on <see cref="Analyser"/>.</summary>
internal sealed class FakeSerial : ISerialLineFactory
{
    private readonly Wire _wire = new();

    public Stream Analyser => _wire.B;

    public int Opened { get; private set; }

    public Exception? OpenFailure { get; set; }

    public ISerialLine Open(SerialSettings settings)
    {
        if (OpenFailure is { } failure)
        {
            throw failure;
        }

        Opened++;
        return new Line(_wire.A);
    }

    private sealed class Line(Stream stream) : ISerialLine
    {
        public Stream Stream => stream;

        public void DiscardInput()
        {
        }

        public void Dispose()
        {
        }
    }
}
