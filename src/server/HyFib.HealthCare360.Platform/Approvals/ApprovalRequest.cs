using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Approvals;

/// <summary>Where a request for a second person's approval stands.</summary>
public enum ApprovalStatus
{
    /// <summary>Waiting for a checker.</summary>
    Pending = 0,

    /// <summary>A checker approved it and the change was made.</summary>
    Approved,

    /// <summary>A checker refused it; nothing was changed.</summary>
    Rejected,

    /// <summary>The maker took it back before anyone decided; nothing was changed.</summary>
    Withdrawn,

    /// <summary>
    /// A checker approved it, but the change could no longer be made as asked (the record changed after it was asked for,
    /// or a rule now refuses it); nothing was changed, and the maker asks again if it is still needed.
    /// </summary>
    Failed,
}

/// <summary>
/// A change one person (the maker) asks for and another (the checker) must approve before it is made: maker-checker, or
/// four eyes. The request keeps the exact change asked for (<see cref="Payload"/>, with its SHA-256 in
/// <see cref="PayloadHash"/>) and the version of the record it was asked against (<see cref="SubjectVersion"/>), so the
/// checker approves exactly what is then done, and a record that changed in the meantime is not changed on an approval
/// given for its old state. Nobody approves their own request (<see cref="FourEyes"/>), and a request waits at most
/// <see cref="Lifetime"/>. The module that owns the record makes the change (<see cref="IApprovalAction"/>).
/// </summary>
public sealed class ApprovalRequest : AuditableEntity, IFacilityScoped
{
    /// <summary>How long a request may wait for a decision; after that it is asked again, against the record as it is then.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public Guid FacilityId { get; set; }

    /// <summary>What kind of change it is (<see cref="IApprovalAction.Kind"/>), for example <c>billing.credit_note</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The kind in words, as the checker's list shows it.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>One line saying what will be done, for example "Credit note of ₹1,200 on bill NHOPB2600012".</summary>
    public string Summary { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }

    /// <summary>The version (xmin) of the record when the change was asked for; the change is made only on that version.</summary>
    public uint? SubjectVersion { get; set; }

    public Guid? PatientId { get; set; }

    /// <summary>What the checker needs to see to decide, as label and value pairs (JSON).</summary>
    public string Details { get; set; } = "[]";

    /// <summary>The change asked for, exactly as it will be made (JSON).</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>SHA-256 of <see cref="Payload"/>, checked before the change is made.</summary>
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>Why the maker asks for it.</summary>
    public string Reason { get; set; } = string.Empty;

    public ApprovalStatus Status { get; private set; }

    public Guid RequestedBy { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public Guid? DecidedBy { get; private set; }
    public string? DecidedByName { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>The checker's note on an approval, or the reason for a rejection.</summary>
    public string? DecisionNote { get; private set; }

    /// <summary>What the approval made (for example the credit note's number), or why the change could not be made.</summary>
    public string? Outcome { get; private set; }

    public bool IsExpired(DateTimeOffset now) => Status == ApprovalStatus.Pending && now >= ExpiresAt;

    /// <summary>
    /// Whether <paramref name="checkerId"/> may decide this request now: it is still waiting, has not run out of time,
    /// was not asked for by the same person, and has its payload as it was asked for.
    /// </summary>
    public Result CanBeDecidedBy(Guid checkerId, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
        {
            return Error.Rule("approval.not_pending", "This request has already been decided or withdrawn.");
        }

        if (IsExpired(now))
        {
            return Error.Rule("approval.expired", $"This request waited more than {Lifetime.TotalDays:0} days. Ask for it again, against the record as it is now.");
        }

        var fourEyes = FourEyes.Check(RequestedBy, checkerId);
        if (fourEyes.IsFailure)
        {
            return fourEyes;
        }

        return ApprovalPayload.Hash(Payload) == PayloadHash
            ? Result.Success()
            : Error.Rule("approval.altered", "This request was changed after it was asked for, so it cannot be approved. Ask for it again.");
    }

    public void MarkApproved(Guid checkerId, string checkerName, string? note, string outcome, DateTimeOffset now)
    {
        Decide(ApprovalStatus.Approved, checkerId, checkerName, note, now);
        Outcome = outcome;
    }

    public void MarkRejected(Guid checkerId, string checkerName, string reason, DateTimeOffset now) =>
        Decide(ApprovalStatus.Rejected, checkerId, checkerName, reason, now);

    /// <summary>The checker approved it but the change could no longer be made; <paramref name="why"/> says why.</summary>
    public void MarkFailed(Guid checkerId, string checkerName, string? note, string why, DateTimeOffset now)
    {
        Decide(ApprovalStatus.Failed, checkerId, checkerName, note, now);
        Outcome = why;
    }

    public Result Withdraw(Guid userId, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
        {
            return Error.Rule("approval.not_pending", "This request has already been decided or withdrawn.");
        }

        if (RequestedBy != userId)
        {
            return Error.Forbidden("approval.not_maker", "Only the person who asked for this change can withdraw it.");
        }

        Status = ApprovalStatus.Withdrawn;
        DecidedBy = userId;
        DecidedByName = RequestedByName;
        DecidedAt = now;
        return Result.Success();
    }

    private void Decide(ApprovalStatus status, Guid checkerId, string checkerName, string? note, DateTimeOffset now)
    {
        // A request is decided once. The service checks first (CanBeDecidedBy, and again before keeping a failure), so
        // this only stops a decision being written over another one.
        if (Status != ApprovalStatus.Pending)
        {
            throw new InvalidOperationException($"Approval request {Id} is {Status} already; it is not {status} as well.");
        }

        Status = status;
        DecidedBy = checkerId;
        DecidedByName = checkerName;
        DecidedAt = now;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }
}

/// <summary>
/// Separation of duties: the person who makes an entry does not approve it. Checked by user, so it holds however many
/// roles the person has (a hospital administrator holds both sides of most controls); it relies on everyone signing in as
/// themselves, which the rest of the system already requires.
/// </summary>
public static class FourEyes
{
    public static Result Check(Guid? makerId, Guid checkerId) =>
        makerId is { } maker && maker == checkerId
            ? Error.Forbidden("approval.self", "A change you asked for must be approved by someone else.")
            : Result.Success();
}

/// <summary>The stored form of a requested change: JSON written once, and its SHA-256.</summary>
public static class ApprovalPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    public static T Read<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, Options) ?? throw new InvalidOperationException("The approval request has no payload.");

    public static string Hash(string payload) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> b)
    {
        b.ToTable("approval_requests", "platform");
        b.Property(x => x.Kind).HasMaxLength(64);
        b.Property(x => x.Title).HasMaxLength(100);
        b.Property(x => x.Summary).HasMaxLength(300);
        b.Property(x => x.SubjectType).HasMaxLength(64);
        b.Property(x => x.Details).HasColumnType("jsonb");

        // Text, not jsonb: jsonb reorders keys, and the payload must read back as the exact text its hash was taken of.
        b.Property(x => x.Payload).HasColumnType("text");
        b.Property(x => x.PayloadHash).HasMaxLength(64);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.DecidedByName).HasMaxLength(200);
        b.Property(x => x.DecisionNote).HasMaxLength(500);
        b.Property(x => x.Outcome).HasMaxLength(500);
        b.HasIndex(x => new { x.FacilityId, x.Status, x.Kind });
        b.HasIndex(x => new { x.SubjectType, x.SubjectId });
        b.HasIndex(x => x.RequestedBy);
    }
}
