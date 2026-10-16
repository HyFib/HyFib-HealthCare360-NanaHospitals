using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Desktop.Services;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>Error returned to the web app as <c>{ ok: false, error: { code, message } }</c>.</summary>
internal sealed class BridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// JSON-RPC over <c>chrome.webview.postMessage</c> (wire format in packages/core/src/bridge/bridge.ts). Requests are
/// accepted only from the pinned server origin, so an external page shown in the view could not drive printers or
/// read the device identity.
/// </summary>
internal sealed class BridgeHost(
    CoreWebView2 webView,
    Uri serverOrigin,
    DesktopSettings settings,
    NetworkStatusService network,
    PrintService printing,
    PrinterSetupStore printers,
    ScannerSetupStore scannerSetup,
    SerialScannerService scanner,
    TrayNotifier tray)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private Action<NetworkStatus>? _networkChanged;
    private Action<string>? _scanned;

    public void Attach()
    {
        webView.WebMessageReceived += OnWebMessageReceived;
        _networkChanged = status => PostEvent("network.changed", status);
        network.Changed += _networkChanged;

        // Every staff window hears each scan; the staff app types it into the focused field of the window in front.
        _scanned = text => PostEvent("scanner.scan", new { text });
        scanner.Scanned += _scanned;
    }

    /// <summary>
    /// Stops the bridge before its window closes (a second window, say), so no event goes to a closed view.
    /// </summary>
    public void Detach()
    {
        webView.WebMessageReceived -= OnWebMessageReceived;
        if (_networkChanged is not null)
        {
            network.Changed -= _networkChanged;
            _networkChanged = null;
        }

        if (_scanned is not null)
        {
            scanner.Scanned -= _scanned;
            _scanned = null;
        }
    }

    public void PostEvent(string name, object? payload)
    {
        var json = JsonSerializer.Serialize(new { type = "event", name, payload }, Json);
        // WebView2 must be called on the UI thread.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                webView.PostWebMessageAsJson(json);
            }
            catch (InvalidOperationException)
            {
                // The view is closing.
            }
        });
    }

    private bool IsTrustedSource(string source) => IsTrustedSource(source, serverOrigin);

    /// <summary>
    /// Only the staff app's pages may use the bridge (printers, device identity, notifications). The patient portal and
    /// the API docs run third-party script (the payment checkout, the API explorer). They have addresses of their own, so
    /// the origin check refuses them; their paths are refused too, for a server from before they moved.
    /// </summary>
    internal static bool IsTrustedSource(string source, Uri serverOrigin) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && Uri.Compare(uri, serverOrigin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
        && !IsUnder(uri, "/portal")
        && !IsUnder(uri, "/docs");

    private static bool IsUnder(Uri uri, string path) =>
        uri.AbsolutePath.Equals(path, StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrustedSource(e.Source))
        {
            Log.Warning("Ignored a bridge message from untrusted origin {Source}", e.Source);
            return;
        }

        long id = 0;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "request")
            {
                return;
            }

            id = root.GetProperty("id").GetInt64();
            var method = root.GetProperty("method").GetString() ?? string.Empty;
            var parameters = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p.Clone() : (JsonElement?)null;

            var result = await DispatchAsync(method, parameters);
            Respond(new { type = "response", id, ok = true, result });
        }
        catch (BridgeException ex)
        {
            Respond(new { type = "response", id, ok = false, error = new { code = ex.Code, message = ex.Message } });
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Log.Warning(ex, "Malformed bridge request");
            Respond(new { type = "response", id, ok = false, error = new { code = "bad_request", message = "The request was not understood." } });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bridge call failed");
            Respond(new { type = "response", id, ok = false, error = new { code = "failed", message = "The desktop could not complete the request." } });
        }
    }

    private void Respond(object message) => webView.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));

    private async Task<object?> DispatchAsync(string method, JsonElement? parameters)
    {
        switch (method)
        {
            case "device.info":
                return new
                {
                    deviceId = settings.DeviceId,
                    deviceSecret = settings.DeviceSecret,
                    deviceName = settings.DeviceName ?? Environment.MachineName,
                    machineName = Environment.MachineName,
                    appVersion = DesktopSettings.AppVersion,
                    osVersion = Environment.OSVersion.VersionString,
                    kiosk = settings.Kiosk,
                };

            case "network.status":
                return network.Current;

            case "printer.list":
                return PrintService.ListPrinters();

            case "printer.getSetup":
                return printers.Load(PrintService.ListPrinters().Select(p => p.Name).ToList());

            case "printer.saveSetup":
            {
                // The staff app offers this only to staff allowed to set up printers, after the server approved the
                // change (and records it once saved). Nothing here can confirm that: the machine policy is what locks
                // the setup against local users and scripts (PrinterSetupStore).
                var setup = Required(parameters).TryGetProperty("setup", out var value) && value.ValueKind == JsonValueKind.Object
                    ? value.Deserialize<WorkstationPrinters>(Json)
                    : null;
                return printers.Save(
                    setup ?? throw new BridgeException("bad_request", "No printers to save."),
                    PrintService.ListPrinters().Select(p => p.Name).ToList(),
                    DateTimeOffset.UtcNow);
            }

            case "printer.printHtml":
            {
                var request = Required(parameters).Deserialize<PrintHtmlRequest>(Json) ?? throw new BridgeException("bad_request", "Nothing to print.");
                await printing.PrintHtmlAsync(request);
                return null;
            }

            case "printer.printRaw":
            {
                // Only printers installed on this computer: never a queue on another computer the page names.
                var printer = PrintService.InstalledPrinter(
                    String(parameters, "printerName"),
                    PrintService.ListPrinters().Select(p => p.Name));
                var data = String(parameters, "data") ?? throw new BridgeException("bad_request", "Nothing to print.");
                RawPrinter.Send(printer, data);
                return null;
            }

            case "scanner.getSetup":
            {
                var (setup, locked, problems) = scannerSetup.Load();
                return new ScannerSetupInfo(setup, locked, Environment.MachineName, problems, scanner.Status, SerialScannerService.PortNames());
            }

            case "scanner.saveSetup":
            {
                // As printer.saveSetup: offered only to staff allowed to set up printers, after the server approved it.
                var setup = Required(parameters).TryGetProperty("setup", out var value) && value.ValueKind == JsonValueKind.Object
                    ? value.Deserialize<WorkstationScanner>(Json)
                    : null;
                var saved = scannerSetup.Save(
                    setup ?? throw new BridgeException("bad_request", "No scanner to save."),
                    DateTimeOffset.UtcNow);
                scanner.Use(saved);
                return saved;
            }

            case "shell.openExternal":
                OpenExternal(String(parameters, "url"));
                return null;

            case "notifications.show":
                tray.Show(String(parameters, "title") ?? "HealthCare360", String(parameters, "body") ?? string.Empty);
                return null;

            case "speech.dictate":
                return new { text = await DictationService.DictateAsync(String(parameters, "language") ?? "en-IN") };

            case "dsc.signPrescription":
                // Only the staff app reaches this (IsTrustedSource), and the doctor confirms in the Windows certificate
                // dialog and with the token's PIN; the server checks the signature again before it keeps it.
                return DscSigner.Sign(String(parameters, "content"));

            default:
                throw new BridgeException("method_not_found", $"'{method}' is not supported by this version of HC360 Desktop.");
        }
    }

    /// <summary>Opens web, mail and phone links in the user's default apps; anything else is refused.</summary>
    internal static void OpenExternal(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "mailto" or "tel"))
        {
            throw new BridgeException("not_allowed", "Only web, email and phone links can be opened.");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static JsonElement Required(JsonElement? parameters) =>
        parameters ?? throw new BridgeException("bad_request", "Missing parameters.");

    private static string? String(JsonElement? parameters, string name) =>
        parameters is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
