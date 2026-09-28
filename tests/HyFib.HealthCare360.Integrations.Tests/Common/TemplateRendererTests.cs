using System.Text.Json;
using HyFib.HealthCare360.Integrations.Common;

namespace HyFib.HealthCare360.Integrations.Tests.Common;

public sealed class TemplateRendererTests
{
    private static readonly Dictionary<string, string?> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["to"] = "+919876543210",
        ["text"] = "Dear \"Ravi\" & family, 50% off? Line1\nLine2",
        ["empty"] = null,
    };

    [Fact]
    public void Url_encoding_keeps_values_inside_their_query_parameter()
    {
        var url = TemplateRenderer.Render("https://sms.example.in/send?to={{to}}&msg={{ text }}&x={{missing}}", Values, TemplateEncoding.Url);

        url.ShouldBe("https://sms.example.in/send?to=%2B919876543210&msg=Dear%20%22Ravi%22%20%26%20family%2C%2050%25%20off%3F%20Line1%0ALine2&x=");
    }

    [Fact]
    public void Json_encoding_produces_valid_json_whatever_the_value_contains()
    {
        var body = TemplateRenderer.Render("{\"to\":\"{{to}}\",\"message\":\"{{text}}\",\"note\":\"{{empty}}\"}", Values, TemplateEncoding.Json);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("message").GetString().ShouldBe("Dear \"Ravi\" & family, 50% off? Line1\nLine2");
        document.RootElement.GetProperty("note").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public void Extracts_placeholder_values_from_rendered_text()
    {
        const string template = "Dear {{name}}, your appointment with {{doctor}} is confirmed for {{date}} at {{time}}. Ref {{reference}}. - {{hospital}}";
        const string rendered = "Dear Ravi Kumar, your appointment with Dr. A. Rao is confirmed for 26 Sep 2026 at 10:30 AM. Ref APT-0009. - Nana Hospitals";

        TemplateRenderer.TryExtract(template, rendered, out var values).ShouldBeTrue();

        values["name"].ShouldBe("Ravi Kumar");
        values["doctor"].ShouldBe("Dr. A. Rao");
        values["date"].ShouldBe("26 Sep 2026");
        values["time"].ShouldBe("10:30 AM");
        values["reference"].ShouldBe("APT-0009");
        values["hospital"].ShouldBe("Nana Hospitals");
    }

    [Fact]
    public void Repeated_placeholders_must_carry_the_same_value()
    {
        TemplateRenderer.TryExtract("{{code}} is your code. Repeat: {{code}}", "4521 is your code. Repeat: 4521", out var values).ShouldBeTrue();
        values["code"].ShouldBe("4521");

        TemplateRenderer.TryExtract("{{code}} is your code. Repeat: {{code}}", "4521 is your code. Repeat: 9999", out _).ShouldBeFalse();
    }

    [Fact]
    public void Text_that_does_not_follow_the_template_is_not_matched() =>
        TemplateRenderer.TryExtract("Payment of Rs.{{amount}} received for {{billNumber}}.", "Your report is ready.", out _).ShouldBeFalse();

    [Fact]
    public void Regex_metacharacters_in_the_template_are_literal() =>
        TemplateRenderer.TryExtract("Pay Rs.{{amount}} (incl. GST) [{{ref}}]?", "Pay Rs.1,200.00 (incl. GST) [B-77]?", out var values).ShouldBeTrue();
}
