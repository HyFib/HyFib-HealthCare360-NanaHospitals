using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>
/// Meta WhatsApp Cloud API: approved template messages with body parameters (and, for templates approved with one, a
/// document header such as a report's PDF), documents (reports, bills) uploaded to <c>/{phone-number-id}/media</c>
/// first and then sent by media id, and free-form text replies. Parameter values, texts and documents can carry PHI,
/// so only the template name, parameter count, text length and file size are logged.
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
        var summary = $"{{\"to\":\"{Phone.Mask(message.To)}\",\"template\":\"{message.TemplateName}\",\"language\":\"{language}\",\"parameters\":{message.BodyParameters.Count}"
            + (message.HeaderDocument is { } attached ? $",\"document\":{attached.Content.Length}}}" : "}");
        HeaderMedia? header = null;
        if (message.HeaderDocument is { } document)
        {
            if (CheckDocument(document.ContentType, document.Content.Length, settings) is { } refused)
            {
                return await FailAsync("template", message.Reference, started, refused, summary);
            }

            var fileName = SafeFileName(document.FileName);
            string? mediaId;
            try
            {
                mediaId = await UploadAsync(settings, secrets, document.Content, document.ContentType, fileName, cancellationToken);
            }
            catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
            {
                return await FailAsync("template", message.Reference, started, "Upload failed: " + IntegrationErrors.Describe(ex, "WhatsApp"), summary);
            }

            if (mediaId is null)
            {
                return await FailAsync("template", message.Reference, started, "WhatsApp did not accept the document upload.", summary);
            }

            header = new HeaderMedia(mediaId, fileName);
        }

        var payload = TemplatePayload(message.To, message.TemplateName, language, message.BodyParameters, header);
        return await SendMessageAsync(settings, secrets, payload, "template", summary, message.Reference, started, cancellationToken);
    }

    public async Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (Validate(settings, secrets) is { } problem)
        {
            return await FailAsync("document", message.Reference, started, problem);
        }

        if (CheckDocument(message.ContentType, message.Content.Length, settings) is { } refused)
        {
            return await FailAsync("document", message.Reference, started, refused);
        }

        var fileName = SafeFileName(message.FileName);
        var summary = $"{{\"to\":\"{Phone.Mask(message.To)}\",\"file\":\"{fileName}\",\"bytes\":{message.Content.Length}}}";
        string? mediaId;
        try
        {
            mediaId = await UploadAsync(settings, secrets, message.Content, message.ContentType, fileName, cancellationToken);
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

    /// <summary>
    /// Template message: an optional document header (an uploaded file, by media id) and the body parameters. Meta
    /// rejects parameters containing newlines, tabs or long runs of spaces.
    /// </summary>
    internal static object TemplatePayload(string to, string templateName, string language, IReadOnlyList<string> parameters, HeaderMedia? header = null)
    {
        var components = new List<object>();
        if (header is not null)
        {
            components.Add(new { type = "header", parameters = new[] { new { type = "document", document = new { id = header.MediaId, filename = header.FileName } } } });
        }

        if (parameters.Count > 0)
        {
            components.Add(new { type = "body", parameters = parameters.Select(p => new { type = "text", text = SanitizeParameter(p) }).ToArray() });
        }

        return new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = to.TrimStart('+'),
            type = "template",
            template = new { name = templateName, language = new { code = language }, components = components.Count == 0 ? null : components },
        };
    }

    /// <summary>A file already uploaded to WhatsApp, shown as a template's document header.</summary>
    internal sealed record HeaderMedia(string MediaId, string FileName);

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

    /// <summary>Why WhatsApp would refuse the document (its type or size), or null.</summary>
    private static string? CheckDocument(string contentType, int length, WhatsAppSettings settings) =>
        !DocumentTypes.Contains(contentType)
            ? $"WhatsApp cannot send documents of type {contentType}."
            : length > Math.Clamp(settings.MaxDocumentMegabytes, 1, 100) * 1024L * 1024L
                ? "The document is larger than the configured WhatsApp limit."
                : null;

    private async Task<string?> UploadAsync(
        WhatsAppSettings settings, WhatsAppSecrets secrets, byte[] content, string contentType, string fileName, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("whatsapp"), "messaging_product");
        form.Add(new StringContent(contentType), "type");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
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
