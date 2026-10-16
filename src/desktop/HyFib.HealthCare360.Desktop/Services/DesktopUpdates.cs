using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.WindowsInterop.Updates;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>The package the Server Node offers: its version, and its manifest and signature (base64 of the files' bytes).</summary>
internal sealed record DesktopUpdateOffer(string Version, DateTimeOffset ReleasedAt, long Size, string Manifest, string Signature);

/// <summary>
/// HC360 Desktop's self-update from the Server Node (SRS DED-18), so workstations need no internet access. Every few
/// hours an enrolled HC360 Desktop asks the server, over the connection pinned to the hospital CA and with its device
/// credential, which package it offers; a newer one, signed by HyFib, is downloaded and staged (<see cref="UpdateStager"/>).
/// The "HC360 Desktop update" task installs it as SYSTEM when HC360 Desktop is not running: at the next start-up or
/// sign-in, every few hours, or straight away when staff choose "Restart to update".
/// Nothing happens when IT turned self-update off (policy.json "autoUpdate": false), or when the updater is not
/// installed beside HC360 Desktop (an installation older than self-update, or a developer's build).
/// </summary>
internal sealed class DesktopUpdateService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly DesktopSettings _settings;
    private readonly UpdateStager _stager;
    private readonly string _installDirectory;

    public DesktopUpdateService(Uri server, ServerTrust trust, DesktopSettings settings)
    {
        // The package may take minutes over a weak WiFi link: each request has its own time limit instead.
        _http = trust.CreateHttpClient(server, Timeout.InfiniteTimeSpan);
        _settings = settings;
        _installDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        _stager = new UpdateStager(DesktopUpdateLayout.StagingDirectory, ReleaseKeys.Trusted, DesktopSettings.AppVersion);
    }

    public string UpdaterPath => Path.Combine(DesktopUpdateLayout.UpdaterDirectoryFor(_installDirectory), DesktopUpdateLayout.UpdaterExe);

    /// <summary>Whether this HC360 Desktop updates itself: IT allows it, and the updater is installed beside it.</summary>
    public bool Enabled => _settings.AutoUpdateAllowed && File.Exists(UpdaterPath);

    /// <summary>A newer version, staged and verified, that waits to be installed; null when there is none.</summary>
    public ReleaseManifest? Ready() => Enabled ? _stager.Ready() : null;

    /// <summary>Removes a staged package that is installed now.</summary>
    public void CleanUp() => _stager.CleanUp(TimeSpan.FromDays(7));

    /// <summary>
    /// Asks the server for its package and stages a newer one. The staged package's manifest, or null when there is
    /// nothing newer (or the device is not enrolled, or the server cannot be reached; the next check tries again).
    /// </summary>
    public async Task<ReleaseManifest?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || _settings.DeviceId is not { } deviceId || _settings.DeviceSecret is not { } secret)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = Request(HttpMethod.Get, "api/v1/devices/desktop-update", deviceId, secret);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return Ready();
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("The server answered {Status} when asked for an HC360 Desktop update", (int)response.StatusCode);
                return Ready();
            }

            var offer = await response.Content.ReadFromJsonAsync<DesktopUpdateOffer>(Json, timeout.Token);
            if (offer is null)
            {
                return Ready();
            }

            return await _stager.StageAsync(
                Convert.FromBase64String(offer.Manifest), Convert.FromBase64String(offer.Signature),
                (file, manifest, ct) => DownloadAsync(file, manifest, deviceId, secret, ct), cancellationToken);
        }
        catch (ReleaseRejectedException ex)
        {
            Log.Warning("The HC360 Desktop update the server offers was not staged: {Reason}", ex.Message);
            return Ready();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Log.Information("No HC360 Desktop update this time: {Reason}", ex.Message);
            }

            return Ready();
        }
    }

    /// <summary>
    /// "Restart to update": starts the updater as this user, which waits for HC360 Desktop to close, has the task install
    /// the update, and opens HC360 Desktop again. False when the updater could not be started.
    /// </summary>
    public bool StartRestart()
    {
        try
        {
            var start = new ProcessStartInfo(UpdaterPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(UpdaterPath)! };
            foreach (var argument in new[] { "restart", "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--install-dir", _installDirectory })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error(ex, "The HC360 Desktop updater could not be started");
            return false;
        }
    }

    // Appends the package from the file's length on (a range request), or writes it all again when the server sends it
    // whole. If-Range carries the package's SHA-256, so a range is only ever of the package the file began with.
    private async Task DownloadAsync(FileStream file, ReleaseManifest manifest, Guid deviceId, string secret, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var request = Request(HttpMethod.Get, "api/v1/devices/desktop-update/package", deviceId, secret);
        if (file.Length > 0)
        {
            request.Headers.Range = new RangeHeaderValue(file.Length, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{manifest.Sha256.ToLowerInvariant()}\""));
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            file.SetLength(0);
            file.Position = 0;
        }
        else if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"The server answered {(int)response.StatusCode} for the HC360 Desktop package.");
        }

        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
        await content.CopyToAsync(file, timeout.Token);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid deviceId, string secret)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Device-Id", deviceId.ToString());
        request.Headers.Add("X-Device-Key", secret);
        return request;
    }

    public void Dispose() => _http.Dispose();
}
