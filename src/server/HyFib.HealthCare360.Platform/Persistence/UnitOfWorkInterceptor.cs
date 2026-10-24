using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// Runs inside every SaveChanges:
/// 1. dispatches domain events (handlers can add work to the same transaction),
/// 2. stamps audit columns, converts deletes into soft deletes and fills FacilityId,
/// 3. records a data-change audit event for each audited entity.
/// </summary>
public sealed class UnitOfWorkInterceptor(
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    DomainEventDispatcher domainEvents) : SaveChangesInterceptor
{
    private const int MaxDomainEventPasses = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        context.ChangeTracker.DetectChanges();
        var now = timeProvider.GetUtcNow();
        var auditEvents = new List<AuditEvent>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged)
            {
                continue;
            }

            ApplyFacility(entry);
            var action = ApplyAuditColumns(entry, now);

            if (action is not null && ShouldAudit(entry))
            {
                auditEvents.Add(BuildAuditEvent(entry, action, now));
            }
        }

        if (auditEvents.Count > 0)
        {
            context.Set<AuditEvent>().AddRange(auditEvents);
        }

        return result;
    }

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

    private static bool ShouldAudit(EntityEntry entry) =>
        (entry.Entity is AuditableEntity && entry.Metadata.ClrType.GetCustomAttribute<NotAuditedAttribute>() is null)
        || entry.Metadata.ClrType.GetCustomAttribute<AuditedAttribute>() is not null;

    private AuditEvent BuildAuditEvent(EntityEntry entry, string action, DateTimeOffset now)
    {
        var changes = AuditChanges(entry, action);
        return new AuditEvent
        {
            OccurredAt = now,
            Category = AuditCategories.Data,
            Action = $"{entry.Metadata.ClrType.Name}.{action}",
            EntityType = entry.Metadata.ClrType.Name,
            EntityId = entry.Property(nameof(Entity.Id)).CurrentValue?.ToString(),
            PatientId = TryGetPatientId(entry),
            UserId = currentUser.UserId,
            UserName = currentUser.UserName,
            FacilityId = entry.Entity is IFacilityScoped scoped ? scoped.FacilityId : currentUser.FacilityId,
            DeviceId = currentUser.DeviceId,
            SessionId = currentUser.SessionId,
            IpAddress = currentUser.IpAddress,
            CorrelationId = currentUser.CorrelationId,
            Details = JsonSerializer.Serialize(changes, JsonOptions),
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
            var name = property.Metadata.Name;
            if (property.Metadata.IsShadowProperty() || name is nameof(IVersioned.Version) or nameof(IAuditable.UpdatedAt) or nameof(IAuditable.UpdatedBy))
            {
                continue;
            }

            if (action == "update" && !property.IsModified)
            {
                continue;
            }

            var sensitive = property.Metadata.PropertyInfo?.GetCustomAttribute<SensitiveDataAttribute>() is not null;
            changes[prefix + JsonNamingPolicy.CamelCase.ConvertName(name)] = action switch
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

    // Binary values (a DSC signature, the bytes it signed, which name the patient) are summarised, never copied: the
    // trail says that they changed and can prove which value they were, without holding the data a second time.
    private static object? Mask(object? value, bool sensitive) =>
        sensitive && value is not null ? "***" : value switch
        {
            string s when s.Length > 500 => s[..500] + "…",
            byte[] bytes => new BinaryValue(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))),
            _ => value,
        };

    /// <summary>How the data audit records a binary value: its length and SHA-256.</summary>
    internal sealed record BinaryValue(int Bytes, string Sha256);

    private static Guid? TryGetPatientId(EntityEntry entry)
    {
        if (entry.Metadata.ClrType.Name == "Patient")
        {
            return entry.Property(nameof(Entity.Id)).CurrentValue as Guid?;
        }

        var property = entry.Metadata.FindProperty("PatientId");
        return property is null ? null : entry.Property("PatientId").CurrentValue as Guid?;
    }
}
