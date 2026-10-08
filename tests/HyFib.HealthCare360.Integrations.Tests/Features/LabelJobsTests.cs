using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Features.Labels;
using HyFib.HealthCare360.Integrations.Labels.Templates;
using HyFib.HealthCare360.Integrations.Simulator;
using HyFib.HealthCare360.Integrations.Tests.Labels;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Features;

/// <summary>
/// Label jobs on the workstations' printers: the commands for every label, refusals the staff app acts on, and a trace
/// of every job in the integration log that never holds what is on the labels. (The simulator's inbox is covered end to
/// end.)
/// </summary>
public sealed class LabelJobsTests
{
    private static readonly LabelPrinterSetup Tsc = new("TSPL", 300, 50, 25);

    private readonly Tracing _tracing = new();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LabelJobs Jobs => new(
        _tracing.Trace,
        new IntegrationSettingsReader(_settings, Substitute.For<ICurrentUser>(), NullLogger<IntegrationSettingsReader>.Instance),
        new SimulatorInbox(Substitute.For<IDbContextFactory<HospitalDbContext>>(), Substitute.For<ICurrentUser>(), TimeProvider.System));

    [Fact]
    public async Task Answers_the_commands_for_every_label_in_the_printers_language()
    {
        var result = await Jobs.RenderAsync("lab-tubes", Tsc, [LabelSamples.Tube(), LabelSamples.Tube(stat: true)], 2, LabTubeTemplate.Draw, Ct);

        var commands = result.ShouldBeOfType<Ok<LabelCommands>>().Value!;
        commands.Language.ShouldBe("tspl");
        commands.Labels.ShouldBe(2);
        commands.Content.ShouldStartWith("SIZE 50 mm,25 mm\r\n");
        commands.Content.Split("\r\n").Count(line => line == "PRINT 1,2").ShouldBe(2);
        var trace = _tracing.Entries.ShouldHaveSingleItem();
        (trace.Integration, trace.Provider, trace.Operation, trace.Success).ShouldBe(("labels", "tspl", "lab-tubes", true));
        trace.Request.ShouldBe("2 label(s) × 2, 50 × 25 mm at 300 dpi");
        trace.Response.ShouldNotBeNull().ShouldNotContain(LabelSamples.Tube().PatientName);
    }

    [Fact]
    public async Task Text_the_printer_fonts_cannot_print_is_refused_so_the_page_is_printed_instead()
    {
        var result = await Jobs.RenderAsync("lab-tubes", Tsc, [LabelSamples.Tube() with { PatientName = "रवि शर्मा" }], 1, LabTubeTemplate.Draw, Ct);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        problem.Status.ShouldBe(422);
        problem.Extensions["code"].ShouldBe("labels.text_not_printable");
        problem.Detail.ShouldBe("The patient's name has letters the label printer's own fonts cannot print.");
        var trace = _tracing.Entries.ShouldHaveSingleItem();
        trace.Success.ShouldBeFalse();
        trace.Error.ShouldBe(problem.Detail);
    }

    [Fact]
    public async Task A_label_that_does_not_fit_is_refused_with_what_to_change()
    {
        var narrow = Tsc with { WidthMm = 25 };

        var result = await Jobs.RenderAsync("lab-tubes", narrow, [LabelSamples.Tube()], 1, LabTubeTemplate.Draw, Ct);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        problem.Status.ShouldBe(422);
        problem.Extensions["code"].ShouldBe("labels.does_not_fit");
        problem.Detail!.ShouldContain("Use wider labels");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("printers", false)]
    [InlineData(" Simulator ", true)]
    public async Task The_facility_chooses_the_printers_or_the_simulator(string? provider, bool simulator)
    {
        _settings.GetAsync<LabelSettings>(IntegrationNames.SettingsKey(IntegrationNames.Labels), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(provider is null ? null : new LabelSettings { Provider = provider });

        (await Jobs.IsSimulatorAsync(Ct)).ShouldBe(simulator);
    }
}
