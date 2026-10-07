using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.Integrations.Pdf;

/// <summary>
/// The Chrome DevTools Protocol over a pair of anonymous pipes (<c>--remote-debugging-pipe</c>). Unlike
/// <c>--remote-debugging-port</c> this opens no TCP port, so no other program on the server can take control of the
/// browser. Messages are UTF-8 JSON, each ended by a NUL byte.
/// </summary>
internal sealed class DevToolsPipe : IDisposable
{
    // A PDF arrives in chunks (IO.read), so no single message comes near this.
    private const int MaxMessageBytes = 32 * 1024 * 1024;

    private readonly AnonymousPipeServerStream _toBrowser = new(PipeDirection.Out, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream _fromBrowser = new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly Lock _writeLock = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<int, (string? SessionId, Action<string, JsonElement> Handler)> _listeners = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId;
    private int _nextListener;

    /// <summary>The switches that give the browser its ends of the pipes.</summary>
    public IReadOnlyList<string> BrowserArguments =>
    [
        "--remote-debugging-pipe",
        $"--remote-debugging-io-pipes={_toBrowser.GetClientHandleAsString()},{_fromBrowser.GetClientHandleAsString()}",
    ];

    /// <summary>The browser's ends of the pipes, which it must inherit for <see cref="BrowserArguments"/> to work.</summary>
    public IReadOnlyCollection<SafeHandle> BrowserHandles => [_toBrowser.ClientSafePipeHandle, _fromBrowser.ClientSafePipeHandle];

    /// <summary>Completes when the browser closes its end (it exited or crashed).</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Call once the browser has started. Closes this process's copies of the browser's ends, so that the pipe ends
    /// when the browser does, and starts reading.
    /// </summary>
    public void Start()
    {
        _toBrowser.DisposeLocalCopyOfClientHandle();
        _fromBrowser.DisposeLocalCopyOfClientHandle();

        // Anonymous pipes have no overlapped I/O on Windows, so reads block: give them a thread of their own.
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "HC360 PDF browser" };
        reader.Start();
    }

    /// <summary>Sends a command and waits for its result.</summary>
    /// <exception cref="DevToolsException">The browser answered with an error, or the pipe closed first.</exception>
    public async Task<JsonElement> SendAsync(string method, JsonObject? parameters, string? sessionId, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
            if (sessionId is not null)
            {
                message["sessionId"] = sessionId;
            }

            Write(Encoding.UTF8.GetBytes(message.ToJsonString()));
            return await reply.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Calls <paramref name="handler"/> with the name and parameters of every event for the session (null: browser-level
    /// events). It runs on the reading thread, so it must be quick. Dispose the result to stop listening.
    /// </summary>
    public IDisposable Listen(string? sessionId, Action<string, JsonElement> handler)
    {
        var key = Interlocked.Increment(ref _nextListener);
        _listeners[key] = (sessionId, handler);
        return new Subscription(() => _listeners.TryRemove(key, out _));
    }

    public void Dispose()
    {
        _toBrowser.Dispose();
        _fromBrowser.Dispose();
        Fail(new DevToolsException("The PDF browser connection was closed."));
    }

    // A whole message and its terminator in one go: writes from several renders must not interleave. A pipe write only
    // blocks while the browser is not reading; if it hangs, the renderer ends the process and the write fails.
    private void Write(byte[] message)
    {
        lock (_writeLock)
        {
            try
            {
                _toBrowser.Write(message);
                _toBrowser.WriteByte(0);
                _toBrowser.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new DevToolsException("The PDF browser is not running.", ex);
            }
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        var message = new ArrayBufferWriter<byte>(64 * 1024);
        try
        {
            while (true)
            {
                var read = _fromBrowser.Read(buffer);
                if (read == 0)
                {
                    break;
                }

                var chunk = buffer.AsSpan(0, read);
                while (chunk.IndexOf((byte)0) is var end and >= 0)
                {
                    message.Write(chunk[..end]);
                    Dispatch(message.WrittenMemory);
                    message.ResetWrittenCount();
                    chunk = chunk[(end + 1)..];
                }

                if (message.WrittenCount + chunk.Length > MaxMessageBytes)
                {
                    throw new DevToolsException("The PDF browser sent a message that is too large.");
                }

                message.Write(chunk);
            }

            Fail(new DevToolsException("The PDF browser stopped."));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or DevToolsException or JsonException)
        {
            Fail(ex as DevToolsException ?? new DevToolsException("The PDF browser connection failed.", ex));
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idProperty))
        {
            if (!_pending.TryGetValue(idProperty.GetInt32(), out var reply))
            {
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var text = error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText();
                reply.TrySetException(new DevToolsException($"The PDF browser refused a command: {text}"));
            }
            else
            {
                reply.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }

            return;
        }

        if (!root.TryGetProperty("method", out var methodProperty) || methodProperty.GetString() is not { } method)
        {
            return;
        }

        var sessionId = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
        foreach (var (listenerSession, handler) in _listeners.Values)
        {
            if (listenerSession == sessionId)
            {
                handler(method, parameters);
            }
        }
    }

    private void Fail(DevToolsException reason)
    {
        foreach (var reply in _pending.Values)
        {
            reply.TrySetException(reason);
        }

        _closed.TrySetResult();
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}

/// <summary>The browser refused a DevTools command, or the connection to it ended.</summary>
internal sealed class DevToolsException(string message, Exception? innerException = null) : Exception(message, innerException);
