using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.WindowsInterop;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>One sample of the Server Node's WiFi link (kept 30 days for the health dashboard).</summary>
[NotAudited]
public sealed class WifiSample
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset At { get; set; }
    public bool Connected { get; set; }
    public string? Ssid { get; set; }
    public string? Bssid { get; set; }
    public int SignalQuality { get; set; }
    public int? RssiDbm { get; set; }
    public double? ReceiveRateMbps { get; set; }
    public double? TransmitRateMbps { get; set; }
    public string? Security { get; set; }
}

internal sealed class WifiSampleConfiguration : IEntityTypeConfiguration<WifiSample>
{
    public void Configure(EntityTypeBuilder<WifiSample> b)
    {
        b.ToTable("wifi_samples", "platform");
        b.HasKey(x => x.Id);
        b.Property(x => x.Ssid).HasMaxLength(64);
        b.Property(x => x.Bssid).HasMaxLength(32);
        b.Property(x => x.Security).HasMaxLength(32);
        b.HasIndex(x => x.At);
    }
}

public enum WifiEventKind
{
    Disconnected = 0,
    Reconnected,
    WeakSignal,
    SignalRecovered,
    InsecureNetwork,
    UnapprovedNetwork,
    AccessPointChanged,
    SharedPasswordNetwork,

    /// <summary>Windows shows no WiFi adapter while the link was in use: see <see cref="WifiAdapterAbsence"/>.</summary>
    AdapterLost,

    /// <summary>
    /// A network cable the Server Node was on during this run is out, or its switch is off, and the workstations cannot reach
    /// it: no other cable is up, or theirs is the one lost (see <see cref="CableEventDetector"/>). Critical.
    /// </summary>
    CableDisconnected,

    /// <summary>
    /// A network cable is up after <see cref="CableDisconnected"/> or <see cref="OtherCableDisconnected"/>, or after a WiFi loss
    /// that was told: the server is back on the network.
    /// </summary>
    CableReconnected,

    /// <summary>
    /// A network cable is lost while the server is still on another, and the workstations' requests did not come over it (an
    /// imaging network's cable, say): see <see cref="CableEventDetector"/>. A warning.
    /// </summary>
    OtherCableDisconnected,
}

/// <summary>Why Windows shows no WiFi adapter, as far as the server can tell (<see cref="WlanService"/>).</summary>
public enum WifiAdapterAbsence
{
    /// <summary>It cannot tell: the WLAN AutoConfig service is not installed, or Windows did not answer.</summary>
    Unknown = 0,

    /// <summary>The WLAN AutoConfig service (WlanSvc) is installed but not running, so Windows sees no WiFi at all.</summary>
    ServiceStopped,

    /// <summary>The WLAN AutoConfig service runs, but there is no wireless adapter: it was disabled or removed.</summary>
    NoAdapter,
}

public sealed record WifiEvent(DateTimeOffset At, WifiEventKind Kind, string Message)
{
    /// <summary>
    /// The cables a cable's event is about: those it tells lost, or those it tells back (with a server back on the network
    /// over a cable not up before, those it no longer waits for). Empty for the WiFi's events.
    /// </summary>
    public IReadOnlyList<string> Cables { get; init; } = [];
}

/// <summary>
/// A network cable up during this run that is lost and not back (DED-06): its adapter's name, the addresses it had when it
/// was last up (a card that is down has none), when it was lost, and whether the workstations' requests came over it then
/// (<see cref="ServedAddresses"/>).
/// </summary>
public sealed record LostCable(string Name, IReadOnlyList<string> Addresses, DateTimeOffset LostAt, bool ServedWorkstations);

/// <summary>Latest known state of the Server Node's links, its WiFi and its network cables (singleton, read by the health dashboard).</summary>
public sealed class WifiLinkState
{
    private readonly Lock _gate = new();
    private readonly LinkedList<WifiEvent> _events = new();

    public bool Supported { get; private set; } = true;
    public WifiConnectionInfo? Current { get; private set; }
    public DateTimeOffset? LastSampleAt { get; private set; }
    public DateTimeOffset? ConnectedSince { get; private set; }
    public DateTimeOffset? LastDisconnectAt { get; private set; }

    /// <summary>Why the adapter of a WiFi link that was in use is missing, while it is (System health warns); null otherwise.</summary>
    public string? AdapterMissing { get; private set; }

    /// <summary>The monitor told the WiFi link lost (a drop, or its adapter gone), and it is not back.</summary>
    public bool WifiLinkLost { get; private set; }

    /// <summary>
    /// The network cables up at the last sample, empty when none is; null until the monitor has read them (it is off, or
    /// this server does not run the jobs), when System health judges the WiFi alone, as before cables were read.
    /// </summary>
    public IReadOnlyList<NetworkLink>? Cables { get; private set; }

    /// <summary>
    /// The network cables up during this run that the monitor told lost and that are not back, each with its last addresses
    /// (<see cref="CableEventDetector.Lost"/>).
    /// </summary>
    public IReadOnlyList<LostCable> LostCables { get; private set; } = [];

