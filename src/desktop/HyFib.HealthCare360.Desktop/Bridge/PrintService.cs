using System.Drawing.Printing;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

internal sealed record PrinterInfo(string Name, bool IsDefault);

internal sealed record PrintHtmlRequest(string Html, string? PrinterName, bool? Silent, int? Copies, string? PageSize, bool? Landscape);

/// <summary>
/// Prints HTML documents (bills, token slips, wristbands, reports) through an invisible WebView2 with scripts
/// disabled. Silent jobs go straight to the chosen or default printer, which is what counters and wards need; the
/// Windows print dialog is shown otherwise.
/// </summary>
internal sealed class PrintService(CoreWebView2Environment environment, IntPtr ownerWindow)
{
    // Width × height in inches for the page sizes the web app uses (see packages/core/src/print/print.ts).
    private static readonly Dictionary<string, (double Width, double Height)> PageSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A4"] = (8.27, 11.69),
        ["A5"] = (5.83, 8.27),
        ["Receipt80"] = (3.15, 11.0),
        ["Label50x25"] = (1.97, 0.98),
    };

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

    public async Task PrintHtmlAsync(PrintHtmlRequest request)
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

            if (request.Silent == true)
            {
                var settings = environment.CreatePrintSettings();
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                settings.Copies = Math.Clamp(request.Copies ?? 1, 1, 20);
                settings.Orientation = request.Landscape == true ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
                if (request.PrinterName is not null)
                {
                    settings.PrinterName = request.PrinterName;
                }

                if (request.PageSize is not null && PageSizes.TryGetValue(request.PageSize, out var size))
                {
                    settings.PageWidth = size.Width;
                    settings.PageHeight = size.Height;
                    settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = request.PageSize.StartsWith("Receipt", StringComparison.OrdinalIgnoreCase) || request.PageSize.StartsWith("Label", StringComparison.OrdinalIgnoreCase) ? 0.05 : 0.4;
                }

                var status = await view.PrintAsync(settings);
                if (status != CoreWebView2PrintStatus.Succeeded)
                {
                    Log.Warning("Silent print to {Printer} ended with {Status}", request.PrinterName ?? "(default)", status);
                    throw new BridgeException(
                        status == CoreWebView2PrintStatus.PrinterUnavailable ? "printer_unavailable" : "print_failed",
                        status == CoreWebView2PrintStatus.PrinterUnavailable ? "The printer is offline or out of paper." : "Printing failed.");
                }
            }
            else
            {
                view.ShowPrintUI(CoreWebView2PrintDialogKind.System);
                // The system dialog runs on its own; keep the document alive long enough to spool.
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            controller?.Close();
            _gate.Release();
        }
    }
}
