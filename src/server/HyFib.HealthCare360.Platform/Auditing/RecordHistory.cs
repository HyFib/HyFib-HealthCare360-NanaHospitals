using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>
/// A record opened for its history: what to call it, whose record it is (for the PHI-access audit), and its parts: the
/// rows its own screen shows as its content that the audit trail records apart from it (a report's results, a bill's
/// receipts), deleted ones included, whose changes are the record's history too. <see cref="Redaction"/> keeps from its
/// reader what the record's screen keeps from them.
/// </summary>
public sealed record HistoryRecord(string Title, Guid? PatientId, IReadOnlyList<HistoryPart>? Parts = null)
{
    public HistoryRedaction Redaction { get; init; } = HistoryRedaction.None;
}

/// <summary>
/// What a record's history keeps from its reader, by the rules that show the record itself, read from the trail: fields
/// its screen masks for them, shown masked so that they changed still shows (<c>phone</c>); fields its screen does not
/// show them, left out of every entry, and a change of one left out whole (<c>sensitiveCategories</c>); and the events
/// about it they may not read, left out (<c>patient.sensitive_changed</c>). Fields are named as the trail names them
/// (<c>address.line1</c>), on the record and its parts alike.
/// </summary>
public sealed record HistoryRedaction(IReadOnlyCollection<string> MaskedFields, IReadOnlyCollection<string> HiddenFields, IReadOnlyCollection<string> HiddenActions)
{
    public static readonly HistoryRedaction None = new([], [], []);
}

/// <summary>A part of a record: its type and id as the audit trail names them, and what to call it (<c>Potassium</c>).</summary>
public sealed record HistoryPart(string EntityType, Guid Id, string Label);

/// <summary>
/// One module's records whose history staff may read from the audit trail: who changed what, when, from what to what.
/// The module that owns the records implements it, as it implements approval actions: it names the record types (as the
/// audit trail names them, the entity's type name), the permission that reads each, and opens a record for its history by
/// the rules that open the record itself.
/// </summary>
public interface IRecordHistorySource
{
    /// <summary>The record types, as the audit trail names them (<c>Bill</c>, <c>LabReport</c>).</summary>
    IReadOnlyCollection<string> EntityTypes { get; }

    /// <summary>The permissions that read records of the type (any one of them), and so their history.</summary>
    IReadOnlyCollection<string> ReadPermissions(string entityType);

    /// <summary>
    /// Opens the record as its own screen would: not found when there is none or it is another facility's, and the
    /// restricted-record rules of its patient (403). Its parts are those its screen shows its reader, found with
    /// deleted ones (their deletion is history too).
    /// </summary>
    Task<Result<HistoryRecord>> OpenAsync(string entityType, Guid id, CancellationToken ct);
}

/// <summary>One field's change: what it was and what it became, as text. Values the trail masks stay masked.</summary>
public sealed record HistoryChange(string Field, string? From, string? To);

/// <summary>
/// One entry of a record's history. <paramref name="By"/> is the person, by the name they are known by now (the login
/// name for an account no longer there), or null for the system. <paramref name="Action"/> is the trail's own
/// (<c>Bill.update</c>, <c>lab.report.amended</c>); <paramref name="Part"/> names the part of the record it is about
/// (<c>Potassium</c>, <c>Approval</c>), null for the record itself; <paramref name="What"/> says it in words;
/// <paramref name="Changes"/> lists the fields a data change set, changed or removed (empty for an entry the trail records
/// in words only).
/// </summary>
public sealed record HistoryEntry(
    long Id, DateTimeOffset At, string? By, string Action, string? Part, string What, IReadOnlyList<HistoryChange> Changes);

public sealed record RecordHistoryDto(string EntityType, Guid EntityId, string Title, PagedResult<HistoryEntry> Entries);

