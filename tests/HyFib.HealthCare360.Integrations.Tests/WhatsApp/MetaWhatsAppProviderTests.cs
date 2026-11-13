using System.Net;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// A receipt rendered from "Dear {{name}}, we have received Rs.{{amount}} for {{billNumber}} (receipt
    /// {{receiptNumber}}). Quote {{billNumber}} for your claim.", with its placeholders as the renderer lists them.
    /// </summary>
    private static readonly WhatsAppTemplateMessage Receipt = new(
        "+919876543210", "bill_receipt", "en",
        ["Dear Asha\nRao, we have received Rs.1,200.00 for NHOB26000042 (receipt NHRC260000123). Quote NHOB26000042 for your claim."],
        "log-12",
        Placeholders:
        [
            new TemplatePlaceholder("name", "Asha\nRao"),
            new TemplatePlaceholder("amount", "1,200.00"),
            new TemplatePlaceholder("billNumber", "NHOB26000042"),
            new TemplatePlaceholder("receiptNumber", "NHRC260000123"),
            new TemplatePlaceholder("billNumber", "NHOB26000042"),
        ]);

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MetaWhatsAppProvider Provider => new(_http.Client(), _tracing.Trace);

    /// <summary>The parameters of the body component of the one template message sent.</summary>
    private JsonElement[] SentBodyParameters() =>
        [.. _http.Requests.ShouldHaveSingleItem().Json().GetProperty("template").GetProperty("components").EnumerateArray()
            .Single(component => component.GetProperty("type").GetString() == "body").GetProperty("parameters").EnumerateArray()];

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
    public async Task By_default_the_whole_text_is_the_one_parameter_though_the_message_lists_its_placeholders()
    {
        _http.Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.TEXT"}]}""");

        var result = await Provider.SendTemplateAsync(Receipt, Settings, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("wamid.TEXT"));
        var parameter = SentBodyParameters().ShouldHaveSingleItem();
        parameter.GetProperty("type").GetString().ShouldBe("text");
        parameter.GetProperty("text").GetString().ShouldBe(
            "Dear Asha Rao, we have received Rs.1,200.00 for NHOB26000042 (receipt NHRC260000123). Quote NHOB26000042 for your claim.");
        parameter.TryGetProperty("parameter_name", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Positional_parameters_are_the_placeholders_values_in_order_one_for_each_use()
    {
        _http.Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.POSITIONAL"}]}""");

        var result = await Provider.SendTemplateAsync(Receipt, Settings with { Parameters = "positional" }, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("wamid.POSITIONAL"));
        var parameters = SentBodyParameters();
        parameters.Select(p => p.GetProperty("text").GetString()).ShouldBe(["Asha Rao", "1,200.00", "NHOB26000042", "NHRC260000123", "NHOB26000042"]);
        parameters.Select(p => p.GetProperty("type").GetString()).ShouldAllBe(type => type == "text");
        parameters.Select(p => p.TryGetProperty("parameter_name", out _)).ShouldAllBe(named => !named);

        // The values are PHI: the integration log records how many there were.
        _tracing.AllText.ShouldContain("\"parameters\":5");
        _tracing.AllText.ShouldNotContain("Asha");
        _tracing.AllText.ShouldNotContain("NHOB26000042");
    }

    [Theory]
    [InlineData("positional")]
    [InlineData("named")]
    public async Task A_placeholder_without_a_value_goes_as_a_dash_because_whatsapp_refuses_an_empty_parameter(string mode)
    {
        _http.Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.EMPTY"}]}""");
        var message = Receipt with
        {
            Placeholders = [new TemplatePlaceholder("name", "Asha Rao"), new TemplatePlaceholder("ward", " \n "), new TemplatePlaceholder("bed", "")],
        };

        (await Provider.SendTemplateAsync(message, Settings with { Parameters = mode }, Secrets, Ct)).Success.ShouldBeTrue();

        SentBodyParameters().Select(p => p.GetProperty("text").GetString()).ShouldBe(["Asha Rao", "-", "-"]);
    }

    [Fact]
    public async Task Named_parameters_are_each_placeholder_once_in_lower_snake_case()
    {
        _http.Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.NAMED"}]}""");

        var result = await Provider.SendTemplateAsync(Receipt, Settings with { Parameters = "named" }, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("wamid.NAMED"));
        var parameters = SentBodyParameters();
        parameters.Select(p => p.GetProperty("parameter_name").GetString()).ShouldBe(["name", "amount", "bill_number", "receipt_number"]);
        parameters.Select(p => p.GetProperty("text").GetString()).ShouldBe(["Asha Rao", "1,200.00", "NHOB26000042", "NHRC260000123"]);
        parameters.Select(p => p.GetProperty("type").GetString()).ShouldAllBe(type => type == "text");
        _tracing.AllText.ShouldContain("\"parameters\":4");
        _tracing.AllText.ShouldNotContain("Asha");
    }

    [Theory]
    [InlineData("billNumber", "bill_number")]
    [InlineData("receiptNumber", "receipt_number")]
    [InlineData("name", "name")]
    [InlineData("patient.uhid", "patient_uhid")]
    [InlineData("UHIDNumber", "uhid_number")]
    public void A_named_parameter_is_its_placeholder_in_lower_snake_case_with_dots_as_underscores(string placeholder, string parameter) =>
        WhatsAppTemplateParameters.ParameterName(placeholder).ShouldBe(parameter);

    [Theory]
    [InlineData("positional")]
    [InlineData("named")]
    public async Task A_template_without_placeholders_sends_no_body_by_its_placeholders(string mode)
    {
        _http.Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.NONE"}]}""");

        var result = await Provider.SendTemplateAsync(
            new WhatsAppTemplateMessage("+919876543210", "clinic_closed", "en", ["The clinic is closed today."], Placeholders: []),
            Settings with { Parameters = mode }, Secrets, Ct);

        result.Success.ShouldBeTrue();
        _http.Requests.ShouldHaveSingleItem().Json().GetProperty("template").TryGetProperty("components", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_message_without_placeholders_goes_with_its_own_parameters_whatever_the_setting()
    {
        _http.Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.OWN"}]}""");

        await Provider.SendTemplateAsync(new WhatsAppTemplateMessage("+919876543210", "order_update", "en", ["NHOB26000042"]),
            Settings with { Parameters = "named" }, Secrets, Ct);

        var parameter = SentBodyParameters().ShouldHaveSingleItem();
        parameter.GetProperty("text").GetString().ShouldBe("NHOB26000042");
        parameter.TryGetProperty("parameter_name", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_parameters_setting_is_a_configuration_problem_and_nothing_is_sent()
    {
        var result = await Provider.SendTemplateAsync(Receipt, Settings with { Parameters = "numbered" }, Secrets, Ct);

        result.ShouldBe(SendResult.Fail("The WhatsApp template parameters must be text, positional or named."));
        _http.Requests.ShouldBeEmpty();
        _tracing.Entries.ShouldHaveSingleItem().Error.ShouldBe("The WhatsApp template parameters must be text, positional or named.");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Named ")]
    [InlineData("POSITIONAL")]
    [InlineData("Text")]
    public void The_parameters_setting_ignores_letter_case_and_spaces_and_is_text_when_empty(string mode) =>
        WhatsAppTemplateParameters.Problem(Settings with { Parameters = mode }).ShouldBeNull();

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
    public async Task A_template_with_a_document_header_uploads_the_file_and_sends_it_as_the_header()
    {
        _http.Respond(HttpStatusCode.OK, """{"id":"2290617331189463"}""")
            .Respond(HttpStatusCode.OK, """{"messages":[{"id":"wamid.REPORT"}]}""");

        var result = await Provider.SendTemplateAsync(
            new WhatsAppTemplateMessage("+919876543210", "lab_report_ready", "en", ["Dear Asha, your report NHLAB2600012 is ready."], "log-7", null,
                new WhatsAppHeaderDocument("lab-report-NHLAB2600012.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 Asha Rao"), "application/pdf")),
            Settings, Secrets, Ct);

        result.ShouldBe(SendResult.Ok("wamid.REPORT"));
        _http.Requests.Count.ShouldBe(2);
        _http.Requests[0].Uri.ShouldBe(new Uri("https://graph.facebook.com/v21.0/106540352242922/media"));
        var components = _http.Requests[1].Json().GetProperty("template").GetProperty("components");
        components.GetArrayLength().ShouldBe(2);
        components[0].GetProperty("type").GetString().ShouldBe("header");
        var document = components[0].GetProperty("parameters")[0];
        document.GetProperty("type").GetString().ShouldBe("document");
        document.GetProperty("document").GetProperty("id").GetString().ShouldBe("2290617331189463");
        document.GetProperty("document").GetProperty("filename").GetString().ShouldBe("lab-report-NHLAB2600012.pdf");
        components[1].GetProperty("type").GetString().ShouldBe("body");

        // The file and the message text are PHI: the integration log records sizes only.
        _tracing.AllText.ShouldNotContain("Asha");
        _tracing.AllText.ShouldContain("\"document\":17");
    }

    [Fact]
    public async Task A_header_document_whatsapp_would_refuse_is_not_uploaded()
    {
        var result = await Provider.SendTemplateAsync(
            new WhatsAppTemplateMessage("+919876543210", "lab_report_ready", "en", ["x"], null, null, new WhatsAppHeaderDocument("report.html", [0x3C], "text/html")),
            Settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("text/html");
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_upload_fails_the_message()
    {
        _http.Respond(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid parameter","code":100}}""");

        var result = await Provider.SendTemplateAsync(
            new WhatsAppTemplateMessage("+919876543210", "lab_report_ready", "en", ["x"], null, null, new WhatsAppHeaderDocument("r.pdf", [0x25], "application/pdf")),
            Settings, Secrets, Ct);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("did not accept the document");
        _http.Requests.ShouldHaveSingleItem();
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