    /// <summary>A cable up during this run is lost and not back (<see cref="LostCables"/>).</summary>
    public bool CableLost => LostCables.Count > 0;

    /// <summary>When a cable was last lost, during this run.</summary>
    public DateTimeOffset? LastCableDropAt { get; private set; }

    public IReadOnlyList<WifiEvent> RecentEvents
    {
        get
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
    }

    /// <param name="info">The WiFi sample: null when Windows shows no WiFi adapter.</param>
    /// <param name="at">When it was taken.</param>
    /// <param name="adapterMissing">Why the adapter of a WiFi link in use is missing (<see cref="WifiEventDetector.AdapterMissing"/>).</param>
    /// <param name="wifiLinkLost">The WiFi link was told lost and is not back (<see cref="WifiEventDetector.LinkLost"/>).</param>
    /// <param name="cables">The cables up now; null leaves those of the last sample (none read, before the first).</param>
    /// <param name="lostCables">The cables told lost and not back (<see cref="CableEventDetector.Lost"/>), with <paramref name="cables"/>.</param>
    internal void Update(
        WifiConnectionInfo? info, DateTimeOffset at, string? adapterMissing = null, bool wifiLinkLost = false,
        IReadOnlyList<NetworkLink>? cables = null, IReadOnlyList<LostCable>? lostCables = null)
    {
        lock (_gate)
        {
            Supported = info is not null;
            AdapterMissing = info is null ? adapterMissing : null;
            WifiLinkLost = wifiLinkLost;
            if (cables is not null)
            {
                // A copy: the detector keeps changing its own list, and System health reads this one from another thread.
                LostCable[] lost = [.. lostCables ?? []];
                if (lost.Any(l => l.LostAt > (LastCableDropAt ?? DateTimeOffset.MinValue)))
                {
                    LastCableDropAt = lost.Max(l => l.LostAt);
                }

                Cables = cables;
                LostCables = lost;
            }

            if (info is { IsConnected: true } && Current is not { IsConnected: true })
            {
                ConnectedSince = at;
            }

            if (info is not { IsConnected: true } && Current is { IsConnected: true })
            {
                LastDisconnectAt = at;
            }

            Current = info;
            LastSampleAt = at;
        }
    }

    internal void Add(WifiEvent wifiEvent)
    {
        lock (_gate)
        {
            _events.AddFirst(wifiEvent);
            while (_events.Count > 50)
            {
                _events.RemoveLast();
            }
        }
    }
}

