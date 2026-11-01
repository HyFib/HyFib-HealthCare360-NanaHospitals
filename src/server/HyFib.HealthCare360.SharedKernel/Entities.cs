using System.ComponentModel.DataAnnotations.Schema;

namespace HyFib.HealthCare360.SharedKernel;

/// <summary>
/// Base type for every persisted entity. Identifiers are time-ordered GUID v7 values generated
/// in-process, so they index well in PostgreSQL and can be created before the row is saved.
/// </summary>
public abstract class Entity
{
    private List<IDomainEvent>? _domainEvents;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Events raised by domain logic; dispatched before the unit of work commits.</summary>
    [NotMapped]
    public IReadOnlyCollection<IDomainEvent> DomainEvents =>
        (IReadOnlyCollection<IDomainEvent>?)_domainEvents ?? [];

    protected void Raise(IDomainEvent domainEvent) => (_domainEvents ??= []).Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents?.Clear();
}

/// <summary>
/// Entity with audit columns, soft delete and an optimistic-concurrency version
/// (mapped to PostgreSQL's <c>xmin</c> system column).
/// </summary>
public abstract class AuditableEntity : Entity, IAuditable, ISoftDeletable, IVersioned
{
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }

    public uint Version { get; set; }
}

public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }
    Guid? CreatedBy { get; set; }
    DateTimeOffset? UpdatedAt { get; set; }
    Guid? UpdatedBy { get; set; }
}

public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTimeOffset? DeletedAt { get; set; }
    Guid? DeletedBy { get; set; }
}

public interface IVersioned
{
    uint Version { get; set; }
}

/// <summary>
/// Rows that belong to one facility (hospital/branch). A named global query filter restricts
/// queries to the signed-in user's current facility.
/// </summary>
public interface IFacilityScoped
{
    Guid FacilityId { get; set; }
}

/// <summary>Marks a property whose value must never be written to logs or audit diffs.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveDataAttribute : Attribute;

/// <summary>
/// Excludes a high-volume entity (for example device readings or stock ledger lines) from automatic
/// data-change auditing. Explicit audit entries can still be written.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NotAuditedAttribute : Attribute;

/// <summary>
/// Audits the changes to an entity that is not an <see cref="AuditableEntity"/>, as the data audit does for those: an
/// append-only statutory register, whose every line and every change belongs in the sealed audit trail.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditedAttribute : Attribute;

/// <summary>
/// Audits the rows of a collection with the record that holds them (a coding's ICD codes, a tariff's bed multipliers, a
/// user's roles): when a row is added, changed or removed, the record's own data audit event lists the collection as it
/// was and as it is, so replacing it keeps the codes or amounts it had in the trail. The row needs one foreign key to its
/// record, from a collection navigation of the record's (<c>EncounterCoding.Entries</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditedWithParentAttribute : Attribute;

/// <summary>Marker for in-process domain events.</summary>
public interface IDomainEvent;

public abstract record DomainEvent : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
