using HyFib.HealthCare360.Platform.Auditing;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The audit chain's seal (SRS SEC-04): a record is sealed over every column but the seal itself, and a record sealed
/// before the chain covered every column still verifies against what was hashed then. AuditTrailTests checks the same
/// against the real database.
/// </summary>
public sealed class AuditSealTests
{
    private const string Previous = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";

    private static readonly Action<AuditEvent>[] Changes =
    [
        e => e.Id++,
        e => e.OccurredAt = e.OccurredAt.AddSeconds(-1),
        e => e.Category = AuditCategories.Data,
        e => e.Action = "patient.accessed",
        e => e.Severity = AuditSeverity.Info,
        e => e.EntityType = "Visit",
        e => e.EntityId = "elsewhere",
        e => e.PatientId = Guid.CreateVersion7(),
        e => e.UserId = Guid.CreateVersion7(),
        e => e.UserName = "someone else",
        e => e.FacilityId = Guid.CreateVersion7(),
        e => e.DeviceId = Guid.CreateVersion7(),
        e => e.SessionId = Guid.CreateVersion7(),
        e => e.IpAddress = "203.0.113.9",
        e => e.CorrelationId = "elsewhere",
        e => e.Summary = "Routine review",
        e => e.Details = "{\"reason\": \"Routine review\"}",
    ];

    [Fact]
    public void A_seal_covers_every_column()
    {
        Sealed().IsSealedAfter(Previous).ShouldBeTrue();
        Sealed().IsSealedAfter(null).ShouldBeFalse("The seal is linked to the record before it.");

        for (var i = 0; i < Changes.Length; i++)
        {
            var changed = Sealed();
            Changes[i](changed);
            changed.IsSealedAfter(Previous).ShouldBeFalse($"Change {i + 1} of {Changes.Length} went unnoticed.");
        }
    }

    [Fact]
    public void A_record_sealed_before_the_chain_covered_every_column_still_verifies_as_it_was_sealed()
    {
        var legacy = Record();
        legacy.Hash = AuditEvent.ComputeHash(Previous, legacy.LegacyCanonicalContent());
        legacy.IsSealedAfter(Previous).ShouldBeTrue();

        legacy.Summary = "Routine review";
        legacy.IsSealedAfter(Previous).ShouldBeFalse("What the chain hashed then is still protected.");
    }

    private static AuditEvent Sealed()
    {
        var record = Record();
        record.Hash = AuditEvent.ComputeHash(Previous, record.CanonicalContent());
        return record;
    }

    private static AuditEvent Record() => new()
    {
        Id = 4_512,
        OccurredAt = new DateTimeOffset(2026, 10, 20, 3, 15, 42, TimeSpan.Zero),
        Category = AuditCategories.Security,
        Action = "patient.break_glass",
        Severity = AuditSeverity.Critical,
        EntityType = "Patient",
        EntityId = "01928f5e-2b1c-7c3d-9e4f-5a6b7c8d9e0f",
        PatientId = Guid.Parse("01928f5e-2b1c-7c3d-9e4f-5a6b7c8d9e0f"),
        UserId = Guid.Parse("01928f5e-0000-7000-8000-0000000000a1"),
        UserName = "doctor",
        FacilityId = Guid.Parse("01928f5e-0000-7000-8000-0000000000f1"),
        DeviceId = Guid.Parse("01928f5e-0000-7000-8000-0000000000d1"),
        SessionId = Guid.Parse("01928f5e-0000-7000-8000-0000000000e1"),
        IpAddress = "10.20.30.40",
        CorrelationId = "0b9f7e3c2d1a4f5e8c7b6a5d4e3f2a1b",
        Summary = "Unconscious in casualty, needs the allergy history",
        Details = "{\"reason\": \"Unconscious in casualty\"}",
        ChainIndex = 4_512,
        PreviousHash = Previous,
    };
}
