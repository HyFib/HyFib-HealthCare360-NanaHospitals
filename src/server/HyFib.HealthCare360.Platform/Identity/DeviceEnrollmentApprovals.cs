using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>An administration desktop's enrolment code, as asked for: who collects it, the kind, facility and note, and how long it works once approved.</summary>
public sealed record DeviceEnrollmentPayload(Guid RequestedBy, DeviceKind Kind, Guid? FacilityId, string? Note, int ValidMinutes);

/// <summary>
/// An enrolment code the signed-in administrator asked for: waiting for a second administrator (<paramref name="Ready"/>
/// false), or approved and not yet shown, until <paramref name="ExpiresAt"/>.
/// </summary>
public sealed record EnrollmentCodeRequestDto(Guid ApprovalId, DeviceKind Kind, string? Note, int ValidMinutes, bool Ready, DateTimeOffset RequestedAt, DateTimeOffset? ExpiresAt);

/// <summary>
/// Administration works only on an enrolled workstation or tablet (SRS SEC-08), so whoever enrols one decides where the
/// hospital can be administered from. Once one is enrolled, a code for another waits for a second administrator who holds
/// <see cref="PlatformPermissions.GrantsApprove"/> (MAKER-CHECKER.md, slice 4), decided as access given is
/// (<see cref="GrantApprovals.DecideAsync"/>). The first enrolment, at installation, and codes for kiosks, queue displays and
/// lab agents, which administer nothing, are made at once as before.
/// <para>
/// Approved, the code is made for the administrator who asked, who is at the computer: it works once and expires the asked
/// minutes after the approval. It is chosen only when they collect it (<see cref="CollectAsync"/>), and shown to them once,
/// so it is never stored, nor seen by the approver. Until then the code's row holds no hash a code could match
/// (<see cref="Uncollected"/>), and carries the request's id.
/// </para>
/// </summary>
internal sealed class DeviceEnrollmentApprovals(
    HospitalDbContext db, ICurrentUser current, IPermissionService permissions, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "devices.enrollment";

    /// <summary>The requests' subject: the administrator who asked, so each may have one waiting.</summary>
    public const string SubjectType = "DeviceEnrollment";

    /// <summary>Asks a second administrator to approve an enrolment code for an administration desktop.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(CreateEnrollmentCodeRequest request, int minutes, CancellationToken ct)
    {
        var maker = current.UserId ?? Guid.Empty;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var facilityId = request.FacilityId ?? current.FacilityId;
        var facility = facilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        var enrolled = await db.Set<Device>().CountAsync(d => !d.IsRevoked && (d.Kind == DeviceKind.Workstation || d.Kind == DeviceKind.Tablet), ct);
        var kind = Words(request.Kind);
        var name = current.DisplayName ?? current.UserName ?? "the administrator who asked";
        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(SubjectType, maker, null, facility, null),
            new DeviceEnrollmentPayload(maker, request.Kind, facilityId, note, minutes),
            note is null ? $"Enrolment code for a {kind}" : $"Enrolment code for a {kind}: {note}",
            [
                new("Device", kind),
                new("Note", note ?? "None"),
                new("Workstations and tablets enrolled now", enrolled.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("What approving does",
                    $"A one-time code is shown to {name} to enrol the computer, and expires {minutes} minutes after you approve. Administration (users, roles, settings, devices, backups) can be used from a {kind} enrolled with it."),
            ],
            note ?? "No reason given.",
            ct);
    }

    /// <summary>
    /// Makes an approved code's row, in the approval's transaction, for the administrator who asked, if they still may enrol
    /// devices; its code is chosen when they collect it.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DeviceEnrollmentPayload>(request.Payload);
        var active = await db.Set<User>().AnyAsync(u => u.Id == asked.RequestedBy && u.IsActive && u.Type == UserType.Staff, ct);
        if (!active || !await permissions.HasPermissionAsync(asked.RequestedBy, PlatformPermissions.DevicesManage, ct))
        {
            return Error.Rule("devices.requester_gone", $"{request.RequestedByName} may no longer enrol devices, so no code was made.");
        }

        var now = time.GetUtcNow();
        db.Add(new DeviceEnrollmentCode
        {
            Id = request.Id,
            CodeHash = Uncollected(request.Id),
            Kind = asked.Kind,
            FacilityId = asked.FacilityId,
            Note = asked.Note,
            CreatedBy = asked.RequestedBy,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(asked.ValidMinutes),
        });
        var approver = current.DisplayName ?? current.UserName;
        audit.Add(new AuditEntry(AuditCategories.Security, "device.enrollment_code_approved",
            $"Enrolment code for a {asked.Kind} asked for by {request.RequestedByName}, approved by {approver} (valid {asked.ValidMinutes} min)")
        {
            EntityType = nameof(DeviceEnrollmentCode),
            EntityId = request.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, requestedBy = asked.RequestedBy, kind = asked.Kind, minutes = asked.ValidMinutes },
        });
        return $"the code is shown to {request.RequestedByName}, and works once within {asked.ValidMinutes} minutes";
    }

    /// <summary>The signed-in administrator's enrolment code requests: waiting, or approved and neither shown nor expired.</summary>
    public async Task<IReadOnlyList<EnrollmentCodeRequestDto>> MineAsync(CancellationToken ct)
    {
        var me = current.UserId ?? Guid.Empty;
        var now = time.GetUtcNow();
        var requests = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == SubjectType && r.RequestedBy == me
                        && ((r.Status == ApprovalStatus.Pending && r.ExpiresAt > now) || r.Status == ApprovalStatus.Approved))
            .OrderByDescending(r => r.RequestedAt)
            .Take(20)
            .ToListAsync(ct);
        var ids = requests.Select(r => r.Id).ToList();
        var codes = await db.Set<DeviceEnrollmentCode>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        List<EnrollmentCodeRequestDto> mine = [];
        foreach (var request in requests)
        {
            var asked = ApprovalPayload.Read<DeviceEnrollmentPayload>(request.Payload);
            if (request.Status == ApprovalStatus.Pending)
            {
                mine.Add(new(request.Id, asked.Kind, asked.Note, asked.ValidMinutes, false, request.RequestedAt, null));
            }
            else if (codes.TryGetValue(request.Id, out var code) && code.CodeHash == Uncollected(request.Id) && code.ExpiresAt > now)
            {
                mine.Add(new(request.Id, asked.Kind, asked.Note, asked.ValidMinutes, true, request.RequestedAt, code.ExpiresAt));
            }
        }

        return mine;
    }

    /// <summary>
    /// The code of an approved request, chosen now and shown once to the administrator who asked for it, until it expires.
    /// Two collections at once take turns under the row's lock, so the code is shown once.
    /// </summary>
    public async Task<Result<EnrollmentCodeResponse>> CollectAsync(Guid approvalId, CancellationToken ct)
    {
        var me = current.UserId ?? Guid.Empty;
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == approvalId && r.Kind == Kind && r.RequestedBy == me, ct);
        if (request is null)
        {
            return Error.NotFound("Enrolment code request", approvalId);
        }

        if (request.Status == ApprovalStatus.Pending)
        {
            return Error.Conflict("devices.enrollment_waiting", "The code waits for another administrator's approval. It is shown here once they approve it.");
        }

        if (request.Status != ApprovalStatus.Approved)
        {
            return Error.Rule("devices.enrollment_not_approved", "This code was not approved, so there is none to show. Ask for a new one.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM identity.device_enrollment_codes WHERE id = {approvalId} FOR NO KEY UPDATE", ct);
        var row = await db.Set<DeviceEnrollmentCode>().FirstOrDefaultAsync(c => c.Id == approvalId, ct);
        if (row is null || row.CodeHash != Uncollected(approvalId))
        {
            return Error.Rule("devices.enrollment_code_shown", "This code was shown already, and is shown only once. If it was lost, ask for a new one.");
        }

        var now = time.GetUtcNow();
        if (row.ExpiresAt <= now)
        {
            return Error.Rule("devices.enrollment_code_expired", "This code expired before it was shown. Ask for a new one.");
        }

        var code = SecretTokens.NewReadableCode(8);
        row.CodeHash = SecretTokens.Hash(code);
        audit.Add(new AuditEntry(AuditCategories.Security, "device.enrollment_code_created",
            $"Enrolment code for a {row.Kind} shown to the administrator who asked for it, approved by {request.DecidedByName} (valid until {row.ExpiresAt:u})")
        {
            EntityType = nameof(DeviceEnrollmentCode),
            EntityId = row.Id.ToString(),
            Details = new { approval = request.Id, approvedBy = request.DecidedBy },
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new EnrollmentCodeResponse(DeviceEndpoints.Readable(code), row.ExpiresAt);
    }

    /// <summary>What an approved code's row holds until it is collected: no code hashes to it (a hash is 64 hexadecimal digits).</summary>
    internal static string Uncollected(Guid approvalId) => $"approved:{approvalId:N}";

    private static string Words(DeviceKind kind) => kind == DeviceKind.Tablet ? "tablet" : "workstation";
}

/// <summary>An administration desktop's enrolment code: one administrator asks, another approves, and the code is shown to the one who asked.</summary>
internal sealed class DeviceEnrollmentApproval(DeviceEnrollmentApprovals enrolments) : IApprovalAction
{
    public string Kind => DeviceEnrollmentApprovals.Kind;

    public string Title => "Administration desktop enrolment";

    public string ApprovePermission => PlatformPermissions.GrantsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => enrolments.ApplyAsync(request, ct);
}
