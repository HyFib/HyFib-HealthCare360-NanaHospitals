using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Auditing;

public static class AuditCategories
{
    public const string Auth = "auth";
    public const string Data = "data";
    public const string PhiAccess = "phi";
    public const string Security = "security";
    public const string Admin = "admin";
    public const string Clinical = "clinical";
    public const string Integration = "integration";
}

public enum AuditSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// Append-only audit record. Rows are inserted without locking; <see cref="AuditSealer"/> later assigns
/// <see cref="ChainIndex"/>, a SHA-256 hash chained to the previous sealed row, so tampering is detectable, and a seal of
/// that hash under a key held outside the database (<see cref="Mac"/>), so whoever can write the database cannot make a new
/// chain that verifies. A database trigger forbids UPDATE (other than sealing) and DELETE.
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Category { get; set; } = AuditCategories.Data;
    public string Action { get; set; } = string.Empty;
    public AuditSeverity Severity { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public Guid? PatientId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public Guid? FacilityId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? SessionId { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public string? Summary { get; set; }
    public string? Details { get; set; }

    public long? ChainIndex { get; set; }
    public string? PreviousHash { get; set; }
    public string? Hash { get; set; }

    /// <summary>HMAC-SHA256 of <see cref="Hash"/> under the <see cref="AuditSealKey"/>; null for events sealed before it.</summary>
    public string? Mac { get; set; }

    /// <summary>
    /// Canonical text hashed into the chain (stable field order, invariant formatting): every column but the seals, so a
    /// changed severity, user name, device, session, address or correlation ID breaks the chain as a changed summary does.
    /// </summary>
    public string CanonicalContent() => string.Join('\u001f',
        LegacyCanonicalContent(), Severity, UserName, DeviceId, SessionId, IpAddress, CorrelationId);

    /// <summary>
    /// What the chain hashed before it covered every column. Records sealed then are still verified against it (see
    /// <see cref="IsSealedAfter"/>); for them a change to a column it leaves out goes unnoticed.
    /// </summary>
    public string LegacyCanonicalContent() => string.Join('\u001f',
        Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        OccurredAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Category, Action, EntityType, EntityId, PatientId, UserId, FacilityId, Summary, Details);

    public static string ComputeHash(string? previousHash, string canonicalContent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((previousHash ?? "GENESIS") + "\u001e" + canonicalContent)));

    /// <summary>
    /// Whether <see cref="Hash"/> seals this record, as it is now, after <paramref name="previousHash"/>. A record sealed
    /// before the chain covered every column matches its legacy content instead. A newer record cannot pass for one: its
    /// hash would have to change, and that breaks the link from the record after it.
    /// </summary>
    public bool IsSealedAfter(string? previousHash) =>
        string.Equals(Hash, ComputeHash(previousHash, CanonicalContent()), StringComparison.Ordinal)
        || string.Equals(Hash, ComputeHash(previousHash, LegacyCanonicalContent()), StringComparison.Ordinal);
}

/// <remarks>
/// The table is partitioned by month of <see cref="AuditEvent.OccurredAt"/> (SCAL-04, migration
/// <c>PartitionHighVolumeTables</c>). PostgreSQL wants the partition key in every unique index, so the primary key is
/// (id, occurred_at), in the database and in the model, and the chain position is indexed but not unique:
/// <see cref="AuditChainService"/> seals under one advisory lock, the chain-link trigger refuses a seal that does not
/// follow the one before, and verification walks the positions in order, so a repeated position breaks it. With the time
/// in the key, every UPDATE EF writes names the row's month, which PostgreSQL then reads alone: by the id alone it looks in
/// every month's partition, and with ten years of them sealing 500 events took 2 seconds instead of 0.1.
/// </remarks>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events", "audit");
        b.HasKey(x => new { x.Id, x.OccurredAt });
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Category).HasMaxLength(32);
        b.Property(x => x.Action).HasMaxLength(128);
        b.Property(x => x.EntityType).HasMaxLength(128);
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.UserName).HasMaxLength(128);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.CorrelationId).HasMaxLength(128);
        b.Property(x => x.Summary).HasMaxLength(1000);
        b.Property(x => x.Details).HasColumnType("jsonb");
        b.Property(x => x.PreviousHash).HasMaxLength(64);
        b.Property(x => x.Hash).HasMaxLength(64);
        b.Property(x => x.Mac).HasMaxLength(64);

        b.HasIndex(x => x.OccurredAt);
        b.HasIndex(x => new { x.PatientId, x.OccurredAt });
        b.HasIndex(x => new { x.UserId, x.OccurredAt });
        b.HasIndex(x => new { x.Category, x.OccurredAt });
        // Not unique: a unique index on a partitioned table must include the partition key (see the remarks above).
        b.HasIndex(x => x.ChainIndex);
        b.HasIndex(x => x.Id).HasFilter("hash IS NULL").HasDatabaseName("ix_audit_events_unsealed");
    }
}

/// <summary>An audit record requested by application code.</summary>
public sealed record AuditEntry(string Category, string Action, string? Summary = null)
{
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public Guid? PatientId { get; init; }
    public object? Details { get; init; }
    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;

    /// <summary>Overrides the user (for example a failed login where nobody is authenticated yet).</summary>
    public Guid? UserId { get; init; }

    public string? UserName { get; init; }
}

public interface IAuditLogger
{
    /// <summary>Adds the entry to the current unit of work; it is saved with the caller's SaveChangesAsync.</summary>
    void Add(AuditEntry entry);

    /// <summary>Writes the entry immediately in its own transaction (reads, failed logins, security events).</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Records that the current user viewed/printed/exported part of a patient's record (PHI access).</summary>
    Task PatientRecordAccessedAsync(Guid patientId, string what, CancellationToken cancellationToken = default);
}

internal sealed class AuditLogger(
    HospitalDbContext db,
    IDbContextFactory<HospitalDbContext> contextFactory,
    ICurrentUser currentUser,
    TimeProvider time) : IAuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Add(AuditEntry entry) => db.Set<AuditEvent>().Add(Build(entry));

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.Set<AuditEvent>().Add(Build(entry));
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task PatientRecordAccessedAsync(Guid patientId, string what, CancellationToken cancellationToken = default) =>
        WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "patient.accessed", what) { PatientId = patientId }, cancellationToken);

    private AuditEvent Build(AuditEntry entry) => new()
    {
        OccurredAt = time.GetUtcNow(),
        Category = entry.Category,
        Action = entry.Action,
        Severity = entry.Severity,
        Summary = entry.Summary,
        EntityType = entry.EntityType,
        EntityId = entry.EntityId,
        PatientId = entry.PatientId,
        UserId = entry.UserId ?? currentUser.UserId,
        UserName = entry.UserName ?? currentUser.UserName,
        FacilityId = currentUser.FacilityId,
        DeviceId = currentUser.DeviceId,
        SessionId = currentUser.SessionId,
        IpAddress = currentUser.IpAddress,
        CorrelationId = currentUser.CorrelationId,
        Details = entry.Details is null ? null : JsonSerializer.Serialize(entry.Details, JsonOptions),
    };
}