/// <summary>
/// Monitors the dedicated machine's own links (DED-06/12). Its WiFi: drops, weak signal, access-point changes, a WiFi
/// adapter that disappears (disabled, removed, or the WLAN AutoConfig service stopped), a link that is down when the
/// server starts though it was on WiFi when it last ran, and insecure (open, WEP), shared-password (WPA-Personal) or
/// unapproved networks. And its network cables, for a hospital whose machines are cabled to its LAN: each cable it was on
/// lost, and back, judged by whether the workstations reach it over that one (<see cref="CableEventDetector"/>,
/// <see cref="ServedAddresses"/>). While a cable is up, the WiFi is not what the workstations reach it
/// over, so the WiFi's drops and signal raise nothing then; what network it joins still does. Events are audited, pushed
/// to administrators in-app and, when an on-call number is configured (<c>alerts.oncallPhone</c>), sent by SMS, and by
/// WhatsApp too when <c>alerts.oncallWhatsApp</c> is on, through the outbox (delivered as soon as connectivity returns).
/// </summary>
internal sealed class WifiMonitor(
    WifiLinkState state,
    ServedAddresses served,
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<WifiMonitor> logger) : BackgroundService
{
    /// <summary>The on-call number that gets the SMS alerts (an administrator sets it in Settings).</summary>
    private const string OnCallPhoneSetting = "alerts.oncallPhone";

    /// <summary>Whether the on-call number also gets the alerts on WhatsApp (off unless set to true).</summary>
    private const string OnCallWhatsAppSetting = "alerts.oncallWhatsApp";

    /// <summary>What a notification log withdrawn when its cable came back says, in Administration's notification log.</summary>
    internal const string WithdrawnBecauseBack =
        "Withdrawn: the network cable was back before this alert went. The alert of its return says when it was lost and when it was back.";

    private readonly CableLossMessages _lossMessages = new();

    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    /// <summary>
    /// One sample of the server's links: the WiFi's events, then the cable's. <paramref name="cables"/> are the cables up
    /// now, <paramref name="current"/> the WiFi (null when Windows shows no WiFi adapter), and <paramref name="diagnose"/>
    /// says why there is no adapter, asked only when there is neither an adapter nor a cable. A WiFi loss told before this
    /// sample that a cable up now ends is told over by the cable: the WiFi detector ends it without a word on a cable.
    /// <paramref name="servedRecently"/> says whether the workstations' requests came lately to given addresses of the server.
    /// </summary>
    internal static List<WifiEvent> SampleLinks(
        WifiEventDetector detector, CableEventDetector cableDetector, WifiConnectionInfo? previous, WifiConnectionInfo? current,
        IReadOnlyList<NetworkLink> cables, DateTimeOffset now, NetworkOptions network, Func<WifiAdapterAbsence> diagnose,
        Func<IReadOnlyList<string>, bool>? servedRecently = null)
    {
        var onCable = cables.Count > 0;
        var wifiLossTold = detector.LinkLost;
        var events = detector.Evaluate(previous, current, now, network, current is null && !onCable ? diagnose() : WifiAdapterAbsence.Unknown, onCable);
        events.AddRange(cableDetector.Evaluate(cables, current, now, wifiLossTold, servedRecently));
        return events;
    }

    /// <summary>How often the link is sampled: <see cref="NetworkOptions.WifiSampleSeconds"/>, kept between 10 seconds and 10 minutes.</summary>
    internal static TimeSpan SampleInterval(NetworkOptions network) => TimeSpan.FromSeconds(Math.Clamp(network.WifiSampleSeconds, 10, 600));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var network = options.Value.Network;
        if (!network.EnableWifiMonitor || !options.Value.RunsJobs)
        {
            return;
        }

        var interval = SampleInterval(network);
        var detector = await DetectorAsync(stoppingToken);
        var cableDetector = new CableEventDetector();
        WifiConnectionInfo? previous = null;

        // Every interval, a cabled server as much as one on WiFi: a cable pulled for a minute is a drop as a WiFi one is.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            try
            {
                var cables = NetworkLinks.Cables(NetworkLinks.Read());
                var current = WlanClient.GetPrimaryConnection();
                var events = SampleLinks(detector, cableDetector, previous, current, cables, now, network, WlanService.Diagnose,
                    served.Recently);
                state.Update(current, now, detector.AdapterMissing, detector.LinkLost, cables, cableDetector.Lost);
                foreach (var linkEvent in events)
                {
                    state.Add(linkEvent);
                }

                await PersistAsync(current, now, events, stoppingToken);
                previous = current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Network link monitoring sample failed");
            }

            await Task.Delay(interval, time, stoppingToken);
        }
    }

    /// <summary>
    /// The detector for this run, told whether the server was on WiFi when it last ran: its last sample, kept 30 days. A
    /// link or an adapter missing now is then a drop, which the first sample reports.
    /// </summary>
    private async Task<WifiEventDetector> DetectorAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var last = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<WifiSample>()
                .OrderByDescending(s => s.At)
                .Select(s => new { s.Connected, s.Ssid })
                .FirstOrDefaultAsync(ct);
            return new WifiEventDetector(last?.Connected == true, last?.Ssid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The WiFi link of the last run could not be read; a link down at start-up is not reported");
            return new WifiEventDetector();
        }
    }

    /// <summary>
    /// Keeps the WiFi sample (none when Windows shows no WiFi adapter) and records and alerts the events. When the adapter
    /// of a link in use disappears, a sample of no link is kept in its place, so that a start-up while it is still missing
    /// does not report it again. A cable's event keeps no WiFi sample: on a server without WiFi it would chart a WiFi that
    /// is not there.
    /// </summary>
    private async Task PersistAsync(WifiConnectionInfo? current, DateTimeOffset now, List<WifiEvent> events, CancellationToken ct)
    {
        if (current is null && events.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

        if (current is not null || events.Exists(e => !CableEventDetector.IsCable(e.Kind)))
        {
            db.Add(current is null
                ? new WifiSample { At = now, Connected = false }
                : new WifiSample
                {
                    At = now,
                    Connected = current.IsConnected,
                    Ssid = current.Ssid,
                    Bssid = current.Bssid,
                    SignalQuality = current.SignalQuality,
                    RssiDbm = current.RssiDbm,
                    ReceiveRateMbps = current.ReceiveRateMbps,
                    TransmitRateMbps = current.TransmitRateMbps,
                    Security = current.Security.ToString(),
                });
        }

        if (events.Count > 0)
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var onCall = await OnCallAsync(scope.ServiceProvider.GetRequiredService<ISettingsService>(), ct);
            await AlertAsync(events, audit, notifications, onCall, Environment.MachineName, _lossMessages,
                (ids, token) => NotificationLogs.WithdrawAsync(db, ids, WithdrawnBecauseBack, token), ct);
        }

        await db.SaveChangesAsync(ct);

        if (now - _lastCleanup > TimeSpan.FromHours(24))
        {
            _lastCleanup = now;
            var cutoff = now.AddDays(-30);
            await db.Set<WifiSample>().Where(s => s.At < cutoff).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// Where the on-call alerts go, read so that a value of the wrong kind cannot stop the alert itself (the settings are
    /// free JSON): a number typed without quotes is the phone number all the same, and anything but true (or "true")
    /// leaves WhatsApp off. A value that did not read as text used to fail the sample, with its alerts and audit entries.
    /// </summary>
    internal static async Task<OnCallAlerts> OnCallAsync(ISettingsService settings, CancellationToken ct)
    {
        var phone = await settings.GetAsync<JsonElement?>(OnCallPhoneSetting, cancellationToken: ct) switch
        {
            { ValueKind: JsonValueKind.String } text => text.GetString(),
            { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
            _ => null,
        };
        var whatsApp = await settings.GetAsync<JsonElement?>(OnCallWhatsAppSetting, cancellationToken: ct) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.String } text => string.Equals(text.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        return new OnCallAlerts(phone, whatsApp);
    }

    /// <summary>
    /// Logs each event in the audit trail and alerts on it: administrators (those who may see System health) in-app, for
    /// every event but a roam to another access point; and, for a warning or worse (a drop, a missing adapter, a weak
    /// signal, an insecure, shared-password or unapproved network, the network cable lost) and for a network cable back,
    /// the IT on-call number by SMS when one is set, and by WhatsApp too when the hospital turned that on. The messages are
    /// queued in the outbox, with the audit entries and the sample, so they go as soon as the internet is back, for 6 hours.
    /// A cable's return first withdraws the messages of its loss not sent yet (<see cref="CableLossMessages"/>): its own
    /// says when it was lost and when it was back.
    /// </summary>
    /// <param name="lossMessages">The messages that told each cable's loss, kept from one sample to the next.</param>
    /// <param name="withdraw">Withdraws those of the given notification log ids not sent yet, in the caller's save.</param>
    internal static async Task AlertAsync(
        IReadOnlyList<WifiEvent> events, IAuditLogger audit, INotificationService notifications, OnCallAlerts onCall, string server,
        CableLossMessages lossMessages, Func<IReadOnlyList<Guid>, CancellationToken, Task> withdraw, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lossMessages);
        ArgumentNullException.ThrowIfNull(withdraw);
        foreach (var wifiEvent in events)
        {
            var severity = wifiEvent.Kind switch
            {
                WifiEventKind.Disconnected or WifiEventKind.AdapterLost or WifiEventKind.InsecureNetwork or WifiEventKind.UnapprovedNetwork
                    or WifiEventKind.CableDisconnected => AuditSeverity.Critical,
                WifiEventKind.WeakSignal or WifiEventKind.SharedPasswordNetwork or WifiEventKind.OtherCableDisconnected => AuditSeverity.Warning,
                _ => AuditSeverity.Info,
            };
            var category = wifiEvent.Kind is WifiEventKind.InsecureNetwork or WifiEventKind.SharedPasswordNetwork or WifiEventKind.UnapprovedNetwork
                ? AuditCategories.Security
                : AuditCategories.Admin;
            var (action, title) = wifiEvent.Kind switch
            {
                WifiEventKind.CableDisconnected or WifiEventKind.OtherCableDisconnected => ("network.cable_disconnected", "Server network cable: Disconnected"),
                WifiEventKind.CableReconnected => ("network.cable_reconnected", "Server network cable: Connected"),
                _ => ($"network.wifi_{wifiEvent.Kind.ToString().ToLowerInvariant()}", "Server WiFi: " + wifiEvent.Kind),
            };
            audit.Add(new AuditEntry(category, action, wifiEvent.Message) { Severity = severity });

            if (wifiEvent.Kind is not WifiEventKind.AccessPointChanged)
            {
                await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, title, wifiEvent.Message,
                    "/admin/system", "system", severity == AuditSeverity.Critical ? "critical" : severity == AuditSeverity.Warning ? "warning" : "info", ct);
            }

            var cableBack = wifiEvent.Kind is WifiEventKind.CableReconnected;
            if ((severity >= AuditSeverity.Warning || cableBack) && !string.IsNullOrWhiteSpace(onCall.Phone))
            {
                if (cableBack && lossMessages.Back(wifiEvent.Cables) is { Count: > 0 } told)
                {
                    await withdraw(told, ct);
                }

                var sent = notifications.Queue("system.alert", new NotificationRecipient(Phone.Normalize(onCall.Phone), null, null, "IT on-call"),
                    new Dictionary<string, string> { ["server"] = server, ["message"] = wifiEvent.Message },
                    onCall.WhatsApp ? [NotificationChannel.Sms, NotificationChannel.WhatsApp] : [NotificationChannel.Sms], TimeSpan.FromHours(6));
                if (wifiEvent.Kind is WifiEventKind.CableDisconnected or WifiEventKind.OtherCableDisconnected)
                {
                    lossMessages.Lost(wifiEvent.Cables, sent);
                }
            }
        }
    }

    /// <summary>
    /// <see cref="AlertAsync(IReadOnlyList{WifiEvent}, IAuditLogger, INotificationService, OnCallAlerts, string, CableLossMessages, Func{IReadOnlyList{Guid}, CancellationToken, Task}, CancellationToken)"/>
    /// for events alerted on their own: nothing is followed from one call to the next, and nothing is withdrawn.
    /// </summary>
    internal static Task AlertAsync(
        IReadOnlyList<WifiEvent> events, IAuditLogger audit, INotificationService notifications, OnCallAlerts onCall, string server, CancellationToken ct) =>
        AlertAsync(events, audit, notifications, onCall, server, new CableLossMessages(), static (_, _) => Task.CompletedTask, ct);
}

