using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>
/// Meta WhatsApp Cloud API: approved template messages with body parameters, documents (reports, bills) uploaded to
/// <c>/{phone-number-id}/media</c> first and then sent by media id, and free-form text replies. Parameter values and
/// texts can carry PHI, so only the template name, parameter count and text length are logged.
/// </summary>
internal sealed partial class MetaWhatsAppProvider(HttpClient http, IntegrationTrace trace) : IWhatsAppProvider
{
    private static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/png", "text/plain",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    public string Key => ProviderKeys.MetaCloud;

    public async Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (Validate(settings, secrets) is { } problem)
        {
            return await FailAsync("template", message.Reference, started, problem);
        }

        var language = settings.LanguageCodes?.GetValueOrDefault(message.Language) ?? message.Language;
        var payload = TemplatePayload(message.To, message.TemplateName, language, message.BodyParameters);
        var summary = $"{{\"to\":\"{Phone.Mask(message.To)}\",\"template\":\"{message.TemplateName}\",\"language\":\"{language}\",\"parameters\":{message.BodyParameters.Count}}}";
        return await SendMessageAsync(settings, secrets, payload, "template", summary, message.Reference, started, cancellationToken);
    }

    public async Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (Validate(settings, secrets) is { } problem)
        {
            return await FailAsync("document", message.Reference, started, problem);
        }

        if (!DocumentTypes.Contains(message.ContentType))
        {
            return await FailAsync("document", message.Reference, started, $"WhatsApp cannot send documents of type {message.ContentType}.");
        }

        if (message.Content.Length > Math.Clamp(settings.MaxDocumentMegabytes, 1, 100) * 1024L * 1024L)
        {
            return await FailAsync("document", message.Reference, started, "The document is larger than the configured WhatsApp limit.");
        }

        var fileName = SafeFileName(message.FileName);
        var summary = $"{{\"to\":\"{Phone.Mask(message.To)}\",\"file\":\"{fileName}\",\"bytes\":{message.Content.Length}}}";
        string? mediaId;
        try
        {
            mediaId = await UploadAsync(settings, secrets, message, fileName, cancellationToken);
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            return await FailAsync("document", message.Reference, started, "Upload failed: " + IntegrationErrors.Describe(ex, "WhatsApp"), summary);
        }

        if (mediaId is null)
        {
            return await FailAsync("document", message.Reference, started, "WhatsApp did not accept the document upload.", summary);
        }

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = message.To.TrimStart('+'),
            type = "document",
            document = new { id = mediaId, filename = fileName, caption = string.IsNullOrWhiteSpace(message.Caption) ? null : message.Caption },
        };
        return await SendMessageAsync(settings, secrets, payload, "document", summary, message.Reference, started, cancellationToken);
    }

    /// <summary>Meta accepts it only within 24 hours of the person's last message (error 131047 otherwise).</summary>
    public async Task<SendResult> SendTextAsync(WhatsAppTextMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (Validate(settings, secrets) is { } problem)
        {
            return await FailAsync("text", message.Reference, started, problem);
        }

        var summary = $"{{\"to\":\"{Phone.Mask(message.To)}\",\"type\":\"text\",\"length\":{message.Text.Length}}}";
        return await SendMessageAsync(settings, secrets, TextPayload(message.To, message.Text), "text", summary, message.Reference, started, cancellationToken);
    }

    internal static object TextPayload(string to, string text) => new
    {
        messaging_product = "whatsapp",
        recipient_type = "individual",
        to = to.TrimStart('+'),
        type = "text",
        text = new { preview_url = false, body = text },
    };

    /// <summary>Template message body. Meta rejects parameters containing newlines, tabs or long runs of spaces.</summary>
    internal static object TemplatePayload(string to, string templateName, string language, IReadOnlyList<string> parameters) => new
    {
        messaging_product = "whatsapp",
        recipient_type = "individual",
        to = to.TrimStart('+'),
        type = "template",
        template = new
        {
            name = templateName,
            language = new { code = language },
            components = parameters.Count == 0
                ? null
                : new[] { new { type = "body", parameters = parameters.Select(p => new { type = "text", text = SanitizeParameter(p) }).ToArray() } },
        },
    };

    internal static string SanitizeParameter(string value) =>
        IntegrationTrace.Truncate(Whitespace().Replace(value, " ").Trim(), 1024) ?? string.Empty;

    /// <summary>Meta returns <c>{"messages":[{"id":"wamid…"}]}</c> or <c>{"error":{"message","code","error_data":{"details"}}}</c>.</summary>
    internal static (bool Success, string? MessageId, string? Error) ParseResponse(bool httpSuccess, string body)
    {
        if (!IntegrationHttp.TryParseJson(body, out var document))
        {
            return (false, null, "WhatsApp returned an unreadable response.");
        }

        using (document)
        {
            var root = document.RootElement;
            var id = IntegrationHttp.ReadPath(root, "messages.0.id");
            if (httpSuccess && !string.IsNullOrEmpty(id))
            {
                return (true, id, null);
            }

            var code = IntegrationHttp.ReadPath(root, "error.code");
            var text = IntegrationHttp.ReadPath(root, "error.error_data.details") ?? IntegrationHttp.ReadPath(root, "error.message") ?? "request failed";
            return (false, null, code is null ? $"WhatsApp: {text}" : $"WhatsApp error {code}: {text}");
        }
    }

    private async Task<SendResult> SendMessageAsync(
        WhatsAppSettings settings, WhatsAppSecrets secrets, object payload, string operation, string summary, string? reference, long started, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings, "messages")) { Content = IntegrationHttp.JsonBody(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.AccessToken);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (success, id, error) = ParseResponse(response.IsSuccessStatusCode, body);
            await trace.LogAsync(IntegrationNames.WhatsApp, Key, operation, success, started, reference, summary,
                $"HTTP {(int)response.StatusCode} id={id}", error);
            return success ? SendResult.Ok(id) : SendResult.Fail(error!);
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            return await FailAsync(operation, reference, started, IntegrationErrors.Describe(ex, "WhatsApp"), summary);
        }
    }

    private async Task<string?> UploadAsync(WhatsAppSettings settings, WhatsAppSecrets secrets, WhatsAppDocumentMessage message, string fileName, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("whatsapp"), "messaging_product");
        form.Add(new StringContent(message.ContentType), "type");
        var file = new ByteArrayContent(message.Content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(message.ContentType);
        form.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings, "media")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || !IntegrationHttp.TryParseJson(body, out var document))
        {
            return null;
        }

        using (document)
        {
            return IntegrationHttp.Property(document.RootElement, "id");
        }
    }

    private static string? Validate(WhatsAppSettings settings, WhatsAppSecrets secrets)
    {
        if (string.IsNullOrWhiteSpace(secrets.AccessToken))
        {
            return "The WhatsApp access token is not configured.";
        }

        if (string.IsNullOrWhiteSpace(settings.PhoneNumberId) || !PhoneNumberIdPattern().IsMatch(settings.PhoneNumberId))
        {
            return "The WhatsApp phone number id is not configured.";
        }

        if (!ApiVersionPattern().IsMatch(settings.ApiVersion))
        {
            return "The WhatsApp API version must look like v21.0.";
        }

        return IntegrationHttp.TryCreateEndpoint(settings.GraphBaseUrl, requireHttps: true, out _) ? null : "The WhatsApp Graph URL must be an https address.";
    }

    private static Uri Endpoint(WhatsAppSettings settings, string edge)
    {
        IntegrationHttp.TryCreateEndpoint(settings.GraphBaseUrl, requireHttps: true, out var baseUri);
        return IntegrationHttp.Combine(baseUri!, $"{settings.ApiVersion}/{settings.PhoneNumberId}/{edge}");
    }

    private static string SafeFileName(string name)
    {
        var cleaned = new string(Path.GetFileName(name).Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ').ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "document.pdf" : IntegrationTrace.Truncate(cleaned, 120)!;
    }

    private async Task<SendResult> FailAsync(string operation, string? reference, long started, string error, string? summary = null)
    {
        await trace.LogAsync(IntegrationNames.WhatsApp, Key, operation, false, started, reference, summary, error: error);
        return SendResult.Fail(error);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\d{5,20}$")]
    private static partial Regex PhoneNumberIdPattern();

    [GeneratedRegex(@"^v\d{1,3}\.\d{1,2}$")]
    private static partial Regex ApiVersionPattern();
}
