using HyFib.HealthCare360.Integrations.Features.Labels;
using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Integrations.Labels.Templates;
using HyFib.HealthCare360.Integrations.Tests.Labels;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyFib.HealthCare360.Integrations.Tests.Features;

public sealed class LabelEndpointTests
{
    private static readonly LabelPrinterSetup Tsc = new("TSPL", 300, 50, 25);

    [Fact]
    public void Answers_the_commands_for_every_label_in_the_printers_language()
    {
        var result = LabelEndpoints.Render(Tsc, [LabelSamples.Tube(), LabelSamples.Tube(stat: true)], 2, LabTubeTemplate.Draw);

        var commands = result.ShouldBeOfType<Ok<LabelCommands>>().Value!;
        commands.Language.ShouldBe("tspl");
        commands.Labels.ShouldBe(2);
        commands.Content.ShouldStartWith("SIZE 50 mm,25 mm\r\n");
        commands.Content.Split("\r\n").Count(line => line == "PRINT 1,2").ShouldBe(2);
    }

    [Fact]
    public void Text_the_printer_fonts_cannot_print_is_refused_so_the_page_is_printed_instead()
    {
        var result = LabelEndpoints.Render(Tsc, [LabelSamples.Tube() with { PatientName = "रवि शर्मा" }], 1, LabTubeTemplate.Draw);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        problem.Status.ShouldBe(422);
        problem.Extensions["code"].ShouldBe("labels.text_not_printable");
        problem.Detail.ShouldBe("The patient's name has letters the label printer's own fonts cannot print.");
    }

    [Fact]
    public void A_label_that_does_not_fit_is_refused_with_what_to_change()
    {
        var narrow = Tsc with { WidthMm = 25 };

        var result = LabelEndpoints.Render(narrow, [LabelSamples.Tube()], 1, LabTubeTemplate.Draw);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        problem.Status.ShouldBe(422);
        problem.Extensions["code"].ShouldBe("labels.does_not_fit");
        problem.Detail!.ShouldContain("Use wider labels");
    }
}