/// <summary>
/// A record's history, newest first, from the audit trail: every create, change and delete of it and of its parts (rows
/// the trail records with it, such as a user's roles, inside its entries; rows it records apart, such as a report's
/// results, named by the record's source), what the modules record about it in words (an amendment), and the approvals
/// asked for on it, in the words of each step: who asked and why, who approved, rejected or withdrew it. (The request
/// rows' own changes say the same field by field; they stay in the trail.) Reads of the record are left out: they are in
/// the patient's access log.
/// </summary>
internal sealed class RecordHistoryService(
    HospitalDbContext db, IServiceProvider services, IPermissionService permissions, ICurrentUser currentUser, IAuditLogger audit)
{
    private const int MaxValueLength = 500;

    /// <summary>A value the reader may not see, as the trail shows a value it masks.</summary>
    private const string Masked = "***";

    /// <summary>The prefix of an approval request row's own data changes (<c>ApprovalRequest.update</c>).</summary>
    private const string ApprovalRows = nameof(ApprovalRequest) + ".";

    private IReadOnlyList<IRecordHistorySource>? sources;

    // Resolved when first needed, as approval actions are: a module's source uses the module's services.
    private IReadOnlyList<IRecordHistorySource> Sources => sources ??= [.. services.GetServices<IRecordHistorySource>()];

    public async Task<Result<RecordHistoryDto>> GetAsync(string entityType, Guid id, PageRequest page, CancellationToken ct)
    {
        if (currentUser.IsPatient || currentUser.UserId is not { } user)
        {
            return Error.Forbidden("history.staff_only", "Only staff can read a record's history.");
        }

        var source = Sources.FirstOrDefault(s => s.EntityTypes.Contains(entityType, StringComparer.Ordinal));
        if (source is null)
        {
            return Error.NotFound("Record type", entityType);
        }

        if (!await HoldsAnyAsync(user, source.ReadPermissions(entityType), ct))
        {
            return Error.Forbidden("history.not_permitted", "You cannot read records of this kind, so not their history either.");
        }

        var opened = await source.OpenAsync(entityType, id, ct);
        if (opened.IsFailure)
        {
            return opened.Error!;
        }

        // The approvals asked for on the record are part of its history, whichever module's they are.
        var approvals = (await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.SubjectType == entityType && r.SubjectId == id)
            .Select(r => r.Id)
            .ToListAsync(ct)).Select(r => r.ToString()).ToList();
        var parts = opened.Value.Parts ?? [];
        var labels = new Dictionary<(string Type, string Id), string>();
        foreach (var part in parts)
        {
            labels.TryAdd((part.EntityType, part.Id.ToString()), part.Label);
        }

        foreach (var approval in approvals)
        {
            labels.TryAdd((nameof(ApprovalRequest), approval), "Approval");
        }

        var key = id.ToString();
        var redaction = opened.Value.Redaction;
        var trail = db.Set<AuditEvent>().AsNoTracking()
            .Where(About([(entityType, key), .. parts.Select(p => (p.EntityType, p.Id.ToString()))], approvals))
            .Where(e => e.Category != AuditCategories.PhiAccess);
        if (redaction.HiddenActions.Count > 0)
        {
            var hiddenActions = redaction.HiddenActions.ToList();
            trail = trail.Where(e => !hiddenActions.Contains(e.Action));
        }

        if (redaction.HiddenFields.Count > 0)
        {
            // A change of a field the reader may not see is left out whole, here, so a page holds what it says it holds; a
            // row created or deleted keeps its other fields.
            var hiddenFields = redaction.HiddenFields.ToArray();
            trail = trail.Where(e => !(e.Category == AuditCategories.Data && e.Action.EndsWith(".update") && e.Details != null
                && EF.Functions.JsonExistAny(e.Details, hiddenFields)));
        }

        var entries = await trail
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Select(e => new { e.Id, e.OccurredAt, e.UserId, e.UserName, e.Category, e.Action, e.EntityType, e.EntityId, e.Summary, e.Details })
            .ToPagedResultAsync(page, ct);

        // Who, by the name they are known by: the trail keeps the login name.
        var users = entries.Items.Select(e => e.UserId).OfType<Guid>().Distinct().ToList();
        var names = users.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<User>().IgnoreQueryFilters().AsNoTracking().Where(u => users.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        if (opened.Value.PatientId is { } patientId)
        {
            await audit.PatientRecordAccessedAsync(patientId, $"Viewed the history of {opened.Value.Title}", ct);
        }

        return new RecordHistoryDto(entityType, id, opened.Value.Title, entries.Map(e =>
        {
            var change = DataChangeOf(e.Category, e.Action, e.EntityType!);
            var part = e.EntityType == entityType && e.EntityId == key ? null : labels.GetValueOrDefault((e.EntityType!, e.EntityId!));
            var by = e.UserId is { } who && names.TryGetValue(who, out var name) && !string.IsNullOrWhiteSpace(name) ? name : e.UserName;
            return new HistoryEntry(
                e.Id, e.OccurredAt, by, e.Action, part,
                change switch { "create" => "Created", "update" => "Changed", "delete" => "Deleted", _ => e.Summary ?? e.Action },
                change is null ? [] : Changes(change, e.Details, redaction));
        }));
    }

    /// <summary>
    /// The trail's events about any of the records, and the steps of the approval requests (not the request rows' own
    /// changes): one condition a type (<c>entity_type = … AND entity_id = ANY(…)</c>), so each is read through the
    /// (entity_type, entity_id, occurred_at) index.
    /// </summary>
    private static Expression<Func<AuditEvent, bool>> About(IEnumerable<(string Type, string Id)> records, List<string> approvals)
    {
        var e = Expression.Parameter(typeof(AuditEvent), "e");
        var conditions = new List<Expression<Func<AuditEvent, bool>>>();
        foreach (var group in records.GroupBy(r => r.Type, r => r.Id, StringComparer.Ordinal))
        {
            var type = group.Key;
            var ids = group.Distinct(StringComparer.Ordinal).ToList();
            conditions.Add(a => a.EntityType == type && ids.Contains(a.EntityId!));
        }

        if (approvals.Count > 0)
        {
            conditions.Add(a => a.EntityType == nameof(ApprovalRequest) && approvals.Contains(a.EntityId!) && !a.Action.StartsWith(ApprovalRows));
        }

        var any = conditions.Select(c => ReplacingExpressionVisitor.Replace(c.Parameters[0], e, c.Body)).Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<AuditEvent, bool>>(any, e);
    }

    private async Task<bool> HoldsAnyAsync(Guid user, IEnumerable<string> any, CancellationToken ct)
    {
        foreach (var permission in any)
        {
            if (await permissions.HasPermissionAsync(user, permission, ct))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>create, update or delete for a data change of the record (<c>Bill.update</c>); null for anything else.</summary>
    private static string? DataChangeOf(string category, string action, string entityType)
    {
        if (category != AuditCategories.Data || !action.StartsWith(entityType + ".", StringComparison.Ordinal))
        {
            return null;
        }

        var verb = action[(entityType.Length + 1)..];
        return verb is "create" or "update" or "delete" ? verb : null;
    }

    /// <summary>
    /// The fields of a data change, from its details: every value on create and delete, each from/to on update, and a child
    /// collection's rows (all of them, or those added and removed); without the fields the reader may not see, and with
    /// those masked for them masked.
    /// </summary>
    internal static IReadOnlyList<HistoryChange> Changes(string change, string? details, HistoryRedaction? redaction = null)
    {
        redaction ??= HistoryRedaction.None;
        if (string.IsNullOrWhiteSpace(details))
        {
            return [];
        }

        using var document = JsonDocument.Parse(details);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var changes = new List<HistoryChange>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (redaction.HiddenFields.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var field = Field(property.Name);
            var value = property.Value;
            var shown = change switch
            {
                "update" when Has(value, "from") || Has(value, "to") => new HistoryChange(field, Text(Get(value, "from")), Text(Get(value, "to"))),
                "update" when Has(value, "added") || Has(value, "removed") =>
                    new HistoryChange(field, Prefixed("removed", Get(value, "removed")), Prefixed("added", Get(value, "added"))),
                "delete" => new HistoryChange(field, Text(value), null),
                _ => new HistoryChange(field, null, Text(value)),
            };
            changes.Add(redaction.MaskedFields.Contains(property.Name, StringComparer.Ordinal)
                ? shown with { From = shown.From is null ? null : Masked, To = shown.To is null ? null : Masked }
                : shown);
        }

        return changes;
    }

    private static bool Has(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _);

    private static JsonElement? Get(JsonElement value, string name) => value.TryGetProperty(name, out var found) ? found : null;

    private static string? Prefixed(string what, JsonElement? rows) =>
        rows is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0 ? $"{what}: {Text(list)}" : null;

    /// <summary>A value as text: as it is, a long text by its start, a binary value by its size, rows one after another.</summary>
    private static string? Text(JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Object when value.TryGetProperty("start", out var start) && value.TryGetProperty("length", out var length) =>
                $"{start.GetString()}… ({length.GetRawText()} characters)",
            JsonValueKind.Object when value.TryGetProperty("bytes", out var bytes) && value.TryGetProperty("sha256", out _) => $"(file, {bytes.GetRawText()} bytes)",
            JsonValueKind.Array => string.Join("; ", value.EnumerateArray().Select(item => Text(item)).Where(t => t is not null)),
            JsonValueKind.Object => string.Join(", ", value.EnumerateObject().Select(p => $"{Field(p.Name)} {Text(p.Value)}")),
            _ => value.GetRawText(),
        };
        return text is { Length: > MaxValueLength } ? text[..(MaxValueLength - 1)] + "…" : text;
    }

    /// <summary>A field as people read it: <c>patientAmount</c> → "Patient amount", <c>address.city</c> → "Address city".</summary>
    internal static string Field(string name)
    {
        var words = new StringBuilder(name.Length + 8);
        foreach (var c in name)
        {
            if (c == '.')
            {
                words.Append(' ');
            }
            else if (char.IsUpper(c) && words.Length > 0 && words[^1] != ' ')
            {
                words.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : c);
            }
        }

        return words.ToString();
    }
}

/// <summary>GET /api/v1/history/{entityType}/{entityId}: a record's history, for staff who may read the record.</summary>
internal static class RecordHistoryEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGroup("/api/v1/history").WithTags("History").RequireAuthorization()
            .MapGet("/{entityType}/{entityId:guid}", async (
                string entityType, Guid entityId, [AsParameters] PageQuery query, RecordHistoryService history, CancellationToken ct) =>
                (await history.GetAsync(entityType, entityId, query.ToRequest(), ct)).ToOk());
}