/// <summary>
/// Where the IT on-call alerts go: <c>alerts.oncallPhone</c> (none when blank) by SMS, and on WhatsApp too when
/// <c>alerts.oncallWhatsApp</c> is on (the hospital's WhatsApp provider must have the <c>system_alert</c> template approved).
/// </summary>
internal sealed record OnCallAlerts(string? Phone, bool WhatsApp);

/// <summary>
/// Why Windows shows the Server Node no WiFi adapter (<see cref="WifiAdapterAbsence"/>): the state of the WLAN AutoConfig
/// service, read through WMI as System health reads the UPS. Anything it cannot read is <see cref="WifiAdapterAbsence.Unknown"/>.
/// </summary>
internal static class WlanService
{
    public static WifiAdapterAbsence Diagnose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return WifiAdapterAbsence.Unknown;
        }

        try
        {
            return DiagnoseWindows();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or PlatformNotSupportedException)
        {
            return WifiAdapterAbsence.Unknown;
        }
    }

    /// <summary>What the WLAN AutoConfig service's state says: not installed (a server without WiFi) tells nothing.</summary>
    internal static WifiAdapterAbsence FromServiceState(string? state) => state switch
    {
        null => WifiAdapterAbsence.Unknown,
        "Running" => WifiAdapterAbsence.NoAdapter,
        _ => WifiAdapterAbsence.ServiceStopped,
    };

    [SupportedOSPlatform("windows")]
    private static WifiAdapterAbsence DiagnoseWindows()
    {
        using var searcher = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name = 'WlanSvc'");
        foreach (var item in searcher.Get())
        {
            using var service = (ManagementObject)item;
            return FromServiceState(service["State"]?.ToString());
        }

        return WifiAdapterAbsence.Unknown;
    }
}

