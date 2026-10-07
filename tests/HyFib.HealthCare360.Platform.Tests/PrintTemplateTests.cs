using System.Text.Json;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Settings;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class PrintTemplateTests
{
    private static readonly PrintTemplateDefinition Reports = PrintTemplateCatalog.Find(PrintTemplateCatalog.Report)!;
    private static readonly PrintTemplateDefinition Prescriptions = PrintTemplateCatalog.Find(PrintTemplateCatalog.Prescription)!;
    private static readonly PrintTemplateDefinition CounterBills = PrintTemplateCatalog.Find(PrintTemplateCatalog.SalesInvoice)!;

    private static readonly PrintTemplate Stationery = new()
    {
        Letterhead = PrintLetterhead.Preprinted,
        TopSpaceMm = 35,
        BottomSpaceMm = 15,
        ShowLogo = false,
        ClosingNote = "Results are to be read with the clinical findings.\nThank you.",
    };

    [Fact]
    public void Each_template_has_its_own_key_and_a_name_staff_know()
    {
        PrintTemplateCatalog.All.Select(d => d.Key).ShouldBeUnique();
        PrintTemplateCatalog.All.ShouldAllBe(d => d.Name.Length > 0 && d.Covers.EndsWith('.'));
        PrintTemplateCatalog.Find("no-such-template").ShouldBeNull();
        PrintTemplateCatalog.SettingKey(PrintTemplateCatalog.Bill).ShouldBe("print.bill");
        Prescriptions.Papers.ShouldBe([DocumentPageSize.A5, DocumentPageSize.A4]);
        CounterBills.AllowsPreprinted.ShouldBeFalse("80 mm rolls are never pre-printed");
    }

    [Fact]
    public void A_template_within_the_rules_may_be_saved()
    {
        PrintTemplateRules.Problems(Reports, Stationery).ShouldBeEmpty();
        PrintTemplateRules.Problems(Prescriptions, PrintTemplate.Default with { Paper = DocumentPageSize.A4 }).ShouldBeEmpty();
    }

    [Fact]
    public void A_template_outside_the_rules_is_refused_with_the_field_that_is_wrong()
    {
        Fields(Reports, Stationery with { TopSpaceMm = 81, BottomSpaceMm = -1 }).ShouldBe(["topSpaceMm", "bottomSpaceMm"]);
        Fields(Reports, Stationery with { ClosingNote = new string('x', PrintTemplate.MaxClosingNoteLength + 1) }).ShouldBe(["closingNote"]);
        Fields(Reports, Stationery with { ClosingNote = "Bell\u0007" }).ShouldBe(["closingNote"]);
        Fields(Reports, PrintTemplate.Default with { Paper = DocumentPageSize.A5 }).ShouldBe(["paper"]);
        Fields(Prescriptions, PrintTemplate.Default with { Letterhead = (PrintLetterhead)7 }).ShouldBe(["letterhead"]);
        Fields(CounterBills, PrintTemplate.Default with { Letterhead = PrintLetterhead.Preprinted }).ShouldBe(["letterhead"]);
    }

    [Fact]
    public void Documents_read_a_template_as_near_to_what_was_set_as_the_rules_allow()
    {
        var printed = PrintTemplateRules.Normalize(Reports, Stationery with { Letterhead = PrintLetterhead.Printed, ClosingNote = "  \n " });
        printed.TopSpaceMm.ShouldBe(0, "blank space is for pre-printed paper only");
        printed.BottomSpaceMm.ShouldBe(0);
        printed.ClosingNote.ShouldBeNull();

        var stretched = PrintTemplateRules.Normalize(Reports, Stationery with { TopSpaceMm = 500, ClosingNote = " Thank you.\u0007 " });
        stretched.TopSpaceMm.ShouldBe(PrintTemplate.MaxSpaceMm);
        stretched.ClosingNote.ShouldBe("Thank you.");

        PrintTemplateRules.Normalize(CounterBills, Stationery).Letterhead.ShouldBe(PrintLetterhead.Printed);
        PrintTemplateRules.Normalize(Reports, PrintTemplate.Default with { Paper = DocumentPageSize.A5 }).Paper.ShouldBeNull();
    }

    [Fact]
    public void A_template_is_stored_in_words_the_staff_app_reads()
    {
        var json = JsonSerializer.Serialize(Stationery with { Paper = DocumentPageSize.A4 }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.ShouldContain("\"letterhead\":\"Preprinted\"");
        json.ShouldContain("\"paper\":\"A4\"");
        json.ShouldContain("\"showLogo\":false");
        json.ShouldNotContain("printsLetterhead");
        JsonSerializer.Deserialize<PrintTemplate>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldBe(Stationery with { Paper = DocumentPageSize.A4 });
    }

    [Fact]
    public async Task The_facility_s_own_template_wins_and_a_value_that_is_not_a_template_prints_the_defaults()
    {
        var facility = Guid.NewGuid();
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<PrintTemplate>("print.report", facility, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PrintTemplate?>(Stationery with { TopSpaceMm = 999 }));
        settings.GetAsync<PrintTemplate>("print.report", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PrintTemplate?>(PrintTemplate.Default with { ShowLogo = false }));
        settings.GetAsync<PrintTemplate>("print.bill", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PrintTemplate?>(new JsonException("set by hand")));
        var templates = new PrintTemplates(settings);

        (await templates.GetAsync(PrintTemplateCatalog.Report, facility, CancellationToken.None)).TopSpaceMm.ShouldBe(PrintTemplate.MaxSpaceMm);
        (await templates.GetAsync(PrintTemplateCatalog.Report, null, CancellationToken.None)).ShowLogo.ShouldBeFalse();
        (await templates.GetAsync(PrintTemplateCatalog.Bill, facility, CancellationToken.None)).ShouldBe(PrintTemplate.Default);
        await Should.ThrowAsync<ArgumentException>(() => templates.GetAsync("letters", facility, CancellationToken.None));
    }

    [Fact]
    public void Pre_printed_stationery_moves_the_page_margins_and_the_closing_note_ends_the_document()
    {
        var html = new PrintableDocument
        {
            Title = "Lab report",
            FileName = "lab-report",
            Footer = "Asha Rao · UHID NH0001",
            Body = Html.Of($"<p>Results</p>"),
            Template = Stationery with { ClosingNote = "Read with the <clinical> findings.\nThank you." },
        }.ToHtml();

        html.ShouldContain("margin: 47mm 12mm 31mm;");
        html.ShouldContain("@bottom-left { content: \"Asha Rao · UHID NH0001\"; padding-bottom: 15mm;", Case.Sensitive, "above the stationery's footer");
        html.ShouldContain("counter(pages); padding-bottom: 15mm;");
        html.ShouldContain("<div class=\"closing-note\">Read with the &lt;clinical&gt; findings.<br>Thank you.</div>");
        html.IndexOf("closing-note\">", StringComparison.Ordinal).ShouldBeGreaterThan(html.IndexOf("<p>Results</p>", StringComparison.Ordinal));

        var plain = new PrintableDocument { Title = "Lab report", FileName = "lab-report", Body = Html.Of($"<p>Results</p>"), PageSize = DocumentPageSize.A5 }.ToHtml();
        plain.ShouldContain("margin: 10mm 10mm 14mm;");
        plain.ShouldNotContain("class=\"closing-note\"");
        plain.ShouldNotContain("padding-bottom");

        var printed = new PrintableDocument
        {
            Title = "Lab report",
            FileName = "lab-report",
            Body = Html.Of($"<p>Results</p>"),
            Template = Stationery with { Letterhead = PrintLetterhead.Printed },
        }.ToHtml();
        printed.ShouldContain("margin: 12mm 12mm 16mm;", Case.Sensitive, "blank space is for pre-printed paper only");
    }

    [Fact]
    public void On_pre_printed_stationery_the_letterhead_leaves_the_paper_s_own_and_keeps_the_doctor()
    {
        var letterhead = new DocumentLetterhead("Nana Hospitals", null, "12 MG Road, Pune", null, null, null, null, null, null, null, null, null);

        DocumentParts.Letterhead(letterhead).ToString().ShouldContain("Nana Hospitals");
        var preprinted = letterhead with { Template = Stationery };
        DocumentParts.Letterhead(preprinted).IsEmpty.ShouldBeTrue();
        var withDoctor = DocumentParts.Letterhead(preprinted, Html.Of($"<b>Dr Asha Rao</b>")).ToString();
        withDoctor.ShouldContain("Dr Asha Rao");
        withDoctor.ShouldNotContain("Nana Hospitals");
        withDoctor.ShouldNotContain("MG Road");
    }

    private static string[] Fields(PrintTemplateDefinition definition, PrintTemplate template) =>
        PrintTemplateRules.Problems(definition, template).Select(p => p.Field).ToArray();
}
