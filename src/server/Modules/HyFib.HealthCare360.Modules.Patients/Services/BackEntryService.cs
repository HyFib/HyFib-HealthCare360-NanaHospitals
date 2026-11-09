using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Downtime;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Back-entry windows and late back-entry (NABH IMS, records kept on paper during a downtime entered afterwards). Each
/// record type has a window set by the administrator (<see cref="BackEntryWindows"/>; lengthening one waits for a second
/// administrator through SettingGuard). A record older than its window is entered only under a
/// <see cref="LateBackEntryGrant"/> a second person approved.
/// </summary>
public sealed class BackEntryService(
    HospitalDbContext db,
    ISettingsService settings,
    IApprovals approvals,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>The approval that opens a late back-entry.</summary>
    public const string LateKind = "patients.late_back_entry";

    public const string SubjectType = "LateBackEntry";

    /// <summary>A clock a few minutes fast on the desk's PC is not "the future".</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>The widest span of paper times one late back-entry may cover.</summary>
    public static readonly TimeSpan LongestSpan = TimeSpan.FromDays(90);

    public async Task<TimeSpan> WindowAsync(BackEntryRecord record, Guid? facilityId, CancellationToken ct)
    {
        var window = BackEntryWindows.For(record);
        return TimeSpan.FromHours(window.Effective(await ReadHoursAsync(window, facilityId, ct)));
    }

    public async Task<IReadOnlyList<BackEntryWindowDto>> WindowsAsync(CancellationToken ct)
    {
        var list = new List<BackEntryWindowDto>();
        foreach (var window in BackEntryWindows.All)
        {
            var hours = window.Effective(await ReadHoursAsync(window, currentUser.FacilityId, ct));
            list.Add(new BackEntryWindowDto(window.Record, window.Name, window.SettingKey, hours, window.DefaultHours, window.MaxHours));
        }

        return list;
    }

    /// <summary>
    /// Whether a record of <paramref name="record"/> dated <paramref name="paperAt"/> may be entered now by the signed-in
    /// user: within its window, or under a late back-entry grant of theirs that covers it (counted as used when
    /// <paramref name="consume"/>, saved with the caller's changes). Answers the grant used, or null within the window.
    /// </summary>
    public async Task<Result<Guid?>> CheckAsync(BackEntryRecord record, DateTimeOffset paperAt, bool consume, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (paperAt > now + ClockTolerance)
        {
            return Error.Field("paperAt", "The time on the paper cannot be in the future.");
        }

        var window = await WindowAsync(record, currentUser.FacilityId, ct);
        if (paperAt >= now - window)
        {
            return (Guid?)null;
        }

        var grant = await FindGrantAsync(record, paperAt, now, ct);
        if (grant is null)
        {
            return Late(record, window);
        }

        if (consume)
        {
            grant.Used++;
        }

        return grant.Id;
    }

    /// <summary>For a validator: true within the window or under a grant (not counted: the validator saves nothing).</summary>
    /// A time in the future is left to the validator's own rule, so it is not refused twice.
    public async Task<bool> AllowsAsync(BackEntryRecord record, DateTimeOffset? paperAt, CancellationToken ct) =>
        paperAt is not { } at || at > time.GetUtcNow() + ClockTolerance || (await CheckAsync(record, at, consume: false, ct)).IsSuccess;

    /// <summary>The message a validator shows past the window.</summary>
    public async Task<string> LateMessageAsync(BackEntryRecord record, CancellationToken ct) =>
        Late(record, await WindowAsync(record, currentUser.FacilityId, ct)).Message;

    public async Task<Result<ApprovalRequestDto>> RequestLateAsync(RequestLateBackEntry request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var now = time.GetUtcNow();
        if (request.PaperTo > now + ClockTolerance)
        {
            return Error.Field("paperTo", "The paper cannot be dated in the future.");
        }

        var window = await WindowAsync(request.Record, facilityId, ct);
        if (request.PaperFrom >= now - window)
        {
            return Error.Rule("backentry.within_window",
                $"Paper dated from {Local(request.PaperFrom)} is within the {Words(window)} back-entry window: enter it without approval.");
        }

        var name = BackEntryWindows.For(request.Record).Name;
        var asked = new LateBackEntryPayload(request.Record, request.PaperFrom, request.PaperTo, request.Records);
        IReadOnlyList<ApprovalDetail> details =
        [
            new("Record", name),
            new("Paper dated", $"{Local(request.PaperFrom)} to {Local(request.PaperTo)}"),
            new("Records", request.Records.ToString(CultureInfo.InvariantCulture)),
            new("Window", $"{Words(window)}: paper older than this is entered only with a second person's approval"),
            new("What follows", $"{currentUser.DisplayName ?? currentUser.UserName} may enter up to {request.Records} of these records, dated in that span, for {LateBackEntryGrant.UsableFor.TotalDays:0} days after approval. Each entry is audited with this approval."),
        ];
        return await approvals.RequestAsync(
            LateKind,
            new ApprovalSubject(SubjectType, Guid.CreateVersion7(), null, facilityId, null),
            asked,
            $"Late back-entry of {request.Records} × {name.ToLowerInvariant()}, dated {Local(request.PaperFrom)} to {Local(request.PaperTo)}",
            details,
            request.Reason.Trim(),
            ct);
    }

    internal async Task<Result<string>> ApplyApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<LateBackEntryPayload>(request.Payload);
        var now = time.GetUtcNow();
        var grant = new LateBackEntryGrant
        {
            FacilityId = request.FacilityId,
            Record = asked.Record,
            PaperFrom = asked.PaperFrom,
            PaperTo = asked.PaperTo,
            MaxRecords = asked.Records,
            RequestedBy = request.RequestedBy,
            RequestedByName = request.RequestedByName,
            Reason = request.Reason,
            ApprovalRequestId = request.Id,
            ApprovedBy = currentUser.UserId,
            ApprovedByName = currentUser.DisplayName ?? currentUser.UserName,
            ApprovedAt = now,
            ExpiresAt = now + LateBackEntryGrant.UsableFor,
        };
        db.Add(grant);
        audit.Add(new AuditEntry(AuditCategories.Data, "patients.late_back_entry_granted",
            $"Late back-entry of {asked.Records} × {BackEntryWindows.For(asked.Record).Name} for {request.RequestedByName}, approved by {grant.ApprovedByName}")
        {
            EntityType = nameof(LateBackEntryGrant),
            EntityId = grant.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { record = asked.Record.ToString(), asked.PaperFrom, asked.PaperTo, asked.Records },
        });
        await Task.CompletedTask;
        return $"usable until {Local(grant.ExpiresAt)}";
    }

    public async Task<IReadOnlyList<LateBackEntryGrantDto>> GrantsAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow() - TimeSpan.FromDays(30);
        var rows = await db.Set<LateBackEntryGrant>().AsNoTracking()
            .Where(g => g.FacilityId == currentUser.FacilityId && g.ApprovedAt >= since)
            .OrderByDescending(g => g.ApprovedAt)
            .Take(200)
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        return [.. rows.Select(g => new LateBackEntryGrantDto(g.Id, g.Record, g.PaperFrom, g.PaperTo, g.MaxRecords, g.Used, g.RequestedByName, g.Reason,
            g.ApprovedByName, g.ApprovedAt, g.ExpiresAt, now < g.ExpiresAt && g.Used < g.MaxRecords))];
    }

    private async Task<LateBackEntryGrant?> FindGrantAsync(BackEntryRecord record, DateTimeOffset paperAt, DateTimeOffset now, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return null;
        }

        // A grant already loaded into this unit of work is checked as it now stands (one used a moment ago counts).
        var local = db.ChangeTracker.Entries<LateBackEntryGrant>().Select(e => e.Entity)
            .FirstOrDefault(g => g.FacilityId == currentUser.FacilityId && g.Covers(record, userId, paperAt, now));
        if (local is not null)
        {
            return local;
        }

        var candidates = await db.Set<LateBackEntryGrant>()
            .Where(g => g.FacilityId == currentUser.FacilityId && g.Record == record && g.RequestedBy == userId
                        && g.PaperFrom <= paperAt && g.PaperTo >= paperAt && g.ExpiresAt > now && g.Used < g.MaxRecords)
            .OrderBy(g => g.ExpiresAt)
            .ToListAsync(ct);
        return candidates.FirstOrDefault();
    }

    private async Task<int?> ReadHoursAsync(BackEntryWindow window, Guid? facilityId, CancellationToken ct)
    {
        try
        {
            return await settings.GetAsync<int?>(window.SettingKey, facilityId, ct);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static Error Late(BackEntryRecord record, TimeSpan window) => Error.Rule("backentry.late",
        $"This {BackEntryWindows.For(record).Name.ToLowerInvariant()} is dated more than {Words(window)} ago, beyond its back-entry window. Ask for a late back-entry approval (Downtime → Late back-entry), then enter it.");

    internal static string Words(TimeSpan window) => window.TotalHours % 24 == 0 && window.TotalHours >= 48
        ? $"{window.TotalDays:0} days"
        : $"{window.TotalHours:0} hours";

    private static string Local(DateTimeOffset at) => HyFib.HealthCare360.Platform.Numbering.NumberFormat.ToHospitalTime(at).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>What a late back-entry approval was asked for.</summary>
internal sealed record LateBackEntryPayload(BackEntryRecord Record, DateTimeOffset PaperFrom, DateTimeOffset PaperTo, int Records);

/// <summary>
/// A late back-entry: whoever enters the records asks (<see cref="PatientsPermissions.BackEntry"/>), and someone else who
/// holds <see cref="PatientsPermissions.BackEntryApprove"/> (medical records officer, hospital administrator) approves.
/// </summary>
internal sealed class LateBackEntryApproval(BackEntryService backEntry) : IApprovalAction
{
    public string Kind => BackEntryService.LateKind;

    public string Title => "Late back-entry of paper records";

    public string ApprovePermission => PatientsPermissions.BackEntryApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => backEntry.ApplyApprovedAsync(request, ct);
}
