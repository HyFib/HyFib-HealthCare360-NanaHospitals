using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Logging;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>Where the anchors are copied, and how that went: <see cref="FailingSince"/> is null while the last copy worked.</summary>
public sealed record AnchorMirrorTarget(string Name, DateTimeOffset? LastCopiedAt, DateTimeOffset? FailingSince, string? Error);

/// <summary>
/// What the copies off the server say. <see cref="Mismatch"/> is set when the copy in <see cref="AuditOptions.AnchorMirrorFile"/>
/// holds an anchor this server's anchor file no longer has, or has with another hash: the anchor file was changed here.
/// </summary>
public sealed record AnchorMirrorStatus(bool Mirrored, IReadOnlyList<AnchorMirrorTarget> Targets, string? Mismatch);

/// <summary>
/// Copies each audit anchor (<see cref="AuditAnchors"/>) off the server as it is written: to a second anchor file on another
/// machine (<see cref="AuditOptions.AnchorMirrorFile"/>, a UNC path) and to the syslog collector
/// (<see cref="AuditOptions.AnchorsToSyslog"/>). The anchor file on the server's own disk proves where the chain ended only
/// to someone who trusts that disk; whoever can rewrite the database and the server's files could rewrite both. A copy on a
/// machine they do not hold cannot be (CERT-In Directions of 28 April 2022; SRS SEC-09).
/// <para>
/// The second file is brought level with the anchor file each time (every anchor it lacks is appended, in order), so one
/// missed while the share was away is copied once it is back. Each anchor goes to the collector once; one that did not reach
/// it is sent again with the next attempt. System health warns while either fails (<see cref="AuditAnchorMirrorWarnings"/>),
/// and says so loudly when the second file disagrees with the first.
/// </para>
/// </summary>
public sealed class AuditAnchorMirror
{
    /// <summary>How long a copy may fail before System health's warning turns critical.</summary>
    public static readonly TimeSpan CriticalAfter = TimeSpan.FromHours(24);

    internal const string SyslogName = "syslog";

    private readonly DataDirectories _directories;
    private readonly AuditOptions _audit;
    private readonly SyslogClient? _syslog;
    private readonly TimeProvider _time;
    private readonly Lock _state = new();
    private readonly SemaphoreSlim _written = new(0, 1);
    private readonly List<AuditAnchor> _toSyslog = [];
    private readonly Dictionary<string, AnchorMirrorTarget> _targets = new(StringComparer.Ordinal);
    private string? _mismatch;

    public AuditAnchorMirror(DataDirectories directories, IOptions<PlatformOptions> options, TimeProvider time, SyslogClient? syslog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directories = directories;
        _audit = options.Value.Audit;
        _syslog = syslog;
        _time = time;
        MirrorFile = string.IsNullOrWhiteSpace(_audit.AnchorMirrorFile)
            ? null
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(_audit.AnchorMirrorFile));
        if (MirrorFile is not null)
        {
            _targets[MirrorFile] = new AnchorMirrorTarget(MirrorFile, null, null, null);
        }

