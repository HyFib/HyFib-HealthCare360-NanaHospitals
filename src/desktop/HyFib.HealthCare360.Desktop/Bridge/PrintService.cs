using System.Drawing.Printing;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

internal sealed record PrinterInfo(string Name, bool IsDefault);

internal sealed record PrintHtmlRequest(string Html, string? PrinterName, bool? Silent, int? Copies, string? PageSize, bool? Landscape);

/// <summary>
/// Prints HTML documents (bills, token slips, wristbands, reports) through an invisible WebView2 with scripts
/// disabled. Silent jobs go straight to the printer they name, which is what counters and wards need (the staff app
/// names the printer this computer uses for that kind of print). A silent job that names no printer gets the Windows
/// print dialog, so nothing is printed on whichever printer happens to be the Windows default.
/// <para>
/// The spooler accepts a job for a paused or offline printer and holds it, so a job accepted is not a job on paper. Once
/// it is accepted the printer's state is read (<see cref="PrinterStatus"/>), and a printer that is paused, offline or
/// reporting a fault is told to the screen as a queued job (<see cref="PrintOutcome"/>), not as a print.
/// </para>
/// </summary>
internal sealed class PrintService(CoreWebView2Environment environment, IntPtr ownerWindow)
{
    // Width × height in inches for the page sizes the web app uses (see packages/core/src/print/print.ts). The wristband
    // is an adult band, 1 × 11 inches, printed along its length.
    internal static readonly IReadOnlyDictionary<string, (double Width, double Height)> PageSizes =
        new Dictionary<string, (double Width, double Height)>(StringComparer.OrdinalIgnoreCase)
        {
            ["A4"] = (8.27, 11.69),
            ["A5"] = (5.83, 8.27),
            ["Receipt80"] = (3.15, 11.0),
            ["Label50x25"] = (1.97, 0.98),
            ["Wristband"] = (1.0, 11.0),
        };

    /// <summary>Thermal stock (receipts, labels, wristbands) prints almost to the edge; paper keeps a 0.4 inch margin.</summary>
    internal static double Margin(string pageSize) =>
        pageSize.StartsWith("Receipt", StringComparison.OrdinalIgnoreCase)
        || pageSize.StartsWith("Label", StringComparison.OrdinalIgnoreCase)
        || pageSize.Equals("Wristband", StringComparison.OrdinalIgnoreCase)
            ? 0.05
            : 0.4;

    /// <summary>Silent only to a named printer: without one the person chooses in the print dialog.</summary>
    internal static bool PrintsSilently(PrintHtmlRequest request) => request.Silent == true && !string.IsNullOrWhiteSpace(request.PrinterName);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public static IReadOnlyList<PrinterInfo> ListPrinters()
    {
        var defaultName = new PrinterSettings().PrinterName;
        return PrinterSettings.InstalledPrinters.Cast<string>()
            .Select(name => new PrinterInfo(name, string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The installed printer a job names, as Windows spells it. Pages may print only to printers installed on this
    /// computer: a name Windows does not list, a shared queue on another computer (<c>\\host\queue</c>) among them,
    /// is refused rather than opened, so a page cannot send printer commands anywhere on the network.
    /// </summary>
    /// <exception cref="BridgeException">The printer is not installed on this computer.</exception>
    internal static string InstalledPrinter(string? name, IEnumerable<string> installed)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BridgeException("bad_request", "Choose a printer.");
        }

        return installed.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new BridgeException(
                   "printer_not_found",
                   name.StartsWith(@"\\", StringComparison.Ordinal)
                       ? $"The printer '{name}' is not installed on this computer. "
                         + "Add it in Windows Settings › Printers and scanners first."
                       : $"The printer '{name}' is not installed on this computer.");
    }

    public async Task<PrintOutcome> PrintHtmlAsync(PrintHtmlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Html) || request.Html.Length > 1_500_000)
        {
            throw new BridgeException("bad_request", "The document is empty or too large to print.");
        }

        if (request.PrinterName is { } name && !PrinterSettings.InstalledPrinters.Cast<string>().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new BridgeException("printer_not_found", $"The printer '{name}' is not installed on this computer.");
        }

        // One job at a time keeps slips from interleaving on shared counter printers.
        await _gate.WaitAsync();
        CoreWebView2Controller? controller = null;
        PrintOutcome outcome;
        try
        {
            controller = await environment.CreateCoreWebView2ControllerAsync(ownerWindow);
            controller.IsVisible = false;
            var view = controller.CoreWebView2;
            view.Settings.IsScriptEnabled = false;
            view.Settings.AreDefaultContextMenusEnabled = false;

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            view.NavigateToString(request.Html);
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20)))
            {
                throw new BridgeException("print_failed", "The document could not be prepared for printing.");
            }

            if (PrintsSilently(request))
            {
                var settings = environment.CreatePrintSettings();
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                settings.Copies = Math.Clamp(request.Copies ?? 1, 1, 20);
                settings.Orientation = request.Landscape == true ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
                settings.PrinterName = request.PrinterName;

                if (request.PageSize is not null && PageSizes.TryGetValue(request.PageSize, out var size))
                {
                    settings.PageWidth = size.Width;
                    settings.PageHeight = size.Height;
                    settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = Margin(request.PageSize);
                }

                var status = await view.PrintAsync(settings);
                if (status != CoreWebView2PrintStatus.Succeeded)
                {
                    Log.Warning("Silent print to {Printer} ended with {Status}", request.PrinterName ?? "(default)", status);
                    throw new BridgeException(
                        status == CoreWebView2PrintStatus.PrinterUnavailable ? "printer_unavailable" : "print_failed",
                        status == CoreWebView2PrintStatus.PrinterUnavailable ? "The printer is offline or out of paper." : "Printing failed.");
                }

                // The spooler took the job. Whether it can reach paper is the queue's state, read now.
                var condition = await Task.Run(() => PrinterStatus.Read(request.PrinterName!)) ?? PrinterCondition.Ready;
                outcome = PrinterStatus.Outcome(request.PrinterName, condition);
            }
            else
            {
                var opened = DateTime.UtcNow;
                view.ShowPrintUI(CoreWebView2PrintDialogKind.System);
                // The system dialog runs on its own; keep the document alive long enough to spool.
                await Task.Delay(TimeSpan.FromSeconds(30));

                // The printer was the person's to choose: those that took a job while the dialog was open are the ones to ask.
                var installed = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
                var received = await Task.Run(() => PrinterStatus.ReceivingJobsSince(installed, opened));
                var worst = PrinterStatus.Worst(received.Select(r => r.Condition));
                outcome = PrinterStatus.Outcome(received.Count == 1 ? received[0].Printer : null, worst);
            }
        }
        finally
        {
            controller?.Close();
            _gate.Release();
        }

        if (outcome.Status != PrintOutcome.Printed)
        {
            Log.Warning("Print to {Printer} queued but not printing: {Status}", outcome.Printer ?? "(chosen in the dialog)", outcome.Status);
        }

        return outcome;
    }
}
