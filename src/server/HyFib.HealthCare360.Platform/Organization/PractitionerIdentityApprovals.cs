using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Organization;

/// <summary>What identifies a practitioner on what they sign: council registration number, HPR ID and signature image.</summary>
public sealed record PractitionerIdentity(string? RegistrationNumber, string? HprId, Guid? SignatureFileId);

/// <summary>A practitioner's identity, the one the change replaces and the one asked for.</summary>
public sealed record PractitionerIdentityPayload(Guid PractitionerId, PractitionerIdentity Before, PractitionerIdentity Asked);

/// <summary>A practitioner's identity waiting for a second administrator: its request, what is asked, who asked and when.</summary>
public sealed record PendingPractitionerIdentity(Guid ApprovalId, PractitionerIdentity Asked, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// A practitioner's registration number, HPR ID and signature image print on the prescriptions, certificates and discharge
/// summaries an account linked to them signs, and identify them in ABDM records (MAKER-CHECKER.md, slice 4). For a
/// practitioner someone signs in as, they change only when a second administrator who holds
/// <see cref="PlatformPermissions.GrantsApprove"/> agrees, decided as access given is (<see cref="GrantApprovals.DecideAsync"/>);
/// the rest of the record is saved at once, and the identity stays as it was until then. A practitioner with no account is
/// edited at once, as before: nothing is signed in their name. The approver reads the before and after in words, and the
/// change is made only over the identity it replaces (<c>approval.stale</c>).
/// </summary>
internal sealed class PractitionerIdentityApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, GrantApprovals grants, TimeProvider time)
{
    public const string Kind = "masters.practitioner_identity";

    private const string NoReason = "No reason given.";

    /// <summary>The practitioner's identity as it is now.</summary>
    public static PractitionerIdentity Of(Practitioner practitioner)
    {
        ArgumentNullException.ThrowIfNull(practitioner);
        return new(practitioner.RegistrationNumber, practitioner.HprId, practitioner.SignatureFileId);
    }

    /// <summary>The staff accounts linked to the practitioner, active or not: who signs as them.</summary>
    public Task<List<string>> LinkedAccountsAsync(Guid practitionerId, CancellationToken ct) =>
        db.Set<User>().AsNoTracking()
            .Where(u => u.PractitionerId == practitionerId && u.Type == UserType.Staff)
            .OrderBy(u => u.DisplayName)
            .Select(u => u.DisplayName + " (" + u.UserName + ")")
            .ToListAsync(ct);

    /// <summary>
    /// Whether a change to the identity waits, is made at once (a hospital that has never had two people who may approve),
    /// or is refused. A doctor changing their own waits for someone else.
    /// </summary>
    public Task<Result<GrantDecision>> DecideAsync(Guid practitionerId, CancellationToken ct) =>
        grants.DecideAsync(self: current.PractitionerId == practitionerId, [], ct);

    /// <summary>Audits an identity changed at once, as <paramref name="decision"/> allowed it.</summary>
    public void AuditMadeAlone(GrantDecision decision, Practitioner practitioner) =>
        grants.AuditMadeAlone(decision, nameof(Practitioner), practitioner.Id, $"registration number, HPR ID or signature of {practitioner.DisplayName}, who has an account");

