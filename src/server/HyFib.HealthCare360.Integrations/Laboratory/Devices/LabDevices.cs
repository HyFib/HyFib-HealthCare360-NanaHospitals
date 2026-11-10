using HyFib.HealthCare360.Integrations.Persistence;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Integrations.Laboratory.Devices;

/// <summary>How an analyser connection's devices prove who they are before a message is read.</summary>
public enum LabDeviceAuthentication
{
    /// <summary>
    /// Not at all: only the connection's IP allow-list stands in the way. Connections saved before device authentication
    /// keep it until the lab chooses one of the others; Lab setup shows them as unauthenticated.
    /// </summary>
    None,

    /// <summary>
    /// A secret of the device's own: the gateway sends a fresh challenge and the device (or the middleware box or
    /// serial-to-network converter in front of it) answers with an HMAC-SHA256 of it under its secret, before any HL7 or
    /// ASTM. The secret itself never crosses the network.
    /// </summary>
    SharedSecret,

    /// <summary>Mutual TLS: the device presents a client certificate whose SHA-256 fingerprint is approved for the connection.</summary>
    MutualTls,

    /// <summary>
    /// A serial analyser's lab agent: the agent proves itself with its own device key on every call (enrolment), and must
    /// also be approved as a device of this connection (its device id is the identifier), so whoever sets the agent on a
    /// connection cannot route an analyser through a PC alone.
    /// </summary>
    LabAgent,
}

public enum LabDeviceStatus
{
    /// <summary>Tried to connect and was refused: unknown here. Listed for the lab to add.</summary>
    Seen,

    /// <summary>Added by one person; refused until someone else approves it.</summary>
    PendingApproval,

    Approved,

    /// <summary>No longer accepted.</summary>
    Revoked,
}

/// <summary>
/// A device allowed (or asking) to send results on one analyser connection: identified by its client certificate's
/// fingerprint (mutual TLS) or by the key id it names when it answers the challenge (shared secret). A device that tries
/// to connect and is not approved is refused and recorded here as <see cref="LabDeviceStatus.Seen"/>; someone in the
/// lab adds it, and someone else approves it (the approvals framework, kind <c>lab.device</c>).
/// </summary>
internal sealed class LabDevice : AuditableEntity
{
    /// <summary>Most devices seen and not added that one connection lists; more are refused without a record.</summary>
    public const int MaxSeenPerConnection = 20;

    /// <summary>The analyser connection's code (<see cref="LabInstrumentSettings.Code"/>).</summary>
    public string ConnectionCode { get; set; } = string.Empty;

    public string? Name { get; set; }

    public LabDeviceAuthentication Method { get; set; }

    /// <summary>Mutual TLS: the client certificate's SHA-256 fingerprint (upper-case hex). Shared secret: the key id.</summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Shared secret: the device's secret, encrypted at rest (the gateway needs it to check the device's answer).</summary>
    [SensitiveData]
    public string? SecretCipher { get; set; }

    /// <summary>Mutual TLS: the certificate's subject, as presented, for people to recognise it.</summary>
    public string? Subject { get; set; }

    public LabDeviceStatus Status { get; set; }

    public DateTimeOffset? FirstSeenAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public string? LastAddress { get; set; }

    /// <summary>Refused attempts while unknown, pending or revoked, or with a wrong answer.</summary>
    public int RefusedAttempts { get; set; }

    public Guid? AddedBy { get; set; }

    public DateTimeOffset? AddedAt { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

internal sealed class LabDeviceConfiguration : IEntityTypeConfiguration<LabDevice>
{
    public void Configure(EntityTypeBuilder<LabDevice> b)
    {
        b.ToTable("lab_devices", IntegrationsSchema.Name);
        b.Property(x => x.ConnectionCode).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Identifier).HasMaxLength(64);
        b.Property(x => x.SecretCipher).HasMaxLength(1000).IsProtectedValue();
        b.Property(x => x.Subject).HasMaxLength(300);
        b.Property(x => x.LastAddress).HasMaxLength(64);

        // One row per device of a connection, whatever its status.
        b.HasIndex(x => new { x.ConnectionCode, x.Method, x.Identifier }).IsUnique().HasFilter("is_deleted = false");
    }
}
