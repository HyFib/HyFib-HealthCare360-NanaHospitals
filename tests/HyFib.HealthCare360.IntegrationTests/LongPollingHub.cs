using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A SignalR hub connection over the long-polling transport, made of plain HTTP requests as the signed-in user (the tests
/// have no SignalR client): enough to call a hub method and read whether it succeeded. Messages use the JSON hub protocol,
/// each ending with the record separator.
/// </summary>
internal sealed class LongPollingHub : IAsyncDisposable
{
    private const char RecordSeparator = '\u001e';

    /// <summary>A poll waits for data up to the server's long-polling timeout, so a few are plenty for one answer.</summary>
    private const int MaxPolls = 5;

    private readonly HttpClient _http;
    private readonly string _connection;
    private int _invocations;

    private LongPollingHub(HttpClient http, string connection, string connectionId)
    {
        _http = http;
        _connection = connection;
        ConnectionId = connectionId;
    }

    /// <summary>The connection's id on the server, as a hub method's <c>Context.ConnectionId</c>.</summary>
    public string ConnectionId { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Negotiates a connection to the hub at <paramref name="path"/> and completes the protocol handshake.</summary>
    public static async Task<LongPollingHub> ConnectAsync(ApiClient client, string path)
    {
        using var negotiate = await client.Http.PostAsync($"{path}/negotiate?negotiateVersion=1", content: null, Ct);
        negotiate.StatusCode.ShouldBe(HttpStatusCode.OK, $"{path} should accept the connection.");
        var negotiated = await negotiate.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var token = negotiated.GetProperty("connectionToken").GetString().ShouldNotBeNull();
        var id = negotiated.GetProperty("connectionId").GetString().ShouldNotBeNull();
        var hub = new LongPollingHub(client.Http, $"{path}?id={Uri.EscapeDataString(token)}", id);

        // The first poll finishes opening the connection and returns at once, without data.
        using (var opened = await client.Http.GetAsync(hub._connection, Ct))
        {
            opened.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await hub.SendAsync("""{"protocol":"json","version":1}""");
        var handshake = await hub.ReceiveAsync(_ => true);
        handshake.TryGetProperty("error", out var error).ShouldBeFalse($"The hub refused the handshake: {error}");
        return hub;
    }

    /// <summary>Calls hub method <paramref name="target"/>; returns the error it answered with, or null when it succeeded.</summary>
    public async Task<string?> InvokeAsync(string target, params object?[] arguments)
    {
        var id = (++_invocations).ToString(CultureInfo.InvariantCulture);
        await SendAsync(JsonSerializer.Serialize(new { type = 1, invocationId = id, target, arguments }, ApiClient.Json));
        var completion = await ReceiveAsync(message => message.TryGetProperty("type", out var type) && type.GetInt32() == 3
                                                       && message.GetProperty("invocationId").GetString() == id);
        return completion.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    /// <summary>Closes the connection on the server.</summary>
    public async ValueTask DisposeAsync()
    {
        using var closed = await _http.DeleteAsync(_connection, Ct);
    }

    private async Task SendAsync(string message)
    {
        using var content = new StringContent(message + RecordSeparator, Encoding.UTF8, "text/plain");
        using var response = await _http.PostAsync(_connection, content, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Polls until a message <paramref name="wanted"/> accepts arrives; pings and other messages are passed over.</summary>
    public async Task<JsonElement> ReceiveAsync(Func<JsonElement, bool> wanted)
    {
        for (var poll = 0; poll < MaxPolls; poll++)
        {
            using var response = await _http.GetAsync(_connection, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, "The hub closed the connection.");
            var text = await response.Content.ReadAsStringAsync(Ct);
            foreach (var record in text.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                using var message = JsonDocument.Parse(record);
                if (wanted(message.RootElement))
                {
                    return message.RootElement.Clone();
                }
            }
        }

        throw new InvalidOperationException($"The hub sent no answer in {MaxPolls} polls.");
    }
}
