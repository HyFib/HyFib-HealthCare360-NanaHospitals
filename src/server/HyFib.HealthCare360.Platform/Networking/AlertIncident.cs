using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>Who an alert has been sent to so far, in the order it is escalated while nobody acknowledges it.</summary>
public enum AlertLevel
{
    /// <summary>IT on-call (<c>alerts.oncallPhone</c>), and everyone who may see System health, in the app.</summary>
    OnCall = 0,

    /// <summary>The second contact (<c>alerts.escalationPhone</c>), when IT on-call did not acknowledge it in time.</summary>
    SecondContact = 1,

    /// <summary>HyFib support (<c>alerts.supportPhone</c>), when the second contact did not either.</summary>
    Support = 2,
}

/// <summary>
/// A critical System health warning while it lasts (audit P2): when it was raised and told, who it was escalated to while
/// nobody acknowledged it, who acknowledged it, and when it cleared. One is open per warning and server; a warning that
/// clears and comes back is a new one, told again. Acknowledging it says someone is on it: the warning stays until its condition clears, and it is still told again a
/// day later while it lasts, but nobody further is paged.
/// </summary>
public sealed class AlertIncident : AuditableEntity
{
    public const int CodeLength = 128;
    public const int ServerLength = 128;
    public const int MessageLength = 2000;

    /// <summary>The warning's code (<see cref="HealthWarning.Code"/>).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The server the warning is about; empty when it is about the whole system.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>What the warning said when it was last told.</summary>
    public string Message { get; set; } = string.Empty;

    public DateTimeOffset RaisedAt { get; set; }

    /// <summary>When it was first told: in the app, and to IT on-call.</summary>
    public DateTimeOffset? ToldAt { get; set; }

    /// <summary>When it was last told again because it lasted a day (<see cref="SystemHealthAlerts.RemindAfter"/>).</summary>
    public DateTimeOffset? RemindedAt { get; set; }

    public AlertLevel Level { get; set; }

    public DateTimeOffset? SecondContactAt { get; set; }

    public DateTimeOffset? SupportAt { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }

    public Guid? AcknowledgedById { get; set; }

    public string? AcknowledgedBy { get; set; }

    public DateTimeOffset? ClearedAt { get; set; }

    /// <summary>The message log entries of what was sent to IT on-call, first and as reminders.</summary>
    public List<Guid> OnCallMessageIds { get; set; } = [];

    public List<Guid> SecondContactMessageIds { get; set; } = [];

    public List<Guid> SupportMessageIds { get; set; } = [];

    /// <summary>The message log entries of the level reached so far.</summary>
    public IReadOnlyList<Guid> LevelMessageIds => Level switch
    {
        AlertLevel.SecondContact => SecondContactMessageIds,
        AlertLevel.Support => SupportMessageIds,
        _ => OnCallMessageIds,
    };
}

/// <summary>What the alerts do next for an incident, decided from what it records (<see cref="AlertEscalation.Next"/>).</summary>
public enum AlertStep
{
    None,
    Tell,
    EscalateToSecondContact,
    EscalateToSupport,
    Remind,
}

/// <summary>
/// Who an alert nobody acknowledges is escalated to, and when (audit P2): IT on-call at once; the second contact once
/// <see cref="EscalateAfter"/> has passed, or as soon as nothing reached IT on-call; HyFib support once
/// <see cref="SupportAfter"/> more has passed, or nothing reached the second contact. A level nobody is set for is
/// skipped.
/// </summary>
public sealed record AlertEscalation(string? SecondContactPhone, string? SupportPhone, TimeSpan EscalateAfter, TimeSpan SupportAfter)
{
    public const string SecondContactSetting = "alerts.escalationPhone";
    public const string SupportSetting = "alerts.supportPhone";
    public const string EscalateMinutesSetting = "alerts.escalateMinutes";
    public const string SupportMinutesSetting = "alerts.supportMinutes";

    public static readonly TimeSpan DefaultEscalateAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultSupportAfter = TimeSpan.FromMinutes(30);

    /// <summary>The longest wait a hospital may set before either step: a day, when the alert is told again anyway.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromDays(1);

