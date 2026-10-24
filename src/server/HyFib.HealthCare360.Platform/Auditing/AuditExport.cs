namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>An audit event as exported: every column, the chain position, the hash it was sealed with and its keyed seal.</summary>
public sealed record AuditExportEvent(
    long Id,
    DateTimeOffset OccurredAt,
    string Category,
    string Action,
    AuditSeverity Severity,
    string? EntityType,
    string? EntityId,
    Guid? PatientId,
    Guid? UserId,
    string? UserName,
    Guid? FacilityId,
    Guid? DeviceId,
    Guid? SessionId,
    string? IpAddress,
    string? CorrelationId,
    string? Summary,
    string? Details,
    long ChainIndex,
    string? PreviousHash,
    string Hash,
    string? Mac)
{
    public static AuditExportEvent From(AuditEvent e) => new(e.Id, e.OccurredAt, e.Category, e.Action, e.Severity, e.EntityType, e.EntityId, e.PatientId,
        e.UserId, e.UserName, e.FacilityId, e.DeviceId, e.SessionId, e.IpAddress, e.CorrelationId, e.Summary, e.Details, e.ChainIndex!.Value, e.PreviousHash,
        e.Hash!, e.Mac);

    internal AuditEvent ToEvent() => new()
    {
        Id = Id,
        OccurredAt = OccurredAt,
        Category = Category,
        Action = Action,
        Severity = Severity,
        EntityType = EntityType,
        EntityId = EntityId,
        PatientId = PatientId,
        UserId = UserId,
        UserName = UserName,
        FacilityId = FacilityId,
        DeviceId = DeviceId,
        SessionId = SessionId,
        IpAddress = IpAddress,
        CorrelationId = CorrelationId,
        Summary = Summary,
        Details = Details,
        ChainIndex = ChainIndex,
        PreviousHash = PreviousHash,
        Hash = Hash,
        Mac = Mac,
    };
}

/// <summary>
/// The audit trail for an auditor (NABH, CERT-In, a court): a contiguous part of the sealed chain, positions
/// <see cref="FromIndex"/> to <see cref="ToIndex"/>, with the anchors written outside the database that fall in it. Anyone
/// can check it without HC360's database or keys (<see cref="AuditExportVerifier"/>, the server's
/// <c>audit verify-export</c>): <see cref="Algorithm"/> says how each hash is made.
/// </summary>
public sealed record AuditExport(
    string Format,
    DateTimeOffset ExportedAt,
    string? ExportedBy,
    long FromIndex,
    long ToIndex,
    string Algorithm,
    IReadOnlyList<AuditAnchor> Anchors,
    IReadOnlyList<AuditExportEvent> Events)
{
    public const string FormatName = "hc360-audit-export/1";

    public const string AlgorithmText =
        "hash = upper-case hex of SHA-256(UTF-8 of (previousHash, or \"GENESIS\" for position 1) + U+001E + content). content joins, with " +
        "U+001F: id; occurredAt as ISO 8601 UTC with 7 decimal places; category; action; entityType; entityId; patientId; userId; facilityId; " +
        "summary; details; then severity (its name); userName; deviceId; sessionId; ipAddress; correlationId. A missing value is empty; a GUID " +
        "is written as 8-4-4-4-12 lower-case hex. Records sealed before the chain covered every column hash only the content up to details. mac = HMAC-SHA256 of the " +
        "hash under the hospital's audit seal key, which stays on the server: HC360's own verification checks it.";
}

/// <summary>The outcome of checking an export.</summary>
public sealed record AuditExportCheck(bool Valid, long EventsChecked, int AnchorsChecked, string Message);

public static class AuditExportVerifier
{
    /// <summary>
    /// Checks an export: positions follow each other from <see cref="AuditExport.FromIndex"/>, each record links to the one
    /// before and its hash is that of its content, and each anchor in the export, and in <paramref name="ownAnchors"/> (the
    /// auditor's copy of the anchor file, or anchors noted down earlier), matches the record at its position.
    /// </summary>
    public static AuditExportCheck Verify(AuditExport export, IReadOnlyList<AuditAnchor>? ownAnchors = null)
    {
        ArgumentNullException.ThrowIfNull(export);
        if (export.Format != AuditExport.FormatName)
        {
            return new AuditExportCheck(false, 0, 0, $"This is not an HC360 audit export ({export.Format}).");
        }

        var byIndex = new Dictionary<long, string>();
        var expected = export.FromIndex;
        string? previous = export.Events.FirstOrDefault()?.PreviousHash;
        if (export.FromIndex == 1 && previous is not null)
        {
            return new AuditExportCheck(false, 0, 0, "The first record of the chain cannot link to an earlier one.");
        }

        foreach (var exported in export.Events)
        {
            var e = exported.ToEvent();
            if (exported.ChainIndex != expected || !string.Equals(exported.PreviousHash, previous, StringComparison.Ordinal) || !e.IsSealedAfter(previous))
            {
                return new AuditExportCheck(false, byIndex.Count, 0,
                    $"The chain is broken at position {expected}: the record there was changed, removed or moved.");
            }

            byIndex[exported.ChainIndex] = exported.Hash;
            previous = exported.Hash;
            expected++;
        }

        if (export.Events.Count > 0 && expected - 1 != export.ToIndex)
        {
            return new AuditExportCheck(false, byIndex.Count, 0, $"The export says it ends at position {export.ToIndex}, but its records end at {expected - 1}.");
        }

        var anchors = export.Anchors.Concat(ownAnchors ?? []).Where(a => a.ChainIndex >= export.FromIndex && a.ChainIndex <= export.ToIndex).ToList();
        foreach (var anchor in anchors)
        {
            if (!byIndex.TryGetValue(anchor.ChainIndex, out var hash) || !string.Equals(hash, anchor.Hash, StringComparison.Ordinal))
            {
                return new AuditExportCheck(false, byIndex.Count, anchors.Count,
                    $"The record at position {anchor.ChainIndex} is not the one anchored on {anchor.AnchoredAt:u}: the chain was made again.");
            }
        }

        return new AuditExportCheck(true, byIndex.Count, anchors.Count,
            $"Positions {export.FromIndex:N0} to {export.ToIndex:N0}: all {byIndex.Count:N0} records are intact and linked" +
            (anchors.Count == 0 ? ". No anchor falls in this range." : $", and match the {anchors.Count:N0} anchor(s) in range."));
    }
}
