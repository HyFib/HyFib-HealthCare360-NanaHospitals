using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Calls the HC360 API with the server's JSON conventions (camelCase, enums as strings). A call that fails throws with the
/// status and the server's problem details, so a failing journey says which step failed and why.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public HttpClient Http => http;

    public Task<T> GetAsync<T>(string path) => SendAsync<T>(HttpMethod.Get, path, null);

    public Task<T> PostAsync<T>(string path, object? body = null) => SendAsync<T>(HttpMethod.Post, path, body);

    public Task<T> PutAsync<T>(string path, object? body = null) => SendAsync<T>(HttpMethod.Put, path, body);

    public async Task PostAsync(string path, object? body = null)
    {
        using var response = await SendRawAsync(HttpMethod.Post, path, body);
        await EnsureSuccessAsync(response, HttpMethod.Post, path);
    }

    /// <summary>A PUT whose answer has no body (204 No Content).</summary>
    public async Task PutAsync(string path, object? body)
    {
        using var response = await SendRawAsync(HttpMethod.Put, path, body);
        await EnsureSuccessAsync(response, HttpMethod.Put, path);
    }

    /// <summary>The raw response, for asserting on a refusal (the caller disposes it).</summary>
    public Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        return http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpMethod method, string path)
    {
        await EnsureSuccessAsync(response, method, path);
        return await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken)
               ?? throw new InvalidOperationException($"{method} {path} returned an empty body.");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body)
    {
        using var response = await SendRawAsync(method, path, body);
        return await ReadAsync<T>(response, method, path);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpMethod method, string path)
    {
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            throw new HttpRequestException($"{method} {path} answered {(int)response.StatusCode} {response.StatusCode}: {problem}", null, response.StatusCode);
        }
    }
}
