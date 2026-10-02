using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.Reports;
using HyFib.HealthCare360.Modules.Analytics.Features.Schedules;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Reports.Export;
using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Reports;

/// <summary>The PDF export of reports: layout, number formats, orientation, the row limit and the safety of its HTML.</summary>
public sealed class ReportPdfTests
{
    private const string Hostile = "<script>alert('x')</script> & \"quoted\"";

    private static readonly Guid FacilityId = Guid.Parse("0197f5a2-0000-7000-8000-000000000001");

    private static readonly DocumentLetterhead Facility = new(
        "Nana Hospitals — Main", "Nana Hospitals Pvt Ltd", "12 MG Road, Pune", "020 4000 1000", "info@nana.in", null, null, "H-2021-0456", null, null,
        "24 x 7 emergency", null);

    private static readonly ReportColumn[] Columns =
    [
        new("department", "Department", ReportValueType.Text),
        new("visits", "Visits", ReportValueType.Integer),
        new("revenue", "Revenue", ReportValueType.Money),
        new("share", "Share %", ReportValueType.Percent),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_table_has_its_headings_and_right_aligned_numbers_in_the_excel_formats_with_indian_grouping()
    {
        var html = Build(Document()).ToHtml();

        html.ShouldContain("<thead><tr><th>Department</th><th class=\"num\">Visits</th><th class=\"num\">Revenue</th><th class=\"num\">Share %</th></tr></thead>");
        html.ShouldContain("<tr><td>Cardiology</td><td class=\"num\">1,23,456</td><td class=\"num\">₹55,000.50</td><td class=\"num\">30.1%</td></tr>");
        html.ShouldContain("<tr><td>Refunds</td><td class=\"num\">5</td><td class=\"num neg\">-₹1,200.00</td><td class=\"num\"></td></tr>");

        // The house style repeats the headings at the top of every page.
        html.ShouldContain("thead { display: table-header-group; }");
        html.ShouldContain(".grid .num { text-align: right;");
    }

    [Fact]
    public void Values_are_printed_as_in_the_excel_export_and_dates_as_on_the_hospitals_documents()
    {
        ReportPdf.Format(12_34_567, ReportValueType.Integer).ShouldBe("12,34,567");
        ReportPdf.Format(1_234.56m, ReportValueType.Decimal).ShouldBe("1,234.6");
        ReportPdf.Format(4.26, ReportValueType.Days).ShouldBe("4.3");
        ReportPdf.Format(12L, ReportValueType.Minutes).ShouldBe("12.0");
        ReportPdf.Format(-50m, ReportValueType.Money).ShouldBe("-₹50.00");
        ReportPdf.Format(1_23_45_678.9m, ReportValueType.Money).ShouldBe("₹1,23,45,678.90");
        ReportPdf.Format(7.26m, ReportValueType.Percent).ShouldBe("7.3%");
        ReportPdf.Format(1_250m, ReportValueType.Percent).ShouldBe("1250.0%");
        ReportPdf.Format(new DateOnly(2026, 9, 5), ReportValueType.Date).ShouldBe("05 Sep 2026");
        ReportPdf.Format(new DateOnly(2026, 9, 1), ReportValueType.Month).ShouldBe("Sep 2026");
        ReportPdf.Format(new DateTimeOffset(2026, 9, 15, 10, 15, 0, TimeSpan.Zero), ReportValueType.DateTime).ShouldBe("15 Sep 2026, 3:45 pm");
        ReportPdf.Format(true, ReportValueType.Text).ShouldBe("Yes");
        ReportPdf.Format("Ward 3", ReportValueType.Text).ShouldBe("Ward 3");
        ReportPdf.Format(null, ReportValueType.Money).ShouldBeNull();
    }

    [Fact]
    public void The_totals_row_closes_the_table_once_and_is_not_repeated_as_page_totals()
    {
        var html = Build(Document()).ToHtml();

        html.ShouldContain("<tbody class=\"totals\"><tr><td>Total</td><td class=\"num\">1,23,861</td><td class=\"num\">₹53,900.50</td><td class=\"num\"></td></tr></tbody>");
        html.ShouldNotContain("<tfoot");
    }

    [Fact]
    public void The_document_names_the_hospital_the_inputs_who_made_it_when_and_from_which_data()
    {
        var document = Build(Document());
        var html = document.ToHtml();

        document.Title.ShouldBe("Revenue by department");
        document.FileName.ShouldBe("revenue-by-department_20260901-20260915");
        document.Footer.ShouldBe("Revenue by department · Nana Hospitals · generated 15 Sep 2026, 12:00 pm · Confidential");
        html.ShouldContain("<div class=\"facility\">Nana Hospitals</div>");
        html.ShouldContain("12 MG Road, Pune");
        html.ShouldContain("NABH accreditation H-2021-0456");
        html.ShouldContain("<h1 class=\"doc-title\">Revenue by department</h1>");
        html.ShouldContain("<span class=\"label\">Period</span><span class=\"value\">01 Sep 2026 – 15 Sep 2026 (Last month)</span>");
        html.ShouldContain("<span class=\"label\">Facility</span><span class=\"value\">Nana Hospitals — Main</span>");
        html.ShouldContain("<span class=\"label\">Generated by</span><span class=\"value\">Asha Rao (asha)</span>");
        html.ShouldContain("<span class=\"label\">Generated on</span><span class=\"value\">15 Sep 2026, 12:00 pm</span>");
        html.ShouldContain("<span class=\"label\">Data source</span><span class=\"value\">Live database</span>");
        html.ShouldContain("<div class=\"figure-value\">₹1,57,100.50</div><div class=\"figure-label\">Net revenue</div>");
        html.ShouldContain("<section class=\"notes\"><h2>Notes</h2><p>A note.</p></section>");
        html.ShouldContain("<h2>Definition</h2><p>Revenue = net amount of finalized bill lines.</p>");
        html.ShouldContain("Do not share outside the hospital.");
        html.ShouldContain("— End of report —");
        html.ShouldNotContain("class=\"banner\"");
    }

    [Fact]
    public void A_patient_list_keeps_the_excel_exports_confidentiality_rules()
    {
        var document = Build(Document() with { ContainsPatientIdentifiers = true });

        document.Footer!.ShouldEndWith(" · Confidential");
        document.ToHtml().ShouldContain("Contains patient-identifiable information: handle under the hospital");
    }

    [Fact]
    public void A_consolidated_report_is_headed_by_the_organisation_and_its_logo_only()
    {
        var logo = ImageData.FromBytes([1, 2, 3], "image/png");
        var letterhead = ReportPdf.Letterhead(Facility with { Logo = logo }, Document() with { FacilityId = null, Organisation = "Nana Hospitals Group" });

        letterhead.Name.ShouldBe("Nana Hospitals Group");
        letterhead.Address.ShouldBeNull();
        letterhead.Phone.ShouldBeNull();
        letterhead.NabhNumber.ShouldBeNull();
        letterhead.Note.ShouldBeNull();
        letterhead.Logo.ShouldBe(logo);
    }

    [Fact]
    public void Narrow_tables_print_portrait_and_wide_or_many_columned_ones_landscape()
    {
        Build(Document()).Landscape.ShouldBeFalse();

        var integers = Enumerable.Range(1, 10).Select(i => new ReportColumn($"c{i}", $"C{i}", ReportValueType.Integer)).ToArray();
        Build(Table(integers, [.. Enumerable.Range(1, 10).Select(i => (object?)i)])).Landscape.ShouldBeTrue();
        Build(Table(integers[..ReportPdf.MaxPortraitColumns], [.. Enumerable.Range(1, ReportPdf.MaxPortraitColumns).Select(i => (object?)i)]))
            .Landscape.ShouldBeFalse();

        // Six money columns in crores (16 characters each) and a payer name are wider than a portrait line.
        ReportColumn[] amounts = [new("payer", "Payer", ReportValueType.Text), .. Enumerable.Range(1, 6).Select(i => new ReportColumn($"m{i}", $"Due {i}", ReportValueType.Money))];
        Build(Table(amounts, ["Star Health Insurance", .. Enumerable.Repeat((object?)12_34_56_789m, 6)])).Landscape.ShouldBeTrue();

        // Text wraps, so a long text counts only up to 40 characters: two long columns fit, three do not.
        var text = new string('x', 100);
        ReportColumn[] notes = [new("a", "A", ReportValueType.Text), new("b", "B", ReportValueType.Text), new("c", "C", ReportValueType.Text)];
        Build(Table(notes[..2], [text, text])).Landscape.ShouldBeFalse();
        Build(Table(notes, [text, text, text])).Landscape.ShouldBeTrue();
    }

    [Fact]
    public void A_long_report_lists_the_first_rows_and_says_the_excel_export_has_every_row()
    {
        var html = Build(Document() with { Rows = ManyRows(ReportPdf.MaxRows + 1) }).ToHtml();

        html.ShouldContain($"<td>D{ReportPdf.MaxRows}</td>");
        html.ShouldNotContain($"<td>D{ReportPdf.MaxRows + 1}</td>");
        html.Split("<tr><td>D").Length.ShouldBe(ReportPdf.MaxRows + 1);
        html.ShouldContain("<div class=\"banner\">The PDF lists the first 2,000 of 2,001 rows; the Excel export has every row. The totals are for all rows.</div>");
        html.ShouldContain("<h2>Notes</h2><p>The PDF lists the first 2,000 of 2,001 rows; the Excel export has every row.</p><p>A note.</p>");
        ReportPdf.Notice(ReportPdf.MaxRows).ShouldBeNull();
        ReportPdf.Notice(10_000).ShouldBe("The PDF lists the first 2,000 of 10,000 rows; the Excel export has every row.");
    }

    [Fact]
    public void A_report_without_rows_says_so_instead_of_printing_an_empty_table()
    {
        var html = Build(Document() with { Rows = [], Totals = null }).ToHtml();

        html.ShouldContain("<p class=\"empty\">No data for these inputs.</p>");
        html.ShouldNotContain("<table class=\"grid\">");
    }

    [Fact]
    public void Text_from_the_report_cannot_become_markup()
    {
        var document = Document() with
        {
            ReportId = "x/../<y>",
            Title = Hostile,
            Organisation = Hostile,
            Definition = Hostile,
            GeneratedBy = Hostile,
            DataSource = Hostile,
            Filters = [new ReportFilterDto(Hostile, Hostile)],
            Columns = [new ReportColumn("department", Hostile, ReportValueType.Text), .. Columns[1..]],
            Rows = [[Hostile, 1, 1m, 1m]],
            Totals = [Hostile, 1, 1m, 1m],
            Summary = [new ReportFigure("f", Hostile, Hostile, ReportValueType.Text)],
            Notes = [Hostile],
        };

        var pdf = Build(document);
        var html = pdf.ToHtml();

        html.ShouldNotContain("<script>");
        html.ShouldContain("<td>&lt;script&gt;alert(&#x27;x&#x27;)&lt;/script&gt; &amp; &quot;quoted&quot;</td>");
        html.ShouldContain("<title>&lt;script&gt;");
        html.Split("</style>").Length.ShouldBe(2);
        pdf.FileName.ShouldMatch("^[A-Za-z0-9_-]+$");
    }

    [Theory]
    [InlineData(null, ExportFormat.Xlsx)]
    [InlineData("", ExportFormat.Xlsx)]
    [InlineData("xlsx", ExportFormat.Xlsx)]
    [InlineData("Excel", ExportFormat.Xlsx)]
    [InlineData("csv", ExportFormat.Csv)]
    [InlineData("pdf", ExportFormat.Pdf)]
    [InlineData(" PDF ", ExportFormat.Pdf)]
    public void Export_formats_are_read_from_the_query(string? value, ExportFormat expected)
    {
        ReportEndpoints.TryFormat(value, out var format).ShouldBeTrue();
        format.ShouldBe(expected);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("pdfa")]
    [InlineData("2")]
    public void Other_formats_are_refused(string value) => ReportEndpoints.TryFormat(value, out _).ShouldBeFalse();

    [Fact]
    public void Pdf_comes_after_the_formats_already_stored()
    {
        Enum.GetNames<ExportFormat>().ShouldBe(["Xlsx", "Csv", "Pdf"]);
        ((int)ExportFormat.Pdf).ShouldBe(2);
    }

    [Fact]
    public void Schedules_can_send_pdfs_and_an_unknown_format_is_refused_in_words()
    {
        var validator = new SaveScheduleRequestValidator();
        var request = new SaveScheduleRequest("Daily revenue", "daily-revenue", null, ExportFormat.Pdf, ScheduleFrequency.Daily, "08:00", null, null, ["md@nana.in"]);

        validator.Validate(request).IsValid.ShouldBeTrue();
        validator.Validate(request with { Format = (ExportFormat)9 }).Errors.Select(e => e.ErrorMessage).ShouldContain("Choose Excel (xlsx), CSV or PDF.");
    }

    [Fact]
    public async Task The_pdf_is_named_like_the_excel_file_and_counts_the_rows_it_lists()
    {
        var renderer = Substitute.For<IPdfRenderer>();
        PdfSource? printed = null;
        renderer.RenderAsync(Arg.Do<PdfSource>(source => printed = source), Arg.Any<CancellationToken>()).Returns(new byte[] { 37, 80, 68, 70 });
        var document = Document() with { Rows = ManyRows(2_500) };

        var file = await ReportFileWriter.PdfAsync(document, ReportPdf.Letterhead(Facility, document), renderer, Ct);

        file.FileName.ShouldBe("revenue-by-department_20260901-20260915.pdf");
        file.ContentType.ShouldBe("application/pdf");
        file.Content.ShouldBe(new byte[] { 37, 80, 68, 70 });
        file.Rows.ShouldBe(ReportPdf.MaxRows);
        file.Notice.ShouldBe("The PDF lists the first 2,000 of 2,500 rows; the Excel export has every row.");
        printed.ShouldNotBeNull().Html.ShouldStartWith("<!doctype html>");
    }

    [Fact]
    public async Task Excel_and_csv_are_written_as_before_without_the_pdf_renderer()
    {
        var renderer = Substitute.For<IPdfRenderer>();
        var writer = new ReportFileWriter(new DocumentAssets(null!, Substitute.For<IFileStore>(), new MemoryCache(new MemoryCacheOptions())), renderer);

        var csv = await writer.WriteAsync(Document(), ExportFormat.Csv, Ct);
        var xlsx = await writer.WriteAsync(Document(), ExportFormat.Xlsx, Ct);

        csv.FileName.ShouldBe("revenue-by-department_20260901-20260915.csv");
        csv.Content.ShouldBe(ReportExporter.Export(Document(), ExportFormat.Csv).Content);
        csv.Rows.ShouldBe(3);
        csv.Notice.ShouldBeNull();
        xlsx.FileName.ShouldBe("revenue-by-department_20260901-20260915.xlsx");
        xlsx.ContentType.ShouldBe(ReportExporter.XlsxContentType);
        renderer.ReceivedCalls().ShouldBeEmpty();
        Should.Throw<ArgumentOutOfRangeException>(() => ReportExporter.Export(Document(), ExportFormat.Pdf));
    }

    [Fact]
    public async Task When_the_server_cannot_make_the_pdf_the_export_answers_503_pdf_unavailable()
    {
        var renderer = Substitute.For<IPdfRenderer>();
        renderer.RenderAsync(Arg.Any<PdfSource>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<byte[]>(new PdfRenderException("Making the PDF took too long.")));

        var error = await Should.ThrowAsync<PdfRenderException>(() => ReportFileWriter.PdfAsync(Document(), Facility, renderer, Ct));
        var answer = ReportEndpoints.PdfUnavailable(error).ShouldBeOfType<ProblemHttpResult>();

        answer.StatusCode.ShouldBe(503);
        answer.ProblemDetails.Extensions["code"].ShouldBe("pdf.unavailable");
        answer.ProblemDetails.Detail.ShouldBe("Making the PDF took too long. Export to Excel or CSV instead.");
    }

    [Fact]
    public void A_pdf_of_a_long_patient_list_is_audited_for_exactly_the_patients_it_lists()
    {
        var patients = Enumerable.Range(0, 2_500).Select(_ => (Guid?)Guid.NewGuid()).ToList();
        ReportColumn[] columns = [new("uhid", "UHID", ReportValueType.Text, IsPatientIdentifier: true), new("minutes", "Minutes to inform", ReportValueType.Minutes)];
        var report = new ReportDescriptor(
            "critical-values", "Critical values register", ReportCategory.Diagnostics, "Critical results.", "Minutes = result to call.",
            [AnalyticsPermissions.ClinicalRead], [], columns, null, DefaultSort: "minutes", DefaultSortDescending: true, ContainsPatientIdentifiers: true);
        var today = new DateOnly(2026, 9, 15);
        var execution = new ReportExecution(
            report,
            new ReportArguments(today, today, null, null, new Dictionary<string, string>()),
            new FacilityScope(FacilityId, "Main", null),
            new ReportData([.. Enumerable.Range(0, 2_500).Select(i => new object?[] { $"NH-{i:D6}", i })]) { RowPatientIds = patients },
            columns,
            new DateTimeOffset(2026, 9, 15, 6, 30, 0, TimeSpan.Zero),
            "Live database");
        var presenter = new ReportPresenter(null!, Options.Create(new PlatformOptions()));

        var document = presenter.Document(execution, [], "Dr Meera Shah (meera)", null, null);
        var (result, _) = presenter.Present(execution, [], 1, 50, null, null, null);

        document.FacilityId.ShouldBe(FacilityId);
        document.Rows[0][0].ShouldBe("NH-002499");
        document.RowPatientIds.ShouldNotBeNull()[0].ShouldBe(patients[2_499]);
        ReportPresenter.PatientsIn(execution, document, ReportPdf.MaxRows)
            .ShouldBe(Enumerable.Range(500, ReportPdf.MaxRows).Reverse().Select(i => patients[i]!.Value));
        ReportPresenter.PatientsIn(execution, document, document.Rows.Count).Count.ShouldBe(2_500);
        result.PdfNotice.ShouldBe("The PDF lists the first 2,000 of 2,500 rows; the Excel export has every row.");
    }

    private static PrintableDocument Build(ReportDocument document) => ReportPdf.Build(document, ReportPdf.Letterhead(Facility, document));

    /// <summary>A one-row report with these columns and no totals.</summary>
    private static ReportDocument Table(ReportColumn[] columns, object?[] row) => Document() with { Columns = columns, Rows = [row], Totals = null, Summary = [] };

    private static List<object?[]> ManyRows(int count) => [.. Enumerable.Range(1, count).Select(i => new object?[] { $"D{i}", i, (decimal)i, null })];

    private static ReportDocument Document() => new(
        "revenue-by-department",
        "20260901-20260915",
        "Nana Hospitals",
        "Revenue by department",
        "Revenue = net amount of finalized bill lines.",
        [new ReportFilterDto("Period", "01 Sep 2026 – 15 Sep 2026 (Last month)"), new ReportFilterDto("Facility", "Nana Hospitals — Main")],
        "Asha Rao (asha)",
        new DateTimeOffset(2026, 9, 15, 6, 30, 0, TimeSpan.Zero),
        "Live database",
        Columns,
        [
            ["Cardiology", 123_456, 55_000.50m, 30.1m],
            ["Refunds", 5, -1_200m, null],
            ["Orthopaedics", 400, 100m, 0.5m],
        ],
        ["Total", 123_861, 53_900.50m, null],
        [new ReportFigure("net", "Net revenue", 157_100.50m, ReportValueType.Money)],
        ["A note."],
        ContainsPatientIdentifiers: false)
    {
        FacilityId = FacilityId,
    };
}