    /// <summary>The identity waiting for approval for this practitioner, if any.</summary>
    public async Task<PendingPractitionerIdentity?> PendingAsync(Guid practitionerId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Practitioner) && r.SubjectId == practitionerId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Payload, r.RequestedByName, r.RequestedAt })
            .FirstOrDefaultAsync(ct);
        return request is null
            ? null
            : new PendingPractitionerIdentity(request.Id, ApprovalPayload.Read<PractitionerIdentityPayload>(request.Payload).Asked, request.RequestedByName, request.RequestedAt);
    }

    /// <summary>Asks a second administrator to change <paramref name="practitioner"/>'s identity, in the caller's transaction, once the rest is saved.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(
        Practitioner practitioner, PractitionerIdentity before, PractitionerIdentity asked, IReadOnlyList<string> linked, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(practitioner);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(asked);
        List<ApprovalDetail> details =
        [
            new("Practitioner", $"{practitioner.DisplayName} ({practitioner.Code})"),
            new("Signed in as by", string.Join(", ", linked)),
        ];
        List<string> parts = [];
        if (!string.Equals(before.RegistrationNumber, asked.RegistrationNumber, StringComparison.Ordinal))
        {
            details.Add(new("Registration number", $"{before.RegistrationNumber ?? "none"} → {asked.RegistrationNumber ?? "none"}"));
            parts.Add($"registration number {asked.RegistrationNumber ?? "removed"}");
        }

        if (!string.Equals(before.HprId, asked.HprId, StringComparison.Ordinal))
        {
            details.Add(new("HPR ID", $"{before.HprId ?? "none"} → {asked.HprId ?? "none"}"));
            parts.Add($"HPR ID {asked.HprId ?? "removed"}");
        }

        if (before.SignatureFileId != asked.SignatureFileId)
        {
            var words = (before.SignatureFileId, asked.SignatureFileId) switch
            {
                (null, _) => "none → a signature image uploaded now",
                (_, null) => "the signature image taken away",
                _ => "replaced with a signature image uploaded now",
            };
            details.Add(new("Signature image", words));
            parts.Add(asked.SignatureFileId is null ? "signature image removed" : "a new signature image");
        }

        details.Add(new("What it reaches", "Prints on the prescriptions, certificates and discharge summaries they sign, and identifies them in ABDM records."));
        var facility = current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Practitioner), practitioner.Id, practitioner.Version, facility, null),
            new PractitionerIdentityPayload(practitioner.Id, before, asked),
            Fit($"{practitioner.DisplayName}: {string.Join("; ", parts)}", 300),
            details,
            NoReason,
            ct);
    }

    /// <summary>
    /// Sets an approved identity, in the approval's transaction, if the practitioner still has the identity the request
    /// replaces and a new signature image is still one that may be used.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PractitionerIdentityPayload>(request.Payload);
        var practitioner = await db.Set<Practitioner>().FirstOrDefaultAsync(p => p.Id == asked.PractitionerId, ct);
        if (practitioner is null)
        {
            return Error.Rule("practitioner.gone", "The practitioner no longer exists, so nothing was changed.");
        }

        // Only the identity is compared: a fee or a department changed meanwhile does not stand in the way.
        if (Of(practitioner) != asked.Before)
        {
            return Error.Rule("approval.stale",
                $"{practitioner.DisplayName}'s registration number, HPR ID or signature changed after this was asked for, so nothing was changed. Ask again, for the record as it is now.");
        }

        if (await OrganizationEndpoints.ImageProblemAsync(db, asked.Asked.SignatureFileId, practitioner.SignatureFileId, MasterImages.Signature, ct) is { } problem)
        {
            return Error.Rule("practitioner.signature_unusable", $"The signature image asked for can no longer be used ({problem}), so nothing was changed.");
        }

        practitioner.RegistrationNumber = asked.Asked.RegistrationNumber;
        practitioner.HprId = asked.Asked.HprId;
        practitioner.SignatureFileId = asked.Asked.SignatureFileId;
        audit.Add(new AuditEntry(AuditCategories.Admin, "practitioner.identity_changed",
            Fit($"Registration number, HPR ID or signature of {practitioner.DisplayName} changed: {request.Summary} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(Practitioner),
            EntityId = practitioner.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { before = asked.Before, after = asked.Asked, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return $"{practitioner.DisplayName}'s identity changed";
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>The identity of a practitioner someone signs in as: one administrator asks, another approves.</summary>
internal sealed class PractitionerIdentityApproval(PractitionerIdentityApprovals identities) : IApprovalAction
{
    public string Kind => PractitionerIdentityApprovals.Kind;

    public string Title => "Practitioner's registration and signature";

    public string ApprovePermission => PlatformPermissions.GrantsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => identities.ApplyAsync(request, ct);
}
