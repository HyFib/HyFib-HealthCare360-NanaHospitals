using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// A cancellation, a return or a ward return of a pharmacy record waiting for a second person: the request, what it asks,
/// who asked and when. Every reader of the record sees it, so nobody asks twice or expects the stock or the money to have
/// moved; <see cref="AskedByYou"/> marks the reader's own request, which they may open and withdraw.
/// </summary>
public sealed record PendingApproval(Guid ApprovalId, string Kind, string Summary, string RequestedBy, DateTimeOffset RequestedAt, bool AskedByYou);

internal static class PendingApprovals
{
    /// <summary>The requests of these kinds about one record still waiting for a decision (not decided, withdrawn or expired), oldest first.</summary>
    public static async Task<IReadOnlyList<PendingApproval>> ForAsync(
        HospitalDbContext db, string subjectType, Guid subjectId, IReadOnlyCollection<string> kinds, Guid? reader, DateTimeOffset now, CancellationToken ct) =>
        await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.SubjectType == subjectType && r.SubjectId == subjectId && kinds.Contains(r.Kind) && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new PendingApproval(r.Id, r.Kind, r.Summary, r.RequestedByName, r.RequestedAt, r.RequestedBy == reader))
            .ToListAsync(ct);
}
