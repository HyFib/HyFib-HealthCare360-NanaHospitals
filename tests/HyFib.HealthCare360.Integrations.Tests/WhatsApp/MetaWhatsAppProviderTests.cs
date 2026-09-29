using System.Net;
using System.Text;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Integrations.WhatsApp;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.WhatsApp;

public sealed class MetaWhatsAppProviderTests
{
    private static readonly WhatsAppSettings Settings = new()
    {
        Enabled = true,
        Provider = "meta",
        PhoneNumberId = "106540352242922",
        LanguageCodes = new Dictionary<string, string> { ["en"] = "en_US" },
    };

    private static readonly WhatsAppSecrets Secrets = new() { AccessToken = "EAAG-test-token" };

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MetaWhatsAppProvider Provider => new(_http.Client(), _tracing.Trace);

    [Fact]
    public async Task Sends_a_template_message_with_sanitised_body_parameters()
    {
        _http.Respond(HttpStatusCode.OK, """{"messaging_product":"whatsapp","contacts":[{"input":"919876543210","wa_id":"919876543210"}],"messages":[{"id":"wamid.HBgLOTE5ODc2NTQzMjEwFQIAERgS"}]}""");

        var result = await Provider.SendTemplateAsync(
            new WhatsAppTemplateMessage("+919876543210", "appointment_confirmed", "en", ["Dear Ravi,\nyour appointment\tis confirmed    at 10:30"], "log-9"),
            Settings, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("wamid.HBgLOTE5ODc2NTQzMjEwFQIAERgS"));
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe(new Uri("https://graph.facebook.com/v21.0/106540352242922/messages"));
        request.Header("Authorization").ShouldBe("Bearer EAAG-test-token");
        var body = request.Json();
        body.GetProperty("messaging_product").GetString().ShouldBe("whatsapp");
        body.GetProperty("to").GetString().ShouldBe("919876543210");
        var template = body.GetProperty("template");
        template.GetProperty("name").GetString().ShouldBe("appointment_confirmed");
        template.GetProperty("language").GetProperty("code").GetString().ShouldBe("en_US");
        var parameter = template.GetProperty("components")[0].GetProperty("parameters")[0];
        parameter.GetProperty("type").GetString().ShouldBe("text");
        parameter.GetProperty("text").GetString().ShouldBe("Dear Ravi, your appointment is confirmed at 10:30");
        _tracing.AllText.ShouldNotContain("Ravi");
    }

    [Fact]
    public async Task Templates_without_parameters_send_no_components()
    {
        _http.Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.X"}]}""");

        await Provider.SendTemplateAsync(new WhatsAppTemplateMessage("+919876543210", "hello_world", "en_US", []), Settings, Secrets, Ct);

        _http.Requests[0].Json().GetProperty("template").TryGetProperty("components", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Uploads_a_document_then_sends_it_by_media_id()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"1037543291543636"}""")
            .Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.DOC"}]}""");

        var result = await Provider.SendDocumentAsync(
            new WhatsAppDocumentMessage("+919876543210", "../Lab Report #42.pdf", Encoding.ASCII.GetBytes("%PDF-1.7"), "application/pdf", "Your lab report"),
            Settings, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("wamid.DOC"));
        _http.Requests.Count.ShouldBe(2);
        _http.Requests[0].Uri.ShouldBe(new Uri("https://graph.facebook.com/v21.0/106540352242922/media"));
        _http.Requests[0].ContentType.ShouldBe("multipart/form-data");
        _http.Requests[0].Body!.ShouldContain("whatsapp");
        var document = _http.Requests[1].Json().GetProperty("document");
        document.GetProperty("id").GetString().ShouldBe("1037543291543636");
        document.GetProperty("filename").GetString().ShouldBe("Lab Report 42.pdf");
        document.GetProperty("caption").GetString().ShouldBe("Your lab report");
    }

    [Fact]
    public async Task Sends_a_free_form_text_reply_and_logs_only_its_length()
    {
        _http.Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.TXT"}]}""");

        var result = await Provider.SendTextAsync(
            new WhatsAppTextMessage("+919876543210", "You'll get WhatsApp messages from Nana Hospitals again.", "reply-1"), Settings, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("wamid.TXT"));
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe(new Uri("https://graph.facebook.com/v21.0/106540352242922/messages"));
        var body = request.Json();
        body.GetProperty("to").GetString().ShouldBe("919876543210");
        body.GetProperty("type").GetString().ShouldBe("text");
        body.GetProperty("text").GetProperty("body").GetString().ShouldBe("You'll get WhatsApp messages from Nana Hospitals again.");
        body.GetProperty("text").GetProperty("preview_url").GetBoolean().ShouldBeFalse();
        _tracing.AllText.ShouldNotContain("Nana Hospitals");
    }

    [Fact]
    public async Task Refuses_unsupported_document_types_without_calling_meta()
    {
        var result = await Provider.SendDocumentAsync(
            new WhatsAppDocumentMessage("+919876543210", "run.exe", [0x4D, 0x5A], "application/x-msdownload"), Settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reports_meta_error_details()
    {
        _http.Respond(HttpStatusCode.BadRequest, """{"error":{"message":"(#132001) Template name does not exist in the translation","type":"OAuthException","code":132001,"error_data":{"messaging_product":"whatsapp","details":"template name (appointment_x) does not exist in en_US"}}}""");

        var result = await Provider.SendTemplateAsync(new WhatsAppTemplateMessage("+919876543210", "appointment_x", "en", []), Settings, Secrets, Ct);

        result.Error.ShouldBe("WhatsApp error 132001: template name (appointment_x) does not exist in en_US");
    }

    [Fact]
    public async Task Requires_a_numeric_phone_number_id_and_access_token()
    {
        var result = await Provider.SendTemplateAsync(new WhatsAppTemplateMessage("+919876543210", "hello_world", "en", []),
            Settings with { PhoneNumberId = "../../me" }, Secrets, Ct);

        result.Success.ShouldBeFalse();
        (await Provider.SendTemplateAsync(new WhatsAppTemplateMessage("+919876543210", "hello_world", "en", []), Settings, new WhatsAppSecrets(), Ct))
            .Success.ShouldBeFalse();
        _http.Requests.ShouldBeEmpty();
    }
}