    /// <summary>
    /// The next step for <paramref name="incident"/> at <paramref name="now"/>. <paramref name="levelUndelivered"/> is true
    /// when nothing sent at the level reached so far has reached anyone: nothing was sent (nobody set), or each message
    /// failed, was skipped or was suppressed.
    /// </summary>
    public AlertStep Next(AlertIncident incident, DateTimeOffset now, bool levelUndelivered)
    {
        ArgumentNullException.ThrowIfNull(incident);
        if (incident.ClearedAt is not null)
        {
            return AlertStep.None;
        }

        if (incident.ToldAt is not { } told)
        {
            return AlertStep.Tell;
        }

        if (incident.AcknowledgedAt is null)
        {
            var (since, wait) = incident.Level == AlertLevel.OnCall
                ? (told, EscalateAfter)
                : (incident.SecondContactAt ?? told, SupportAfter);
            var next = incident.Level switch
            {
                AlertLevel.OnCall when !string.IsNullOrWhiteSpace(SecondContactPhone) => AlertStep.EscalateToSecondContact,
                AlertLevel.OnCall or AlertLevel.SecondContact when !string.IsNullOrWhiteSpace(SupportPhone) => AlertStep.EscalateToSupport,
                _ => AlertStep.None,
            };
            if (next != AlertStep.None && (levelUndelivered || now - since >= wait))
            {
                return next;
            }
        }

        return now - (incident.RemindedAt ?? told) >= SystemHealthAlerts.RemindAfter ? AlertStep.Remind : AlertStep.None;
    }

    /// <summary>System health's warning when a critical alert nobody acknowledges would go no further than IT on-call.</summary>
    public const string NotSetCode = "alerts.no_escalation";

    /// <summary>
    /// System health's warning on a live server where neither the second contact nor HyFib support is set: a critical
    /// alert nobody acknowledges then goes no further than IT on-call. A warning, not critical: IT on-call is still told.
    /// </summary>
    public static async Task<HealthWarning?> WarningAsync(ISettingsService settings, PlatformOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowsSimulators)
        {
            return null;
        }

        var escalation = await ReadAsync(settings, ct);
        return escalation.SecondContactPhone is null && escalation.SupportPhone is null
            ? new HealthWarning(NotSetCode, "warning",
                "A critical alert nobody acknowledges goes no further than IT on-call: set a second contact (alerts.escalationPhone) "
                + "and HyFib support's number (alerts.supportPhone) under Administration › Settings.")
            : null;
    }

    /// <summary>
    /// Reads the escalation from the settings, which are free JSON: a number typed without quotes is the phone number all
    /// the same, and minutes outside 0 to a day, or not a number, are the defaults.
    /// </summary>
    public static async Task<AlertEscalation> ReadAsync(ISettingsService settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AlertEscalation(
            await PhoneAsync(settings, SecondContactSetting, ct),
            await PhoneAsync(settings, SupportSetting, ct),
            await MinutesAsync(settings, EscalateMinutesSetting, DefaultEscalateAfter, ct),
            await MinutesAsync(settings, SupportMinutesSetting, DefaultSupportAfter, ct));
    }

    private static async Task<string?> PhoneAsync(ISettingsService settings, string key, CancellationToken ct) =>
        await settings.GetAsync<JsonElement?>(key, cancellationToken: ct) switch
        {
            { ValueKind: JsonValueKind.String } text when !string.IsNullOrWhiteSpace(text.GetString()) => text.GetString()!.Trim(),
            { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
            _ => null,
        };

    private static async Task<TimeSpan> MinutesAsync(ISettingsService settings, string key, TimeSpan fallback, CancellationToken ct)
    {
        double? minutes = await settings.GetAsync<JsonElement?>(key, cancellationToken: ct) switch
        {
            { ValueKind: JsonValueKind.Number } number when number.TryGetDouble(out var value) => value,
            { ValueKind: JsonValueKind.String } text when double.TryParse(text.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) => value,
            _ => null,
        };
        return minutes is { } m && m >= 0 && TimeSpan.FromMinutes(m) <= LongestWait ? TimeSpan.FromMinutes(m) : fallback;
    }
}

internal sealed class AlertIncidentConfiguration : IEntityTypeConfiguration<AlertIncident>
{
    public void Configure(EntityTypeBuilder<AlertIncident> b)
    {
        b.ToTable("alert_incidents", "platform");
        b.Property(x => x.Code).HasMaxLength(AlertIncident.CodeLength);
        b.Property(x => x.Server).HasMaxLength(AlertIncident.ServerLength);
        b.Property(x => x.Message).HasMaxLength(AlertIncident.MessageLength);
        b.Property(x => x.Level).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.AcknowledgedBy).HasMaxLength(200);
        b.Ignore(x => x.LevelMessageIds);

        // One open incident per warning and server: the alerts take turns (an advisory lock), and the database refuses a second.
        b.HasIndex(x => new { x.Code, x.Server }).IsUnique().HasFilter("cleared_at IS NULL AND NOT is_deleted");
        b.HasIndex(x => x.RaisedAt);
    }
}