/// <summary>
/// Decides which events a new sample of the Server Node's WiFi link raises (DED-06/12), remembering what it has already
/// reported so that one episode is reported once: a drop when the link goes down and a reconnection when it comes back; a
/// WiFi adapter that disappears while the link was in use; at the first sample since the server started, a link or an
/// adapter missing though the server was on WiFi when it last ran; a weak signal once it has stayed below
/// <see cref="NetworkOptions.WifiWeakSignalThreshold"/> for <see cref="WeakStreakThreshold"/> samples in a row, and its
/// recovery once it is back at the threshold or above; a roam to another access point; and an insecure, shared-password
/// or unapproved network. While a network cable is up, the workstations reach the server over the cable, so the WiFi's
/// coming and going and its signal raise nothing, and a loss already told is over; what network it joins still does.
/// </summary>
/// <param name="onWifiWhenLastRun">The server's last sample before this run (kept 30 days) had a link.</param>
/// <param name="lastSsid">That sample's network, for the message.</param>
internal sealed class WifiEventDetector(bool onWifiWhenLastRun = false, string? lastSsid = null)
{
    /// <summary>Weak samples in a row before the signal is reported weak: a minute below the threshold, sampled every 30 s.</summary>
    internal const int WeakStreakThreshold = 3;

    /// <summary>
    /// The warning lasts until the WiFi is back, or a network cable is up: a server moved to a cable on purpose clears it
    /// by connecting it.
    /// </summary>
    private const string WiredOnPurpose = " If Server Node now runs on a network cable on purpose, this warning clears once the cable is connected.";

    private bool _started;
    private int _weakStreak;
    private bool _weakAlerted;
    private string? _lastInsecureAlertSsid;
    private string? _lastSharedPasswordAlertSsid;

    /// <summary>A drop, or the loss of the adapter, has been reported, and the link is not back yet.</summary>
    public bool LinkLost { get; private set; }

    /// <summary>Why the adapter of the link that was lost is missing, while it is; null otherwise.</summary>
    public string? AdapterMissing { get; private set; }

