using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class DocumentTests
{
    private const string Hostile = "<script>alert('x')</script> & \"quoted\"";

    [Fact]
    public void Values_are_encoded_and_template_text_is_kept()
    {
        var html = Html.Of($"<td class=\"name\">{Hostile}</td>").ToString();

        html.ShouldStartWith("<td class=\"name\">");
        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
        html.ShouldContain("&amp;");
        html.ShouldNotContain("\"quoted\"");
    }

    [Fact]
    public void Html_values_and_lists_of_them_go_in_as_they_are()
    {
        var rows = new[] { "a", "<b>" }.Select(v => Html.Of($"<li>{v}</li>")).ToList();

        Html.Of($"<ul>{rows}</ul>").ToString().ShouldBe("<ul><li>a</li><li>&lt;b&gt;</li></ul>");
        Html.Of($"<ul>{rows.AsEnumerable()}</ul>").ToString().ShouldBe("<ul><li>a</li><li>&lt;b&gt;</li></ul>");
    }

    [Fact]
    public void Numbers_use_indian_grouping_and_hindi_stays_readable()
    {
        Html.Of($"{123456.5m:N2}").ToString().ShouldBe("1,23,456.50");
        Html.Text("आशा राव").ToString().ShouldBe("आशा राव");
    }

    [Fact]
    public void Text_keeps_its_lines_and_paragraphs()
    {
        Html.Lines("First line\r\nSecond <line>").ToString().ShouldBe("First line<br>Second &lt;line&gt;");
        Html.Paragraphs("One\nstill one\n\nTwo").ToString().ShouldBe("<p>One<br>still one</p><p>Two</p>");
        Html.Paragraphs("   ").IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Only_image_data_uris_can_be_embedded()
    {
        ImageData.FromDataUri("data:image/png;base64,iVBORw0KGgo=").ShouldNotBeNull();
        ImageData.FromDataUri("javascript:alert(1)").ShouldBeNull();
        ImageData.FromDataUri("data:text/html;base64,PHNjcmlwdD4=").ShouldBeNull();
        ImageData.FromDataUri("data:image/png;base64,iVBOR\"onerror=\"x").ShouldBeNull();
        ImageData.FromBytes([1, 2, 3], "application/pdf").ShouldBeNull();

        var image = Html.Image(ImageData.FromBytes([1, 2, 3], "image/png"), Hostile, "logo").ToString();
        image.ShouldStartWith("<img class=\"logo\" src=\"data:image/png;base64,AQID\"");
        image.ShouldNotContain("<script>");
    }

    [Fact]
    public void A_document_forbids_scripts_and_network_access_and_sets_its_page()
    {
        var html = new PrintableDocument { Title = "Lab report <1>", FileName = "lab", Body = Html.Of($"<p>{Hostile}</p>"), PageSize = DocumentPageSize.A5, Landscape = true }.ToHtml();

        html.ShouldStartWith("<!doctype html>");
        html.ShouldContain("content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:\"");
        html.ShouldContain("<title>Lab report &lt;1&gt;</title>");
        html.ShouldContain("size: A5 landscape");
        html.ShouldContain("counter(page)");
        html.ShouldNotContain("<script>");
    }

    [Fact]
    public void The_running_footer_cannot_break_out_of_its_css_string()
    {
        var html = new PrintableDocument { Title = "x", FileName = "x", Body = Html.Empty, Footer = "Asha \"R\" Rao </style><script>x</script>\\" }.ToHtml();

        html.ShouldContain("@bottom-left { content: \"Asha \\22 R\\22  Rao \\3C /style\\3E \\3C script\\3E x\\3C /script\\3E \\5C \"");
        html.Split("</style>").Length.ShouldBe(2);
    }

    [Theory]
    [InlineData("lab-report-NHLAB2600012", "lab-report-NHLAB2600012")]
    [InlineData("../../windows/system32", "windows-system32")]
    [InlineData("bill NH/26/001 \"copy\"", "bill-NH-26-001-copy")]
    [InlineData("", "document")]
    public void Download_names_are_safe(string name, string expected) =>
        new PrintableDocument { Title = "x", FileName = name, Body = Html.Empty }.FileName.ShouldBe(expected);

    [Fact]
    public void Dates_and_money_print_in_ist_and_indian_style()
    {
        var instant = new DateTimeOffset(2026, 9, 25, 10, 15, 0, TimeSpan.Zero);

        DocumentFormat.Date(instant).ShouldBe("25 Sep 2026");
        DocumentFormat.Time(instant).ShouldBe("3:45 pm");
        DocumentFormat.DateTime(instant).ShouldBe("25 Sep 2026, 3:45 pm");
        DocumentFormat.DateTime(new DateTimeOffset(2026, 9, 24, 18, 45, 0, TimeSpan.Zero)).ShouldBe("25 Sep 2026, 12:15 am");
        DocumentFormat.Money(123456.5m).ShouldBe("₹1,23,456.50");
        DocumentFormat.Money(-20m).ShouldBe("-₹20.00");
        DocumentFormat.Date((DateTimeOffset?)null).ShouldBe("—");
    }

    [Fact]
    public void The_letterhead_and_fields_encode_what_they_print()
    {
        var letterhead = new DocumentLetterhead(Hostile, null, "12 MG Road, Pune", "020 1234", "lab@nana.in", null, null, "NABH-123", null, null, null, null);

        var head = DocumentParts.Letterhead(letterhead).ToString();
        head.ShouldNotContain("<script>");
        head.ShouldContain("Phone 020 1234 · lab@nana.in");
        head.ShouldContain("NABH accreditation NABH-123");

        var fields = DocumentParts.Fields([("Patient", Hostile), ("Referred by", null)]).ToString();
        fields.ShouldNotContain("<script>");
        fields.ShouldContain(">—<");
    }

    [Fact]
    public void The_verification_block_carries_a_qr_image_of_the_link()
    {
        var block = DocumentParts.Verification("https://portal.nana.in/portal/verify/lab/ABCD", "ABCD").ToString();

        block.ShouldContain("src=\"data:image/svg+xml;base64,");
        block.ShouldContain("https://portal.nana.in/portal/verify/lab/ABCD");
        block.ShouldContain("Verification code ABCD");
    }
}
