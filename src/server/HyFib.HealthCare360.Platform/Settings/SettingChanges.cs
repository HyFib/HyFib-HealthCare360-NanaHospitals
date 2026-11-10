using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Settings;

/// <summary>A change to a guarded setting waiting for a second administrator, as the settings list shows it.</summary>
public sealed record PendingSettingChange(Guid ApprovalId, string Summary, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>What saving a setting would do, before it is saved: whether it waits for a second administrator, and in words.</summary>
/// <param name="Controls">What the setting controls; null for one that is not guarded.</param>
public sealed record SettingChangeCheck(bool Waits, string Setting, string? Controls, string From, string To);

/// <summary>
/// A change to a guarded setting as it will be saved once approved: the value's JSON, or for a credential that JSON as the
/// settings store encrypts it (<see cref="SecretSettingValues"/>), so the clear text never sits in the request.
/// </summary>
internal sealed record SettingChangePayload(
    string Key, Guid? FacilityId, bool Secret, string? Value, string? ProtectedValue, string? Description, string Name, string From, string To);

/// <summary>
/// Changes to the settings <see cref="SettingGuard"/> guards: one holder of <see cref="PlatformPermissions.SettingsManage"/>
/// asks, with the reason, and another approves (<see cref="SettingGuard.ChangeKind"/>); the person who asked never does
/// (<c>approval.self</c>). One change waits per setting and facility. Approved, it is saved as any setting is
/// (<see cref="ISettingsService"/>), only over the value it was asked against (<c>approval.stale</c> otherwise), and
/// audited as <c>setting.changed</c> at Warning with who asked and who approved.
/// </summary>
internal sealed class SettingChanges(
    HospitalDbContext db, ISettingsService settings, IFieldProtector protector, IApprovals approvals, IAuditLogger audit, ICurrentUser current, TimeProvider time)
{
    public const string ChangedAction = "setting.changed";

    /// <summary>
    /// What saving <paramref name="request"/> as <paramref name="key"/> does, against the value in force where it is saved:
    /// the facility's own value, else the hospital's, for a facility (a facility's value, even an empty one, overrides the
    /// hospital's there); the hospital's own for a hospital-wide value.
    /// </summary>
    public async Task<SettingChange> JudgeAsync(string key, SaveSettingRequest request, CancellationToken ct)
    {
        var rows = await db.Set<SystemSetting>().AsNoTracking()
            .Where(s => s.Key == key && (s.FacilityId == null || s.FacilityId == request.FacilityId))
            .ToListAsync(ct);
        var inForce = rows.OrderByDescending(s => s.FacilityId != null).FirstOrDefault();
        return SettingGuard.Judge(key, inForce is null ? null : JsonNode.Parse(inForce.Value), inForce?.IsSecret == true, request.Value, request.Secret);
    }

    /// <summary>
    /// Asks another administrator to make <paramref name="change"/>. Asked against the value the administrator saw, as a save
    /// is (REL-04): one saved meanwhile is not overwritten (409). The request shows the setting, what it controls, where it
    /// applies, the value in force and the one asked; a credential's values are never shown.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(string key, SaveSettingRequest request, SettingChange change, CancellationToken ct)
    {
        var version = await SettingVersions.OfAsync(db, key, request.FacilityId, ct);
        if (version != request.Version)
        {
            return Error.ConcurrencyConflict;
        }

        // As SetAsync encrypts it: a *.secret key even when the request leaves "secret" unset.
        var secret = request.Secret || SecretSettingKeys.IsSecret(key);
        var json = request.Value?.ToJsonString() ?? "null";
        var payload = new SettingChangePayload(
            key, request.FacilityId, secret, secret ? null : json, secret ? SecretSettingValues.Protect(protector, json) : null, request.Description, change.Name,
            change.From, change.To);

        var place = request.FacilityId is { } facilityId
            ? await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Name).FirstOrDefaultAsync(ct) ?? "One facility"
            : "All facilities";
        List<ApprovalDetail> details =
        [
            new("Setting", change.Name.Contains(key, StringComparison.Ordinal) ? change.Name : $"{change.Name} ({key})"),
            new("What it controls", change.Rule!.Controls),
            new("Applies to", place),
            new("In force now", change.From),
            new("Asked for", change.To),
        ];
        if (request.Description is { Length: > 0 } description)
        {
            details.Add(new("Description", description));
        }

        var facility = request.FacilityId ?? current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        return await approvals.RequestAsync(
            SettingGuard.ChangeKind,
            new ApprovalSubject(SettingGuard.SubjectType, SettingGuard.SubjectId(key, request.FacilityId), version, facility, null),
            payload,
            request.FacilityId is null ? change.Summary : $"{change.Summary} ({place})",
            details,
            request.Reason ?? string.Empty,
            ct);
    }

    /// <summary>The changes waiting for approval, by setting (<see cref="SettingGuard.SubjectId"/>), whichever facility asked.</summary>
    public async Task<IReadOnlyDictionary<Guid, PendingSettingChange>> PendingAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var waiting = await db.Set<ApprovalRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.Kind == SettingGuard.ChangeKind && r.SubjectType == SettingGuard.SubjectType && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new { r.SubjectId, r.Id, r.Summary, r.RequestedByName, r.RequestedAt })
            .ToListAsync(ct);
        return waiting.GroupBy(r => r.SubjectId)
            .ToDictionary(g => g.Key, g => g.Select(r => new PendingSettingChange(r.Id, r.Summary, r.RequestedByName, r.RequestedAt)).First());
    }

    /// <summary>
    /// Saves an approved change, in the approval's transaction, through the settings store as the settings screen saves one,
    /// if the setting still has the version it had when the change was asked for: a value saved meanwhile is never
    /// overwritten. A credential is decrypted only here, to be encrypted again as it is stored.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<SettingChangePayload>(request.Payload);
        if (await SettingVersions.OfAsync(db, asked.Key, asked.FacilityId, ct) != request.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"{asked.Name} was changed after this was asked for, so nothing was changed. Ask again, against the setting as it is now.");
        }

        string? json;
        try
        {
            json = asked.Secret ? SecretSettingValues.Unprotect(protector, asked.ProtectedValue ?? string.Empty) : asked.Value;
        }
        catch (CryptographicException)
        {
            return Error.Rule("settings.secret_unreadable",
                $"The secret value asked for {asked.Name} can no longer be read (the data key was changed after it was asked for), so nothing was changed. Ask again.");
        }

        var value = json is null ? null : JsonNode.Parse(json);
        await settings.SetAsync(asked.Key, value, asked.FacilityId, asked.Secret, request.SubjectVersion, ct);
        if (asked.Description is not null)
        {
            await db.Set<SystemSetting>().Where(s => s.Key == asked.Key && s.FacilityId == asked.FacilityId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, asked.Description), ct);
        }

        var approvedBy = current.DisplayName ?? current.UserName ?? "Unknown user";
        audit.Add(new AuditEntry(AuditCategories.Admin, ChangedAction,
            Fit($"{asked.Name}: {asked.From} → {asked.To} (asked for by {request.RequestedByName}, approved by {approvedBy})", 1000))
        {
            EntityType = nameof(SystemSetting),
            EntityId = asked.Key,
            Severity = AuditSeverity.Warning,
            Details = new
            {
                facilityId = asked.FacilityId,
                secret = asked.Secret,
                value = asked.Secret ? null : value,
                from = asked.From,
                to = asked.To,
                approval = request.Id,
                requestedBy = request.RequestedBy,
                requestedByName = request.RequestedByName,
                approvedBy = current.UserId,
                approvedByName = approvedBy,
            },
        });
        return $"{asked.Name}: {asked.To}";
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A change to a guarded setting (<see cref="SettingGuard"/>), approved by a second administrator.</summary>
internal sealed class SettingChangeApproval(SettingChanges changes) : IApprovalAction
{
    public string Kind => SettingGuard.ChangeKind;

    public string Title => "Change to a setting";

    public string ApprovePermission => PlatformPermissions.SettingsManage;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => changes.ApplyAsync(request, ct);
}
