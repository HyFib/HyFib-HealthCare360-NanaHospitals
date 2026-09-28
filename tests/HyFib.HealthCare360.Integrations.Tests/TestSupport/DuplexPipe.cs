using System.IO.Pipelines;

namespace HyFib.HealthCare360.Integrations.Tests.TestSupport;

/// <summary>
/// An in-memory full-duplex connection: <see cref="Server"/> is handed to the protocol session under test and
/// <see cref="Client"/> plays the analyser, exactly as the two ends of a TCP socket would.
/// </summary>
internal sealed class DuplexPipe
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();

    public DuplexPipe()
    {
        Server = new DuplexStream(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
        Client = new DuplexStream(_toClient.Reader.AsStream(), _toServer.Writer.AsStream());
    }

    public Stream Server { get; }

    public Stream Client { get; }

    /// <summary>The analyser hangs up: the session reads end-of-stream.</summary>
    public Task CloseClientAsync() => _toServer.Writer.CompleteAsync().AsTask();

    public async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var read = await Client.ReadAsync(buffer, timeout.Token);
        return read == 1 ? buffer[0] : throw new EndOfStreamException();
    }

    /// <summary>Reads bytes until <paramref name="terminator"/> (inclusive), e.g. an ASTM frame ending in LF.</summary>
    public async Task<byte[]> ReadUntilAsync(Func<List<byte>, bool> terminator, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (!terminator(bytes))
        {
            bytes.Add(await ReadByteAsync(cancellationToken));
        }

        return [.. bytes];
    }

    private sealed class DuplexStream(Stream input, Stream output) : Stream
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

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            input.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            output.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            output.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