    /// <param name="previous">The sample before (null when it had no adapter), or null for the first since the server started.</param>
    /// <param name="current">This sample, or null when Windows shows no WiFi adapter.</param>
    /// <param name="absence">Why there is no adapter, when <paramref name="current"/> is null.</param>
    /// <param name="onCable">A network cable is up (<see cref="NetworkLinks.Cables"/>): the WiFi is not the server's link.</param>
    public List<WifiEvent> Evaluate(
        WifiConnectionInfo? previous, WifiConnectionInfo? current, DateTimeOffset now, NetworkOptions network,
        WifiAdapterAbsence absence = WifiAdapterAbsence.Unknown, bool onCable = false)
    {
        var events = new List<WifiEvent>();
        var starting = !_started;
        _started = true;

        if (onCable)
        {
            LinkLost = false;
            AdapterMissing = null;
            _weakStreak = 0;
            _weakAlerted = false;
            if (current is { IsConnected: true, DetailsAvailable: true })
            {
                NetworkEvents(previous, current, now, network, events);
            }

            return events;
        }

        // Whether the link was up just before: at the sample before, or, at the first sample, when the server last ran.
        var wasUp = starting ? onWifiWhenLastRun : previous is { IsConnected: true };

        if (current is null)
        {
            if (wasUp)
            {
                LinkLost = true;
                events.Add(new WifiEvent(now, WifiEventKind.AdapterLost, AdapterLostMessage(starting, absence)));
            }

            AdapterMissing = LinkLost ? AdapterLostMessage(false, absence) + WiredOnPurpose : null;
            return events;
        }

        AdapterMissing = null;
        if (!current.IsConnected)
        {
            if (wasUp)
            {
                LinkLost = true;
                events.Add(new WifiEvent(now, WifiEventKind.Disconnected, starting
                    ? $"Server Node started without its WiFi connection{(lastSsid is null ? string.Empty : $" (on '{lastSsid}' when it last ran)")}. Workstations cannot reach HC360 until it connects."
                    : "Server Node lost its WiFi connection. Workstations cannot reach HC360 until it reconnects."));
            }
        }
        else
        {
            // Back from a drop, or with its adapter back after the loss was reported.
            if (!starting && (previous is { IsConnected: false } || (previous is null && LinkLost)))
            {
                events.Add(new WifiEvent(now, WifiEventKind.Reconnected, $"Server Node reconnected to WiFi{(current.Ssid is null ? string.Empty : $" '{current.Ssid}'")}."));
            }

            LinkLost = false;
        }

        if (current is { IsConnected: true, DetailsAvailable: true })
        {
            if (current.SignalQuality < network.WifiWeakSignalThreshold)
            {
                _weakStreak++;
                if (_weakStreak >= WeakStreakThreshold && !_weakAlerted)
                {
                    _weakAlerted = true;
                    events.Add(new WifiEvent(now, WifiEventKind.WeakSignal,
                        $"Server Node WiFi signal is weak ({current.SignalQuality}%). Move the access point closer or connect Ethernet."));
                }
            }
            else
            {
                if (_weakAlerted)
                {
                    events.Add(new WifiEvent(now, WifiEventKind.SignalRecovered, $"Server Node WiFi signal recovered ({current.SignalQuality}%)."));
                }

                _weakStreak = 0;
                _weakAlerted = false;
            }

            NetworkEvents(previous, current, now, network, events);
        }

        return events;
    }

    /// <summary>What the network the WiFi is on raises: a roam to another access point, and an insecure, shared-password or unapproved network.</summary>
    private void NetworkEvents(WifiConnectionInfo? previous, WifiConnectionInfo current, DateTimeOffset now, NetworkOptions network, List<WifiEvent> events)
    {
        if (previous is { IsConnected: true, Bssid: not null } && current.Bssid is not null && previous.Bssid != current.Bssid)
        {
            events.Add(new WifiEvent(now, WifiEventKind.AccessPointChanged, $"Server Node roamed to access point {current.Bssid}."));
        }

        if (current.IsInsecure && _lastInsecureAlertSsid != current.Ssid)
        {
            _lastInsecureAlertSsid = current.Ssid;
            events.Add(new WifiEvent(now, WifiEventKind.InsecureNetwork,
                $"Server Node is connected to an insecure WiFi network ({current.Security}). Use WPA2/WPA3-Enterprise for clinical data."));
        }
        else if (current.UsesSharedPassword && _lastSharedPasswordAlertSsid != current.Ssid)
        {
            _lastSharedPasswordAlertSsid = current.Ssid;
            events.Add(new WifiEvent(now, WifiEventKind.SharedPasswordNetwork,
                $"Server Node is on a WiFi network with a shared password ({current.Security}): anyone who has the password can join it or imitate it. Use WPA2/WPA3-Enterprise for clinical data."));
        }

        if (network.ApprovedSsids.Length > 0 && current.Ssid is not null
            && !network.ApprovedSsids.Contains(current.Ssid, StringComparer.Ordinal))
        {
            events.Add(new WifiEvent(now, WifiEventKind.UnapprovedNetwork, $"Server Node joined an unapproved WiFi network '{current.Ssid}'."));
        }
    }

    /// <summary>What the on-call engineer is to do: start the service, or enable the adapter.</summary>
    private static string AdapterLostMessage(bool starting, WifiAdapterAbsence absence) => absence switch
    {
        WifiAdapterAbsence.ServiceStopped => $"Server Node {(starting ? "started with" : "has")} no WiFi: the WLAN AutoConfig service is not running. Start it in Services. Workstations cannot reach HC360 over WiFi until then.",
        WifiAdapterAbsence.NoAdapter => $"Server Node {(starting ? "started without" : "lost")} its WiFi adapter: Windows shows none (disabled or removed). Enable it in Network Connections. Workstations cannot reach HC360 over WiFi until then.",
        _ => $"Server Node {(starting ? "started without" : "lost")} its WiFi adapter (disabled or removed, or the WLAN AutoConfig service stopped). Workstations cannot reach HC360 over WiFi until it is back.",
    };
}

