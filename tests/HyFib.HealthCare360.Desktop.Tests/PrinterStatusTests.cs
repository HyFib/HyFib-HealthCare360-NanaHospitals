using System.Printing;
using System.Text.Json;
using HyFib.HealthCare360.Desktop.Bridge;

namespace HyFib.HealthCare360.Desktop.Tests;

/// <summary>
/// A job the Windows spooler accepts is not a job on paper: the queue of a paused or offline printer takes it and holds it.
/// These are the rules that turn a queue's state into what the screen is told (<see cref="PrinterStatus"/>), which need no
/// printer or spooler.
/// </summary>
public sealed class PrinterStatusTests
{
    [Fact]
    public void A_queue_with_nothing_wrong_is_ready()
    {
        PrinterStatus.Classify(PrintQueueStatus.None).ShouldBe(PrinterCondition.Ready);

        // Busy and printing are a printer working, not one that cannot take the job.
        PrinterStatus.Classify(PrintQueueStatus.Printing | PrintQueueStatus.Busy).ShouldBe(PrinterCondition.Ready);
    }

    [Fact]
    public void A_paused_queue_is_paused()
    {
        PrinterStatus.Classify(PrintQueueStatus.Paused).ShouldBe(PrinterCondition.Paused);
    }

    [Theory]
    [InlineData(PrintQueueStatus.Offline)]
    [InlineData(PrintQueueStatus.NotAvailable)]
    public void An_offline_or_unavailable_queue_is_offline(PrintQueueStatus status)
    {
        PrinterStatus.Classify(status).ShouldBe(PrinterCondition.Offline);
    }

    [Fact]
    public void A_printer_set_to_work_offline_is_offline_even_when_windows_lists_it_as_ready()
    {
        PrinterStatus.Classify(PrintQueueStatus.None, isOffline: true).ShouldBe(PrinterCondition.Offline);
    }

    [Theory]
    [InlineData(PrintQueueStatus.Error)]
    [InlineData(PrintQueueStatus.PaperOut)]
    [InlineData(PrintQueueStatus.PaperJam)]
    [InlineData(PrintQueueStatus.PaperProblem)]
    [InlineData(PrintQueueStatus.NoToner)]
    [InlineData(PrintQueueStatus.DoorOpen)]
    [InlineData(PrintQueueStatus.UserIntervention)]
    public void A_fault_the_printer_reports_needs_attention(PrintQueueStatus fault)
    {
        PrinterStatus.Classify(fault).ShouldBe(PrinterCondition.NeedsAttention);
    }

    [Fact]
    public void Offline_wins_over_paused_and_paused_over_a_fault()
    {
        PrinterStatus.Classify(PrintQueueStatus.Paused | PrintQueueStatus.Offline).ShouldBe(PrinterCondition.Offline);
        PrinterStatus.Classify(PrintQueueStatus.Paused | PrintQueueStatus.PaperOut).ShouldBe(PrinterCondition.Paused);
    }

    [Fact]
    public void The_worst_of_several_printers_is_the_one_to_act_on_first()
    {
        PrinterStatus.Worst([]).ShouldBe(PrinterCondition.Ready);
        PrinterStatus.Worst([PrinterCondition.Ready, PrinterCondition.NeedsAttention]).ShouldBe(PrinterCondition.NeedsAttention);
        PrinterStatus.Worst([PrinterCondition.NeedsAttention, PrinterCondition.Paused, PrinterCondition.Ready]).ShouldBe(PrinterCondition.Paused);
        PrinterStatus.Worst([PrinterCondition.Paused, PrinterCondition.Offline]).ShouldBe(PrinterCondition.Offline);
    }

    [Fact]
    public void A_job_on_a_ready_printer_is_reported_printed_with_no_message()
    {
        var outcome = PrinterStatus.Outcome("EPSON TM-T82 Receipt", PrinterCondition.Ready);

        (outcome.Status, outcome.Printer, outcome.Message).ShouldBe((PrintOutcome.Printed, "EPSON TM-T82 Receipt", null));
    }

    [Theory]
    [InlineData("Paused", PrintOutcome.QueuedPaused, "paused")]
    [InlineData("Offline", PrintOutcome.QueuedOffline, "offline")]
    [InlineData("NeedsAttention", PrintOutcome.QueuedNeedsAttention, "problem")]
    public void A_job_on_a_stuck_printer_is_reported_queued_and_never_printed(string condition, string status, string saying)
    {
        var outcome = PrinterStatus.Outcome("HP LaserJet M404", Enum.Parse<PrinterCondition>(condition));

        outcome.Status.ShouldBe(status);
        outcome.Status.ShouldNotBe(PrintOutcome.Printed);
        outcome.Printer.ShouldBe("HP LaserJet M404");
        outcome.Message.ShouldNotBeNull().ShouldContain("'HP LaserJet M404'");
        outcome.Message.ShouldContain("queued");
        outcome.Message.ShouldContain(saying);
    }

    [Fact]
    public void A_stuck_printer_the_person_chose_in_the_dialog_is_not_named_when_it_is_not_known()
    {
        var outcome = PrinterStatus.Outcome(null, PrinterCondition.Paused);

        outcome.Status.ShouldBe(PrintOutcome.QueuedPaused);
        outcome.Printer.ShouldBeNull();
        outcome.Message.ShouldNotBeNull().ShouldContain("the printer you chose");
    }

    [Fact]
    public void The_bridge_hands_the_screen_the_status_in_camel_case()
    {
        var json = JsonSerializer.Serialize(PrinterStatus.Outcome("HP LaserJet M404", PrinterCondition.Offline), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("status").GetString().ShouldBe("queued_printer_offline");
        document.RootElement.GetProperty("printer").GetString().ShouldBe("HP LaserJet M404");
        document.RootElement.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
