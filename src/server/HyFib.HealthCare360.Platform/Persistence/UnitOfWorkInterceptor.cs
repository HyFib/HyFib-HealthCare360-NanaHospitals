using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// Runs inside every SaveChanges:
/// 1. dispatches domain events (handlers can add work to the same transaction),
/// 2. stamps audit columns, converts deletes into soft deletes and fills FacilityId,
/// 3. records a data-change audit event for each audited entity, with the collections audited with it.
/// </summary>
public sealed class UnitOfWorkInterceptor(
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    DomainEventDispatcher domainEvents) : SaveChangesInterceptor
{
    /// <summary>The longest text the data audit copies whole; a longer one is recorded as a <see cref="LongText"/>.</summary>
    internal const int MaxTextLength = 500;

    private const int MaxDomainEventPasses = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Read for every tracked row of every save, so the attribute is looked up once per type.
    private static readonly ConcurrentDictionary<Type, bool> AuditedWithParent = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        throw new NotSupportedException("Use SaveChangesAsync: synchronous saves block the request thread.");

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not { } context)
        {
            return result;
        }

        await DispatchDomainEventsAsync(context, cancellationToken);

        var events = DataAuditEvents(context);
        if (events.Count > 0)
        {
            context.Set<AuditEvent>().AddRange(events);
        }

        return result;
    }

    /// <summary>
    /// Stamps the audit columns of what <paramref name="context"/> is about to save, turns its deletes into soft deletes,
    /// and returns the data audit events that record the changes.
    /// </summary>
    internal List<AuditEvent> DataAuditEvents(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        var now = timeProvider.GetUtcNow();
        var entries = context.ChangeTracker.Entries().ToList();
        var events = new List<DataChange>();

        foreach (var entry in entries)
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged)
            {
                continue;
            }

            ApplyFacility(entry);
            var action = ApplyAuditColumns(entry, now);

            if (action is not null && ShouldAudit(entry))
            {
                events.Add(new DataChange(entry, action, AuditChanges(entry, action)));
            }
        }

        AddCollectionChanges(entries, events);
        return [.. events.Select(change => BuildAuditEvent(change, now))];
    }

    /// <summary>
    /// Whether changes to the entity type are recorded in the data audit trail: an <see cref="AuditableEntity"/> not marked
    /// <see cref="NotAuditedAttribute"/>, or any type marked <see cref="AuditedAttribute"/>. The rows of a type marked
    /// <see cref="AuditedWithParentAttribute"/> are recorded with their record instead (<see cref="ParentForeignKey"/>).
    /// </summary>
    public static bool IsAudited(Type clrType) =>
        (typeof(AuditableEntity).IsAssignableFrom(clrType) && clrType.GetCustomAttribute<NotAuditedAttribute>() is null)
        || clrType.GetCustomAttribute<AuditedAttribute>() is not null;

    /// <summary>
    /// For a type marked <see cref="AuditedWithParentAttribute"/>, the foreign key to the record whose audit events list its
    /// rows: the one key the record holds a collection of the rows by. Null for any other type, or when there is not
    /// exactly one such key (the architecture tests refuse that model).
    /// </summary>
    public static IForeignKey? ParentForeignKey(IEntityType entityType) =>
        !AuditedWithParent.GetOrAdd(entityType.ClrType, type => type.GetCustomAttribute<AuditedWithParentAttribute>() is not null)
            ? null
            : entityType.GetForeignKeys().Where(fk => fk.PrincipalToDependent is { IsCollection: true }).ToList() is [var only] ? only : null;

    private async Task DispatchDomainEventsAsync(DbContext context, CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < MaxDomainEventPasses; pass++)
        {
            var events = context.ChangeTracker.Entries<Entity>()
                .SelectMany(e =>
                {
                    var pending = e.Entity.DomainEvents.ToList();
                    e.Entity.ClearDomainEvents();
                    return pending;
                })
                .ToList();

            if (events.Count == 0)
            {
                return;
            }

            foreach (var domainEvent in events)
            {
                await domainEvents.DispatchAsync(domainEvent, cancellationToken);
            }
        }
    }

    private void ApplyFacility(EntityEntry entry)
    {
        if (entry is { State: EntityState.Added, Entity: IFacilityScoped scoped } && scoped.FacilityId == Guid.Empty
            && currentUser.FacilityId is { } facilityId)
        {
            scoped.FacilityId = facilityId;
        }
    }

    private string? ApplyAuditColumns(EntityEntry entry, DateTimeOffset now)
    {
        var userId = currentUser.UserId;

        switch (entry.State)
        {
            case EntityState.Added:
                if (entry.Entity is IAuditable added)
                {
                    added.CreatedAt = now;
                    added.CreatedBy ??= userId;
                }

                return "create";

            case EntityState.Modified:
                if (entry.Entity is IAuditable modified)
                {
                    modified.UpdatedAt = now;
                    modified.UpdatedBy = userId;
                    entry.Property(nameof(IAuditable.CreatedAt)).IsModified = false;
                    entry.Property(nameof(IAuditable.CreatedBy)).IsModified = false;
                }

                return entry.Entity is ISoftDeletable { IsDeleted: true } && entry.Property(nameof(ISoftDeletable.IsDeleted)).IsModified
                    ? "delete"
                    : "update";

            case EntityState.Deleted when entry.Entity is ISoftDeletable softDeletable:
                entry.State = EntityState.Modified;
                softDeletable.IsDeleted = true;
                softDeletable.DeletedAt = now;
                softDeletable.DeletedBy = userId;
                return "delete";

            case EntityState.Deleted:
                return "delete";

            default:
                return null;
        }
    }

    private static bool ShouldAudit(EntityEntry entry) => IsAudited(entry.Metadata.ClrType);

    /// <summary>
    /// The collections audited with their record (<see cref="AuditedWithParentAttribute"/>) that this save changes: each
    /// record whose rows were added, changed or removed gets, in its own audit event, the collection as it was and as it
    /// is now (all of it when the record is new, as it was when the record is deleted). A record that did not change itself
    /// gets an update event of its own. When the record's collection was not loaded, what was there before is not known
    /// here, so the rows added and removed are recorded instead: only those are certain.
    /// </summary>
    private void AddCollectionChanges(List<EntityEntry> entries, List<DataChange> events)
    {
        var rows = new List<Row>();
        foreach (var entry in entries)
        {
            if (entry.State != EntityState.Detached && ParentForeignKey(entry.Metadata) is { } parent)
            {
                rows.Add(new Row(
                    entry,
                    parent,
                    entry.State == EntityState.Added ? null : KeyOf(entry, parent.Properties, original: true),
                    entry.State == EntityState.Deleted ? null : KeyOf(entry, parent.Properties, original: false)));
            }
        }

        var changed = rows.Where(r => r.Entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        // The records a changed row belongs to now, or belonged to before (a row moved to another record changes both).
        var records = new HashSet<(IForeignKey ForeignKey, string Key)>();
        foreach (var row in changed)
        {
            if (row.WasIn is { } before)
            {
                records.Add((row.ForeignKey, before));
            }

            if (row.IsIn is { } now)
            {
                records.Add((row.ForeignKey, now));
            }
        }

        // Looked up once, so a save of many records (a load test's accounts with their roles) stays one pass over the rows.
        var was = rows.Where(r => r.WasIn is not null && !IsSoftDeleted(r.Entry, original: true)).ToLookup(r => (r.ForeignKey, r.WasIn!));
        var isNow = rows.Where(r => r.IsIn is not null && !IsSoftDeleted(r.Entry, original: false)).ToLookup(r => (r.ForeignKey, r.IsIn!));
        var recordEntries = records.Select(r => r.ForeignKey).Distinct().ToDictionary(fk => fk, fk => entries
            .Where(e => e.State != EntityState.Detached && fk.PrincipalEntityType.IsAssignableFrom(e.Metadata))
            .GroupBy(e => KeyOf(e, fk.PrincipalKey.Properties, original: false) ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
        var eventsOf = events.Where(c => c.Entry is not null).ToDictionary(c => c.Entry!.Entity, ReferenceEqualityComparer.Instance);
        var eventsOfUnread = new Dictionary<(string EntityType, string Key), DataChange>();

        foreach (var (foreignKey, key) in records)
        {
            var before = Items(was[(foreignKey, key)].Select(r => r.Entry), foreignKey, original: true);
            var after = Items(isNow[(foreignKey, key)].Select(r => r.Entry), foreignKey, original: false);
            if (before.Select(i => i.Json).SequenceEqual(after.Select(i => i.Json), StringComparer.Ordinal))
            {
                continue;
            }

            // One event a record, whichever of its collections changed (a user's roles and facilities together).
            var record = recordEntries[foreignKey].GetValueOrDefault(key);
            var unread = (foreignKey.PrincipalEntityType.ClrType.Name, key);
            var change = record is null ? eventsOfUnread.GetValueOrDefault(unread) : eventsOf.GetValueOrDefault(record.Entity);
            if (change is null)
            {
                // A record that is not audited itself keeps no trail of its collections either (the architecture tests
                // refuse such a model).
                if (record is not null && !ShouldAudit(record))
                {
                    continue;
                }

                change = record is null
                    ? new DataChange(null, "update", new Dictionary<string, object?>())
                    {
                        EntityType = foreignKey.PrincipalEntityType.ClrType.Name,
                        EntityId = key,
                    }
                    : new DataChange(record, "update", new Dictionary<string, object?>());
                events.Add(change);
                if (record is null)
                {
                    eventsOfUnread[unread] = change;
                }
                else
                {
                    eventsOf[record.Entity] = change;
                }
            }

            var navigation = foreignKey.PrincipalToDependent!;
            var complete = record is not null && (record.State == EntityState.Added || record.Collection(navigation.Name).IsLoaded);
            change.Changes[JsonNamingPolicy.CamelCase.ConvertName(navigation.Name)] = change.Action switch
            {
                "create" => after.Select(i => i.Values).ToList(),
                "delete" => before.Select(i => i.Values).ToList(),
                _ when complete => new { from = before.Select(i => i.Values).ToList(), to = after.Select(i => i.Values).ToList() },
                _ => new { added = Except(after, before), removed = Except(before, after) },
            };
        }
    }

    /// <summary>The rows as the trail lists them, in a stable order: by their values.</summary>
    private static List<(string Json, Dictionary<string, object?> Values)> Items(IEnumerable<EntityEntry> rows, IForeignKey parent, bool original) =>
        rows.Select(row =>
            {
                // The link to the record and a row's own surrogate id (a new one each time the rows are replaced) say
                // nothing; the other parts of a key do (a user's role is the key of the grant).
                var surrogate = row.Metadata.FindPrimaryKey() is { Properties: [var only] } && !only.IsForeignKey() ? only : null;
                var values = new Dictionary<string, object?>();
                AddValues(values, row.Properties, row.ComplexProperties, prefix: "", original,
                    property => !parent.Properties.Contains(property) && property != surrogate);
                return (Json: JsonSerializer.Serialize(values, JsonOptions), Values: values);
            })
            .OrderBy(i => i.Json, StringComparer.Ordinal)
            .ToList();

    /// <summary>The rows of <paramref name="these"/> that <paramref name="those"/> does not have, each as often as it is missing.</summary>
    private static List<Dictionary<string, object?>> Except(
        List<(string Json, Dictionary<string, object?> Values)> these, List<(string Json, Dictionary<string, object?> Values)> those)
    {
        var left = those.GroupBy(i => i.Json, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var missing = new List<Dictionary<string, object?>>();
        foreach (var (json, values) in these)
        {
            if (left.TryGetValue(json, out var count) && count > 0)
            {
                left[json] = count - 1;
            }
            else
            {
                missing.Add(values);
            }
        }

        return missing;
    }

    /// <summary>The values of a key as text, to compare a row's link with a record's id; null when the row has no link.</summary>
    private static string? KeyOf(EntityEntry entry, IReadOnlyList<IProperty> properties, bool original)
    {
        var values = properties.Select(p => original ? entry.Property(p.Name).OriginalValue : entry.Property(p.Name).CurrentValue).ToList();
        return values.Any(v => v is null) ? null : string.Join('|', values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
    }

    private static bool IsSoftDeleted(EntityEntry entry, bool original) =>
        entry.Entity is ISoftDeletable
        && (original ? entry.Property(nameof(ISoftDeletable.IsDeleted)).OriginalValue : entry.Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue) is true;

    private AuditEvent BuildAuditEvent(DataChange change, DateTimeOffset now)
    {
        var entry = change.Entry;
        return new AuditEvent
        {
            OccurredAt = now,
            Category = AuditCategories.Data,
            Action = $"{change.EntityType}.{change.Action}",
            EntityType = change.EntityType,
            EntityId = change.EntityId,
            PatientId = entry is null ? null : TryGetPatientId(entry),
            UserId = currentUser.UserId,
            UserName = currentUser.UserName,
            FacilityId = entry?.Entity is IFacilityScoped scoped ? scoped.FacilityId : currentUser.FacilityId,
            DeviceId = currentUser.DeviceId,
            SessionId = currentUser.SessionId,
            IpAddress = currentUser.IpAddress,
            CorrelationId = currentUser.CorrelationId,
            Details = JsonSerializer.Serialize(change.Changes, JsonOptions),
        };
    }

    /// <summary>
    /// What a data audit event records: every value on create and delete, and each changed value as from/to on update.
    /// Complex properties are included part by part ("address.city"), so a changed address, cardiology finding or TTI
    /// result is as visible in the trail as any other field.
    /// </summary>
    internal static Dictionary<string, object?> AuditChanges(EntityEntry entry, string action)
    {
        var changes = new Dictionary<string, object?>();
        AddChanges(changes, entry.Properties, entry.ComplexProperties, prefix: "", action);
        return changes;
    }

    private static void AddChanges(
        Dictionary<string, object?> changes, IEnumerable<PropertyEntry> properties, IEnumerable<ComplexPropertyEntry> complexProperties, string prefix, string action)
    {
        foreach (var property in properties)
        {
            if (!IsRecorded(property.Metadata) || (action == "update" && !property.IsModified))
            {
                continue;
            }

            var sensitive = IsSensitive(property.Metadata);
            changes[prefix + JsonNamingPolicy.CamelCase.ConvertName(property.Metadata.Name)] = action switch
            {
                "update" => new { from = Mask(property.OriginalValue, sensitive), to = Mask(property.CurrentValue, sensitive) },
                _ => Mask(property.CurrentValue, sensitive),
            };
        }

        foreach (var complex in complexProperties)
        {
            if (complex.CurrentValue is null)
            {
                continue;
            }

            AddChanges(changes, complex.Properties, complex.ComplexProperties, prefix + JsonNamingPolicy.CamelCase.ConvertName(complex.Metadata.Name) + ".", action);
        }
    }

    /// <summary>Every recorded value of a row, as it is or as it was, complex properties part by part.</summary>
    private static void AddValues(
        Dictionary<string, object?> values, IEnumerable<PropertyEntry> properties, IEnumerable<ComplexPropertyEntry> complexProperties, string prefix,
        bool original, Func<IProperty, bool> include)
    {
        foreach (var property in properties)
        {
            if (!IsRecorded(property.Metadata) || !include(property.Metadata))
            {
                continue;
            }

            values[prefix + JsonNamingPolicy.CamelCase.ConvertName(property.Metadata.Name)] =
                Mask(Comparable(original ? property.OriginalValue : property.CurrentValue), IsSensitive(property.Metadata));
        }

        foreach (var complex in complexProperties)
        {
            // What it was is read part by part from the snapshot; what it is, only when there is one.
            if (!original && complex.CurrentValue is null)
            {
                continue;
            }

            AddValues(values, complex.Properties, complex.ComplexProperties, prefix + JsonNamingPolicy.CamelCase.ConvertName(complex.Metadata.Name) + ".",
                original, include);
        }
    }

    /// <summary>
    /// A row's value as the rows are compared: a multiplier read back as 1.5000 is the 1.5 a screen saves again, and a time
    /// is the instant, whatever offset it was given in.
    /// </summary>
    private static object? Comparable(object? value) => value switch
    {
        decimal number => number / 1.0000000000000000000000000000m,
        DateTimeOffset time => time.ToUniversalTime(),
        _ => value,
    };

    private static bool IsRecorded(IReadOnlyPropertyBase property) =>
        !property.IsShadowProperty() && property.Name is not (nameof(IVersioned.Version) or nameof(IAuditable.UpdatedAt) or nameof(IAuditable.UpdatedBy));

    private static bool IsSensitive(IReadOnlyPropertyBase property) => property.PropertyInfo?.GetCustomAttribute<SensitiveDataAttribute>() is not null;

    // Binary values (a DSC signature, the bytes it signed, which name the patient) are summarised, never copied: the
    // trail says that they changed and can prove which value they were, without holding the data a second time. A long
    // text is cut short to keep each audit row small, with its length and hash, so an edit past the cut still shows.
    private static object? Mask(object? value, bool sensitive) =>
        sensitive && value is not null ? "***" : value switch
        {
            string { Length: > MaxTextLength } text => LongText.Of(text),
            byte[] bytes => new BinaryValue(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))),
            _ => value,
        };

    /// <summary>How the data audit records a binary value: its length and SHA-256.</summary>
    internal sealed record BinaryValue(int Bytes, string Sha256);

    /// <summary>
    /// How the data audit records a text longer than <see cref="MaxTextLength"/>: its start, its length and the SHA-256 of
    /// all of it (UTF-8). A note edited past its first 500 characters starts the same, but its length or hash does not.
    /// </summary>
    internal sealed record LongText(string Start, int Length, string Sha256)
    {
        public static LongText Of(string text)
        {
            // Never between the two halves of a surrogate pair, which would leave half a character in the trail.
            var cut = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
            return new LongText(text[..cut] + "…", text.Length, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }
    }

    private static Guid? TryGetPatientId(EntityEntry entry)
    {
        if (entry.Metadata.ClrType.Name == "Patient")
        {
            return entry.Property(nameof(Entity.Id)).CurrentValue as Guid?;
        }

        var property = entry.Metadata.FindProperty("PatientId");
        return property is null ? null : entry.Property("PatientId").CurrentValue as Guid?;
    }

    /// <summary>
    /// A tracked row audited with its record, and the key of the record it belonged to before this save and belongs to now
    /// (null when it was not there before, as a new row, or is not there now, as a deleted one, or has no record).
    /// </summary>
    private sealed record Row(EntityEntry Entry, IForeignKey ForeignKey, string? WasIn, string? IsIn);

    /// <summary>One data audit event to write: the entity and what changed (or, for a record not loaded, its type and id).</summary>
    private sealed record DataChange(EntityEntry? Entry, string Action, Dictionary<string, object?> Changes)
    {
        public string EntityType { get; init; } = Entry?.Metadata.ClrType.Name ?? string.Empty;

        public string? EntityId { get; init; } = Entry is { } entry && entry.Metadata.FindProperty(nameof(Entity.Id)) is not null
            ? entry.Property(nameof(Entity.Id)).CurrentValue?.ToString()
            : null;
    }
}