/// <summary>
/// Decides which events a new sample of the Server Node's network cables raises (DED-06, where the hospital's machines are
/// cabled to its LAN). Each cable up during this run is followed on its own, so that on a server with two cards (the hospital
/// LAN and an imaging network, say) each is told. A cable lost while no other is up is critical: it is out or its switch is
/// off, and the message says whether the server is still on WiFi. A cable lost while another is up is critical when the
/// workstations' requests came over it lately (<see cref="ServedAddresses"/>), and a warning otherwise, named with its last
/// address and what is still up. Each is told back once. A server that starts with no cable up raises nothing here: it may
/// simply have started before its switch, and System health tells a server with no link at all (<c>network.down</c>) once
/// that has lasted.
/// </summary>
internal sealed class CableEventDetector
{
    /// <summary>Each cable up during this run, as it last was up: its addresses go when it goes down.</summary>
    private readonly Dictionary<string, NetworkLink> _known = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<LostCable> _lost = [];

    /// <summary>The cables up during this run that were told lost and are not back, in the order they were lost.</summary>
    public IReadOnlyList<LostCable> Lost => _lost;

    /// <summary>A cable up during this run was told lost and is not back.</summary>
    public bool CableLost => _lost.Count > 0;

    /// <summary>Whether <paramref name="kind"/> is a cable's event rather than the WiFi's.</summary>
    public static bool IsCable(WifiEventKind kind) =>
        kind is WifiEventKind.CableDisconnected or WifiEventKind.OtherCableDisconnected or WifiEventKind.CableReconnected;

    /// <summary>A link's speed as people say it: 100 Mbps, 1 Gbps, 2.5 Gbps.</summary>
    public static string Speed(long mbps) =>
        mbps >= 1000 ? (mbps / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Gbps" : mbps.ToString(CultureInfo.InvariantCulture) + " Mbps";

    /// <param name="cables">The cables up now (<see cref="NetworkLinks.Cables"/>).</param>
    /// <param name="wifi">The WiFi at the same sample, for the message: null when Windows shows no WiFi adapter.</param>
    /// <param name="now">When the sample was taken.</param>
    /// <param name="wifiLossTold">
    /// A loss of the WiFi was told before this sample and has not ended: a cable up now ends it, and says so.
    /// </param>
    /// <param name="servedRecently">
    /// Whether the workstations' requests came lately to any of the given addresses of the server
    /// (<see cref="ServedAddresses.Recently"/>); null when there is nothing to go by.
    /// </param>
    public List<WifiEvent> Evaluate(
        IReadOnlyList<NetworkLink> cables, WifiConnectionInfo? wifi, DateTimeOffset now, bool wifiLossTold = false,
        Func<IReadOnlyList<string>, bool>? servedRecently = null)
    {
        var events = new List<WifiEvent>();
        var up = cables.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var noneWasUp = _known.Count > 0 && _known.Keys.All(IsLost);

        var back = cables.Where(c => IsLost(c.Name)).ToList();
        if (back.Count > 0)
        {
            var returned = _lost.Where(l => up.Contains(l.Name)).ToList();
            _lost.RemoveAll(l => up.Contains(l.Name));
            // The workstations' cable, or the server's only way onto the network: its return is theirs too.
            var reachAgain = returned.Exists(l => l.ServedWorkstations) || (noneWasUp && wifi is not { IsConnected: true });
            var said = back.Count == 1
                ? $"Server Node's network cable is connected again ({Described(back[0])})"
                : $"Server Node's network cables are connected again ({string.Join("; ", back.Select(Described))})";
            events.Add(new WifiEvent(now, WifiEventKind.CableReconnected,
                $"{said}{(reachAgain ? ": workstations can reach HC360 again" : string.Empty)}. {When(returned.Min(l => l.LostAt), now)}")
            {
                Cables = [.. back.Select(c => c.Name)],
            });
        }
        else if (cables.Count > 0 && (noneWasUp || wifiLossTold))
        {
            // Back on the network over a cable that was not up before (moved to the card's other port, say): the ports it
            // left are not waited for.
            List<LostCable> left = noneWasUp ? [.. _lost] : [];
            foreach (var cable in left)
            {
                _known.Remove(cable.Name);
            }

            if (noneWasUp)
            {
                _lost.Clear();
            }

            events.Add(new WifiEvent(now, WifiEventKind.CableReconnected,
                $"Server Node is back on the network, on a network cable ({Described(cables[0])}): workstations can reach HC360 again. "
                + When(left.Count > 0 ? left.Min(l => l.LostAt) : null, now))
            {
                Cables = [.. left.Select(l => l.Name)],
            });
        }

        var newlyLost = _known.Values.Where(k => !up.Contains(k.Name) && !IsLost(k.Name)).OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var cable in cables)
        {
            _known[cable.Name] = cable;
        }

        if (newlyLost.Count == 0)
        {
            return events;
        }

        var lost = newlyLost.Select(k => new LostCable(k.Name, k.Addresses, now, servedRecently?.Invoke(k.Addresses) ?? false)).ToList();
        _lost.AddRange(lost);
        if (cables.Count == 0)
        {
            events.Add(new WifiEvent(now, WifiEventKind.CableDisconnected, NoCableLeft(lost, wifi)) { Cables = [.. lost.Select(l => l.Name)] });
            return events;
        }

        var used = servedRecently is null ? [] : cables.Where(c => servedRecently(c.Addresses)).ToList();
        foreach (var cable in lost)
        {
            var named = Named(cable.Name, cable.Addresses);
            var stillOn = string.Join(", ", cables.Select(c => Named(c.Name, c.Addresses)));
            var told = cable.ServedWorkstations
                ? new WifiEvent(now, WifiEventKind.CableDisconnected,
                    $"Server Node lost its network cable {named}, which workstations reach it over: they cannot reach HC360 until it is back. It is still on {stillOn}.")
                : new WifiEvent(now, WifiEventKind.OtherCableDisconnected, used.Count > 0
                    ? $"Server Node lost its network cable {named}. Workstations reach it over {string.Join(", ", used.Select(c => $"'{c.Name}'"))}, "
                      + $"which {(used.Count == 1 ? "is" : "are")} up; what used '{cable.Name}' cannot reach it until it is back."
                    : $"Server Node lost its network cable {named}. It is still on {stillOn}.");
            events.Add(told with { Cables = [cable.Name] });
        }

        return events;
    }

