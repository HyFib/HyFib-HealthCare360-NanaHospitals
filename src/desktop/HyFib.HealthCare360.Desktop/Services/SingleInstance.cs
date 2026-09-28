using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// One HC360 Desktop per Windows session. A second launch (or an <c>hc360://</c> link clicked in another app)
/// hands its arguments to the running instance over a named pipe and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _ = Task.Run(ListenAsync);
    }

    public event Action<string[]>? ArgumentsReceived;

    public static SingleInstance? TryAcquire(string[] args)
    {
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var name = $"HyFib.HC360Desktop.{Environment.UserName}.{sessionId}";
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex, name);
        }

        mutex.Dispose();
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            client.Connect(2000);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
            client.Write(payload);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Log.Warning(ex, "Could not reach the running instance");
        }

        return null;
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var json = await reader.ReadToEndAsync(_stop.Token);
                var args = JsonSerializer.Deserialize<string[]>(json) ?? [];
                ArgumentsReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Log.Warning(ex, "Ignored a malformed activation message");
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _stop.Dispose();
    }
}