        if (ToSyslog)
        {
            _targets[SyslogName] = new AnchorMirrorTarget(SyslogName, null, null, null);
        }
    }

    /// <summary>The second anchor file, in full; null when there is none.</summary>
    public string? MirrorFile { get; }

    public bool IsMirrored => MirrorFile is not null || ToSyslog;

    private bool ToSyslog => _audit.AnchorsToSyslog && _syslog is { IsEnabled: true };

    public AnchorMirrorStatus Status
    {
        get
        {
            lock (_state)
            {
                return new AnchorMirrorStatus(IsMirrored, [.. _targets.Values], _mismatch);
            }
        }
    }

    /// <summary>An anchor was just appended to the anchor file: it is copied at once (<see cref="WaitForWrittenAsync"/>).</summary>
    internal void Written(AuditAnchor anchor)
    {
        if (!IsMirrored)
        {
            return;
        }

        if (ToSyslog)
        {
            lock (_state)
            {
                _toSyslog.Add(anchor);
            }
        }

        if (_written.CurrentCount == 0)
        {
            try
            {
                _written.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <summary>Waits until an anchor is written, or <paramref name="timeout"/> passes; true when one was.</summary>
    internal Task<bool> WaitForWrittenAsync(TimeSpan timeout, CancellationToken ct) => _written.WaitAsync(timeout, ct);

    /// <summary>
    /// The anchor last written, sent to the collector again: a node that starts does not know what reached it before, and
    /// one more copy of an anchor does no harm.
    /// </summary>
    internal void SendLastAgain()
    {
        if (ToSyslog && AuditAnchors.ReadFile(_directories.AuditAnchors).Anchors.LastOrDefault() is { } last)
        {
            lock (_state)
            {
                if (!_toSyslog.Contains(last))
                {
                    _toSyslog.Insert(0, last);
                }
            }
        }
    }

    /// <summary>
    /// Brings every copy level with the anchor file: appends to the second file each anchor it lacks, and sends the
    /// collector each anchor it has not had. A copy that fails is recorded for System health, and tried again next time;
    /// this does not throw for it.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (MirrorFile is { } mirror)
        {
            try
            {
                CopyToFile(mirror);
                Succeeded(mirror);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
            {
                Failed(mirror, ex.Message);
            }
        }

        if (ToSyslog)
        {
            await SendToSyslogAsync(ct);
        }
    }

    private void CopyToFile(string mirror)
    {
        var (anchors, _) = AuditAnchors.ReadFile(_directories.AuditAnchors);
        var (copied, _) = AuditAnchors.ReadFile(mirror);

        // The copy holds what the anchor file once had: an anchor it lacks now, or holds with another hash, was changed here.
        var here = anchors.GroupBy(a => a.ChainIndex).ToDictionary(g => g.Key, g => g.Select(a => a.Hash).ToHashSet(StringComparer.Ordinal));
        var changed = copied.FirstOrDefault(a => !here.TryGetValue(a.ChainIndex, out var hashes) || !hashes.Contains(a.Hash));
        lock (_state)
        {
            _mismatch = changed is null
                ? null
                : string.Create(CultureInfo.InvariantCulture,
                    $"The copy of the audit anchors in {mirror} holds an anchor at chain position {changed.ChainIndex:N0} (hash {changed.Hash}, anchored {changed.AnchoredAt:yyyy-MM-dd HH:mm} UTC) that this server's anchor file {(here.ContainsKey(changed.ChainIndex) ? "holds with another hash" : "no longer has")}.");
        }

        // A restore's anchor names a position, with its hash, that may be anchored already: it is copied all the same, so the
        // copy off the server shows the restore that sets the anchors after it aside (AuditRestores).
        var have = copied.Select(a => (a.ChainIndex, a.Hash, a.Restore is not null)).ToHashSet();
        var missing = anchors.Where(a => have.Add((a.ChainIndex, a.Hash, a.Restore is not null))).ToList();
        if (missing.Count > 0)
        {
            AuditAnchors.AppendTo(mirror, missing);
        }
    }

    private async Task SendToSyslogAsync(CancellationToken ct)
    {
        List<AuditAnchor> pending;
        lock (_state)
        {
            pending = [.. _toSyslog];
        }

        foreach (var anchor in pending)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await _syslog!.SendAsync(Message(anchor), timeout.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Failed(SyslogName, ex is OperationCanceledException ? "The collector did not take the anchor within 30 seconds." : _syslog!.Status.LastError ?? ex.Message);
                return;
            }

            lock (_state)
            {
                _toSyslog.Remove(anchor);
            }

            Succeeded(SyslogName);
        }
    }

    /// <summary>An anchor as the collector gets it: facility "log audit", MSGID AUDIT_ANCHOR, the anchor in the structured data.</summary>
    internal static SyslogEntry Message(AuditAnchor anchor) => new(
        anchor.AnchoredAt,
        SyslogSeverity.Notice,
        "AUDIT_ANCHOR",
        string.Create(CultureInfo.InvariantCulture, $"Audit chain anchored at position {anchor.ChainIndex}: hash {anchor.Hash}"),
        [
            new("chainIndex", anchor.ChainIndex.ToString(CultureInfo.InvariantCulture)),
            new("hash", anchor.Hash),
            new("mac", anchor.Mac),
            new("anchoredAt", SyslogFormat.Timestamp(anchor.AnchoredAt)),
        ],
        SyslogFormat.LogAuditFacility);

    private void Succeeded(string name)
    {
        lock (_state)
        {
            _targets[name] = new AnchorMirrorTarget(name, _time.GetUtcNow(), null, null);
        }
    }

    private void Failed(string name, string error)
    {
        lock (_state)
        {
            var before = _targets.GetValueOrDefault(name);
            _targets[name] = new AnchorMirrorTarget(name, before?.LastCopiedAt, before?.FailingSince ?? _time.GetUtcNow(), SensitiveText.Redact(error));
        }
    }
}

/// <summary>
/// Keeps the copies of the anchors level (<see cref="AuditAnchorMirror"/>) on every server: an anchor may be written by the
/// node that runs the jobs (the sealer) or by the API node that makes an approved purge. It copies at once when one is
/// written, every five minutes otherwise, and every minute while a copy fails.
/// </summary>
internal sealed class AuditAnchorMirrorService(AuditAnchorMirror mirror, IOptions<PlatformOptions> options) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WhileFailing = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!mirror.IsMirrored)
        {
            return;
        }

        if (options.Value.RunsJobs)
        {
            mirror.SendLastAgain();
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await mirror.SyncAsync(stoppingToken);
                var failing = mirror.Status.Targets.Any(t => t.FailingSince is not null);
                await mirror.WaitForWrittenAsync(failing ? WhileFailing : Every, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// System health's warnings about the copies of the audit anchors off the server: one that fails (critical after a day),
/// a copy that disagrees with the anchor file (critical), and, on a live server, none at all (info).
/// </summary>
internal sealed class AuditAnchorMirrorWarnings(AuditAnchorMirror mirror, IOptions<PlatformOptions> options, TimeProvider time) : IHealthWarningSource
{
    public Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Warnings(mirror.Status, options.Value, time.GetUtcNow()));

    internal static IReadOnlyList<HealthWarning> Warnings(AnchorMirrorStatus status, PlatformOptions options, DateTimeOffset now)
    {
        if (!status.Mirrored)
        {
            return options.SeedDemoData || !options.RunsJobs
                ? []
                :
                [
                    new("audit.anchor_mirror_off", "info",
                        "The audit chain's anchors are kept on this server only. Copy them to another machine: set HealthCare360:Audit:AnchorMirrorFile to a share there, or send them to the syslog collector (HealthCare360:Audit:AnchorsToSyslog)."),
                ];
        }

        var warnings = new List<HealthWarning>();
        if (status.Mismatch is { } mismatch)
        {
            warnings.Add(new("audit.anchor_mirror_mismatch", "critical",
                $"{mismatch} Someone may have changed the anchor file to hide a change to the audit trail. Keep both files as they are and verify the chain under Administration › Audit."));
        }

        foreach (var target in status.Targets.Where(t => t.FailingSince is not null))
        {
            var where = target.Name == AuditAnchorMirror.SyslogName ? "the syslog collector" : target.Name;
            var severity = now - target.FailingSince!.Value >= AuditAnchorMirror.CriticalAfter ? "critical" : "warning";
            var last = target.LastCopiedAt is { } at ? $" The last copy was made {at:yyyy-MM-dd HH:mm} UTC." : string.Empty;
            warnings.Add(new("audit.anchor_mirror_failed", severity,
                $"The audit chain's anchors cannot be copied to {where} since {target.FailingSince:yyyy-MM-dd HH:mm} UTC: {target.Error}{last} They are kept in this server's anchor file meanwhile, and copied once it answers."));
        }

        return warnings;
    }
}