    /// <summary>What it means that no cable is up now: the cables just lost, and whether the server is on WiFi.</summary>
    private string NoCableLeft(List<LostCable> lost, WifiConnectionInfo? wifi)
    {
        var onWifi = wifi is { IsConnected: true } ? $"WiFi{(wifi.Ssid is null ? string.Empty : $" '{wifi.Ssid}'")} only" : null;
        if (_lost.Count == 1)
        {
            var only = $"Server Node lost its network cable ('{lost[0].Name}'): the cable is out or its switch is off.";
            return onWifi is null ? $"{only} Workstations cannot reach HC360 until it is back." : $"{only} It is on {onWifi}.";
        }

        var said = lost.Count == 1
            ? $"Server Node lost its network cable ('{lost[0].Name}'): the cable is out or its switch is off."
            : $"Server Node lost its network cables ({string.Join(", ", lost.Select(l => $"'{l.Name}'"))}): the cables are out or their switch is off.";
        return onWifi is null
            ? $"{said} No cable is up now: workstations cannot reach HC360 until one is back."
            : $"{said} No cable is up now: it is on {onWifi}.";
    }

    private bool IsLost(string name) => _lost.Exists(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Described(NetworkLink cable) =>
        $"'{cable.Name}'{(cable.SpeedMbps is { } mbps ? $", {Speed(mbps)}" : string.Empty)}";

    private static string Named(string name, IReadOnlyList<string> addresses) =>
        addresses.Count == 0 ? $"'{name}'" : $"'{name}' ({string.Join(", ", addresses)})";

    /// <summary>
    /// When the cables back were lost and when they were back, in the hospital's time: "Lost at 09:31, back at 09:32.", with
    /// the dates when the loss was on another day. The SMS of a return may come late, and before or after the loss's (the
    /// phone network keeps no order); with the times it reads right either way.
    /// </summary>
    private static string When(DateTimeOffset? lostAt, DateTimeOffset backAt)
    {
        var back = TimeZoneInfo.ConvertTime(backAt, HospitalTimeZone.Zone);
        if (lostAt is not { } at)
        {
            return $"Back at {back.ToString("HH:mm", CultureInfo.InvariantCulture)}.";
        }

        var lost = TimeZoneInfo.ConvertTime(at, HospitalTimeZone.Zone);
        var format = lost.Date == back.Date ? "HH:mm" : "d MMM HH:mm";
        return $"Lost at {lost.ToString(format, CultureInfo.InvariantCulture)}, back at {back.ToString(format, CultureInfo.InvariantCulture)}.";
    }
}

/// <summary>
/// The on-call messages that told each cable's loss (DED-06), so that the alert of its return can withdraw those not sent yet.
/// A server that reaches the SMS gateway over that cable alone cannot send its loss while it is out: the outbox tries it
/// again at 10, 20, 40, 80 seconds and on, and the return, sent at once, would reach IT's phone first, leaving "lost" as
/// the last word. Withdrawn, the loss is told by the return itself ("Lost at 09:31, back at 09:32").
/// </summary>
internal sealed class CableLossMessages
{
    private readonly List<(HashSet<string> Cables, IReadOnlyList<Guid> Messages)> _told = [];

    /// <summary>The loss of <paramref name="cables"/> was told in <paramref name="messages"/> (notification log ids).</summary>
    public void Lost(IReadOnlyList<string> cables, IReadOnlyList<Guid> messages)
    {
        if (cables.Count > 0 && messages.Count > 0)
        {
            _told.Add((cables.ToHashSet(StringComparer.OrdinalIgnoreCase), messages));
        }
    }

    /// <summary>
    /// <paramref name="cables"/> are back: the messages that told the loss of cables now all back, no longer followed here.
    /// One that also told a cable still out is kept, and still goes.
    /// </summary>
    public List<Guid> Back(IReadOnlyList<string> cables)
    {
        var settled = new List<Guid>();
        foreach (var told in _told.ToList())
        {
            told.Cables.ExceptWith(cables);
            if (told.Cables.Count == 0)
            {
                settled.AddRange(told.Messages);
                _told.Remove(told);
            }
        }

        return settled;
    }
}
