using System.Printing;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>What the Windows spooler says of a printer's queue, as far as a job reaching paper goes.</summary>
internal enum PrinterCondition
{
    /// <summary>Nothing the spooler reports stops the job.</summary>
    Ready,

    /// <summary>Paused in Windows (Printers and scanners, "Pause printing"): jobs wait until someone resumes it.</summary>
    Paused,

    /// <summary>Offline: switched off or unplugged, or set to "Use Printer Offline". Jobs wait for it to come back.</summary>
    Offline,

    /// <summary>The printer reports a problem a person must clear: paper out or jammed, a door open, no toner, an error.</summary>
    NeedsAttention,
}

/// <summary>
/// How a print job ended, for the screen to say: <see cref="Printed"/> when the printer took it, else one of the
/// <c>queued_printer_…</c> statuses, which mean Windows accepted the job but it will not reach paper until someone
/// resumes the printer, brings it back online or clears its fault. The job is not lost, so these are not errors; the
/// staff app should tell the person and not say "printed".
/// </summary>
internal sealed record PrintOutcome(string Status, string? Printer, string? Message)
{
    public const string Printed = "printed";
    public const string QueuedPaused = "queued_printer_paused";
    public const string QueuedOffline = "queued_printer_offline";
    public const string QueuedNeedsAttention = "queued_printer_needs_attention";

    /// <summary>The job went to a printer that is ready, or to whichever one the person chose and none is known to be stuck.</summary>
    public static PrintOutcome Done(string? printer) => new(Printed, printer, null);
}

/// <summary>
/// The state of a printer's Windows queue, read from the spooler. A job the spooler accepts is not a job on paper: the
/// queue of a paused or offline printer takes it and holds it, so "the spooler said yes" alone would report success for
/// a slip that nobody will ever see.
/// </summary>
internal static class PrinterStatus
{
    /// <summary>
    /// The condition a queue's status flags mean. Offline first (the job cannot go however the queue is set), then
    /// paused, then a fault the printer reports. <paramref name="isOffline"/> is <see cref="PrintQueue.IsOffline"/>, which
    /// also covers a printer set to "Use Printer Offline" that Windows may still list as ready.
    /// </summary>
    internal static PrinterCondition Classify(PrintQueueStatus status, bool isOffline = false)
    {
        const PrintQueueStatus faults = PrintQueueStatus.Error | PrintQueueStatus.PaperJam | PrintQueueStatus.PaperOut | PrintQueueStatus.PaperProblem
            | PrintQueueStatus.NoToner | PrintQueueStatus.DoorOpen | PrintQueueStatus.OutOfMemory | PrintQueueStatus.UserIntervention
            | PrintQueueStatus.OutputBinFull;

        if (isOffline || status.HasFlag(PrintQueueStatus.Offline) || status.HasFlag(PrintQueueStatus.NotAvailable))
        {
            return PrinterCondition.Offline;
        }

        if (status.HasFlag(PrintQueueStatus.Paused))
        {
            return PrinterCondition.Paused;
        }

        return (status & faults) != PrintQueueStatus.None ? PrinterCondition.NeedsAttention : PrinterCondition.Ready;
    }

    /// <summary>The worst of several printers' conditions: the one a person must act on first.</summary>
    internal static PrinterCondition Worst(IEnumerable<PrinterCondition> conditions) =>
        conditions.Aggregate(PrinterCondition.Ready, (worst, next) => Rank(next) > Rank(worst) ? next : worst);

    private static int Rank(PrinterCondition condition) => condition switch
    {
        PrinterCondition.Offline => 3,
        PrinterCondition.Paused => 2,
        PrinterCondition.NeedsAttention => 1,
        _ => 0,
    };

    /// <summary>
    /// What to tell the screen for a job sent to <paramref name="printer"/> (null: the printer the person chose in the
    /// dialog, not known) whose queue is in <paramref name="condition"/>.
    /// </summary>
    internal static PrintOutcome Outcome(string? printer, PrinterCondition condition)
    {
        var where = printer is null ? "the printer you chose" : $"'{printer}'";
        return condition switch
        {
            PrinterCondition.Paused => new(PrintOutcome.QueuedPaused, printer,
                $"The document is queued on {where}, but the printer is paused in Windows, so nothing will print until it is resumed."),
            PrinterCondition.Offline => new(PrintOutcome.QueuedOffline, printer,
                $"The document is queued on {where}, but the printer is offline, so nothing will print until it is switched on and connected."),
            PrinterCondition.NeedsAttention => new(PrintOutcome.QueuedNeedsAttention, printer,
                $"The document is queued on {where}, but the printer reports a problem (paper out or jammed, a door open, or an error), so it may not print until that is cleared."),
            _ => PrintOutcome.Done(printer),
        };
    }

    /// <summary>
    /// The condition of the named printer's queue now, or null when the spooler cannot be asked: a state that cannot be
    /// read never fails a print that was accepted, and is not reported as a fault.
    /// </summary>
    internal static PrinterCondition? Read(string printerName)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = new PrintQueue(server, printerName);
            queue.Refresh();
            return Classify(queue.QueueStatus, queue.IsOffline);
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "The state of printer {Printer} could not be read", printerName);
            return null;
        }
    }

    /// <summary>
    /// The conditions of the printers that took a job since <paramref name="since"/>: for a print the person sent from the
    /// Windows dialog, where the printer is theirs to choose and not known beforehand. Empty when no printer shows a new
    /// job (the dialog was cancelled, or the job was already printed and gone), so nothing is claimed either way.
    /// </summary>
    internal static IReadOnlyList<(string Printer, PrinterCondition Condition)> ReceivingJobsSince(IEnumerable<string> printers, DateTime since)
    {
        var found = new List<(string, PrinterCondition)>();
        foreach (var name in printers)
        {
            try
            {
                using var server = new LocalPrintServer();
                using var queue = new PrintQueue(server, name);
                queue.Refresh();
                var received = queue.GetPrintJobInfoCollection().Cast<PrintSystemJobInfo>().Any(job => job.TimeJobSubmitted.ToUniversalTime() >= since.ToUniversalTime());
                if (received)
                {
                    found.Add((name, Classify(queue.QueueStatus, queue.IsOffline)));
                }
            }
            catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "The jobs of printer {Printer} could not be read", name);
            }
        }

        return found;
    }
}
