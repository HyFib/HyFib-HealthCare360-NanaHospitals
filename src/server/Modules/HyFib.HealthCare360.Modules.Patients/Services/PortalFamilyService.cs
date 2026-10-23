using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Family members on other mobile numbers in the patient portal (PP-01). A link is made at the front desk, or by the
/// account holder with a code sent to the family member's own number; each side can see the link and end it.
/// <see cref="PortalPatientScope"/> reads the links in force, so a link made or ended takes effect at once.
/// </summary>
public sealed class PortalFamilyService(
    HospitalDbContext db,
    PortalPatientScope scope,
    PatientAccessGuard guard,
    PatientContactAccess contacts,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>Most codes one account may ask for in an hour.</summary>
    internal const int CodesPerAccountPerHour = 5;

    /// <summary>Most codes one mobile number may receive in 15 minutes.</summary>
    internal const int CodesPerNumberPer15Minutes = 3;

    // ------------------------------------------------------------------ front desk

    /// <summary>Who can see the patient's records in the portal, for the patient's record in the staff app.</summary>
    public async Task<Result<PatientPortalAccessDto>> AccessAsync(Guid patientId, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var ownPhone = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.Phone).FirstOrDefaultAsync(ct);
        var links = await db.Set<PortalFamilyLink>().AsNoTracking()
            .Where(l => l.PatientId == patientId)
            .OrderBy(l => l.RevokedAt != null)
            .ThenByDescending(l => l.LinkedAt)
            .ToListAsync(ct);
        var userIds = links.SelectMany(l => new[] { l.LinkedByUserId, l.RevokedByUserId }).OfType<Guid>().Distinct().ToList();
        var names = await db.Set<User>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        await audit.PatientRecordAccessedAsync(patientId, "Viewed who can see the record in the patient portal", ct);

        // The patient's number and their family's, in full only for those who contact or bill patients (SRS PRV-03).
        var full = await contacts.FullAsync(ct);
        return new PatientPortalAccessDto(PatientContacts.Phone(ownPhone, full), links
            .Select(l => new PortalFamilyLinkDto(
                l.Id, PatientContacts.Phone(l.AccountPhone, full)!, l.Relation, l.Source, l.LinkedAt, DisplayNameOf(l.LinkedByUserId), l.ConsentGivenBy,
                PatientContacts.Phone(l.CodeSentTo, full), l.RevokedAt, DisplayNameOf(l.RevokedByUserId), l.RevokedReason, l.IsActive))
            .ToList());

        string? DisplayNameOf(Guid? userId) => userId is { } id ? names.GetValueOrDefault(id) : null;
    }

    /// <summary>
    /// Shares the patient's records with the portal account of a family member's number. Staff have checked who both
    /// people are and record who agreed.
    /// </summary>
    public async Task<Result<FamilyLinkCreated>> LinkAtFrontDeskAsync(Guid patientId, LinkFamilyMemberRequest request, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var patient = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null || !patient.IsActive || patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("portal.family_patient_inactive", "This record is no longer in use, so it cannot be shared.");
        }

        var accountPhone = Phone.Normalize(request.AccountPhone)!;
        if (patient.Phone == accountPhone)
        {
            return Error.Rule("portal.family_same_number",
                "The patient is registered with this mobile number, so its portal account sees the record already.");
        }

        if (await db.Set<PortalFamilyLink>().AnyAsync(l => l.PatientId == patientId && l.AccountPhone == accountPhone && l.RevokedAt == null, ct))
        {
            return Error.Conflict("portal.family_already_linked", "This mobile number can already see the record.");
        }

        var link = new PortalFamilyLink
        {
            AccountPhone = accountPhone,
            PatientId = patientId,
            Relation = request.Relation,
            Source = FamilyLinkSource.FrontDesk,
            LinkedAt = time.GetUtcNow(),
            LinkedByUserId = currentUser.UserId,
            ConsentGivenBy = request.ConsentGivenBy.Trim(),
        };
        db.Add(link);
        Audit(link, "portal.family.linked",
            $"{patient.Uhid} shared with the portal account of {Phone.Mask(accountPhone)} at the front desk; agreed by {link.ConsentGivenBy}");
        await db.SaveChangesAsync(ct);
        return new FamilyLinkCreated(link.Id);
    }

    public async Task<Result> RevokeAtFrontDeskAsync(Guid linkId, string reason, CancellationToken ct)
    {
        var link = await db.Set<PortalFamilyLink>().FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null)
        {
            return Error.NotFound("Family link", linkId);
        }

        var access = await guard.EnsureCanOpenAsync(link.PatientId, ct);
        if (access.IsFailure)
        {
            return access;
        }

        return await EndAsync(link, reason, "at the front desk", ct);
    }

    // ------------------------------------------------------------------ portal

    /// <summary>The family members on other numbers this account sees, and the accounts that see its own patients.</summary>
    public async Task<Result<PortalFamilyDto>> PortalFamilyAsync(CancellationToken ct)
    {
        if (await scope.AccountPhoneAsync(ct) is not { } phone)
        {
            return NotSignedIn;
        }

        var own = await scope.OwnNumberPatientIdsAsync(ct);
        var linked = await (
                from l in db.Set<PortalFamilyLink>().AsNoTracking()
                join p in db.Set<Patient>() on l.PatientId equals p.Id
                where l.AccountPhone == phone && l.RevokedAt == null && p.IsActive && p.MergedIntoPatientId == null
                orderby l.LinkedAt
                select new PortalLinkedRelativeDto(l.Id, p.Id, p.FullName, p.Uhid, l.Relation, l.Source, l.LinkedAt))
            .ToListAsync(ct);
        var sharedWith = await (
                from l in db.Set<PortalFamilyLink>().AsNoTracking()
                join p in db.Set<Patient>() on l.PatientId equals p.Id
                where own.Contains(l.PatientId) && l.RevokedAt == null
                orderby l.LinkedAt
                select new { l.Id, l.PatientId, p.FullName, l.AccountPhone, l.Relation, l.LinkedAt })
            .ToListAsync(ct);

        return new PortalFamilyDto(
            linked,
            sharedWith.Select(s => new PortalSharingDto(s.Id, s.PatientId, s.FullName, Phone.Mask(s.AccountPhone), s.Relation, s.LinkedAt)).ToList());
    }

    /// <summary>
    /// Sends a code to the family member's own number when the UHID and the number belong to the same patient. The answer
    /// is the same either way, and both the account and the number are limited in how many codes they get.
    /// </summary>
    public async Task<Result<FamilyCodeSent>> RequestCodeAsync(RequestFamilyCodeRequest request, CancellationToken ct)
    {
        if (await scope.AccountPhoneAsync(ct) is not { } accountPhone)
        {
            return NotSignedIn;
        }

        var target = Phone.Normalize(request.Phone)!;
        if (target == accountPhone)
        {
            return Error.Rule("portal.family_own_number", "Everyone registered with your own number is already in your family.");
        }

        // The limits are counts of codes already saved, so the account's and the number's requests take turns until the
        // new code is saved: requests sent together would otherwise all count the same codes and all pass.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync([LockKey("account", accountPhone), LockKey("number", target)], ct);

        var now = time.GetUtcNow();
        var hourAgo = now.AddHours(-1);
        if (await db.Set<PortalFamilyCode>().CountAsync(c => c.AccountPhone == accountPhone && c.CreatedAt > hourAgo, ct) >= CodesPerAccountPerHour)
        {
            return Error.Rule("portal.family_code_throttled", "You have asked for several codes in the last hour. Please try again later.");
        }

        var windowStart = now.AddMinutes(-15);
        if (await db.Set<PortalFamilyCode>().CountAsync(c => c.TargetPhone == target && c.CreatedAt > windowStart, ct) >= CodesPerNumberPer15Minutes)
        {
            return Error.Rule("portal.family_code_throttled", "Several codes have just been sent to this number. Please wait a few minutes.");
        }

        var uhid = request.Uhid.Trim().ToUpperInvariant();
        var patient = await db.Set<Patient>()
            .Where(p => p.Uhid == uhid && p.Phone == target && p.IsActive && p.MergedIntoPatientId == null)
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(ct);
        if (patient is not null
            && await db.Set<PortalFamilyLink>().AnyAsync(l => l.AccountPhone == accountPhone && l.PatientId == patient.Id && l.RevokedAt == null, ct))
        {
            // Only a family member the account already sees gets this answer.
            return Error.Conflict("portal.family_already_linked", "This family member is already in your family.");
        }

        var code = new PortalFamilyCode
        {
            AccountPhone = accountPhone,
            TargetPhone = target,
            PatientId = patient?.Id,
            Relation = request.Relation,
            CreatedAt = now,
            ExpiresAt = now + PortalFamilyCode.Lifetime,
        };
        if (patient is not null)
        {
            var secret = SecretTokens.NewNumericCode(6);
            code.CodeHash = HashOf(code.Id, secret);
            notifications.Queue(
                "portal.family_code",
                new NotificationRecipient(target, null, patient.Id, null),
                new Dictionary<string, string>
                {
                    ["code"] = secret,
                    ["requester"] = Requester(accountPhone),
                    ["minutes"] = ((int)PortalFamilyCode.Lifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture),
                },
                [NotificationChannel.Sms],
                PortalFamilyCode.Lifetime);
        }

        db.Add(code);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new FamilyCodeSent(code.Id, code.ExpiresAt);
    }

    /// <summary>Makes the link when the account holder enters the code the family member passed on.</summary>
    public async Task<Result<PortalLinkedRelativeDto>> VerifyCodeAsync(Guid codeId, string enteredCode, CancellationToken ct)
    {
        if (await scope.AccountPhoneAsync(ct) is not { } accountPhone)
        {
            return NotSignedIn;
        }

        // Tries of one code take turns, and each is saved before the next is read: tries sent together would otherwise
        // read the same attempt count and save one attempt for all of them, so far more guesses than MaxAttempts.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync([LockKey("code", codeId.ToString("N"))], ct);
        var result = await VerifyTakingTurnsAsync(codeId, enteredCode, accountPhone, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task<Result<PortalLinkedRelativeDto>> VerifyTakingTurnsAsync(Guid codeId, string enteredCode, string accountPhone, CancellationToken ct)
    {
        var code = await db.Set<PortalFamilyCode>().FirstOrDefaultAsync(c => c.Id == codeId && c.AccountPhone == accountPhone, ct);
        if (code is null)
        {
            return Error.Rule("portal.family_code_expired", "This code can no longer be used. Ask for a new one.");
        }

        var now = time.GetUtcNow();
        var verified = code.Verify(HashOf(code.Id, enteredCode.Trim()), now);
        if (verified.IsFailure)
        {
            return verified.Error!;
        }

        var patient = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == verified.Value, ct);
        if (patient is null || !patient.IsActive || patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("portal.family_code_expired", "This code can no longer be used. Ask for a new one.");
        }

        var link = await db.Set<PortalFamilyLink>()
            .FirstOrDefaultAsync(l => l.AccountPhone == accountPhone && l.PatientId == patient.Id && l.RevokedAt == null, ct);
        if (link is null)
        {
            link = new PortalFamilyLink
            {
                AccountPhone = accountPhone,
                PatientId = patient.Id,
                Relation = code.Relation,
                Source = FamilyLinkSource.PortalCode,
                LinkedAt = now,
                LinkedByUserId = currentUser.UserId,
                CodeSentTo = code.TargetPhone,
            };
            db.Add(link);
            Audit(link, "portal.family.linked",
                $"{patient.Uhid} shared with the portal account of {Phone.Mask(accountPhone)} by a code sent to {Phone.Mask(code.TargetPhone)}");
        }

        return new PortalLinkedRelativeDto(link.Id, patient.Id, patient.FullName, patient.Uhid, link.Relation, link.Source, link.LinkedAt);
    }

    /// <summary>
    /// Ends a link from the portal: the account holder stops seeing a family member, or someone signed in with the
    /// patient's own number stops another account seeing the patient's records.
    /// </summary>
    public async Task<Result> EndInPortalAsync(Guid linkId, CancellationToken ct)
    {
        if (await scope.AccountPhoneAsync(ct) is not { } accountPhone)
        {
            return NotSignedIn;
        }

        var link = await db.Set<PortalFamilyLink>().FirstOrDefaultAsync(l => l.Id == linkId && l.RevokedAt == null, ct);
        if (link is null)
        {
            return Error.NotFound("Family link", linkId);
        }

        if (link.AccountPhone == accountPhone)
        {
            return await EndAsync(link, "Removed by the family member in the portal.", "in the portal by the family member", ct);
        }

        // Any other link is none of this account's business unless it shares one of the account's own patients.
        return (await scope.OwnNumberPatientIdsAsync(ct)).Contains(link.PatientId)
            ? await EndAsync(link, "Stopped in the portal from the patient's own number.", "in the portal from the patient's own number", ct)
            : Error.NotFound("Family link", linkId);
    }

    /// <summary>
    /// Ends the family links of a portal account that is closed at the patient's request (DPDP erasure): those that let
    /// the account see other patients, and those that let other accounts see <paramref name="patientId"/>. Saved with the
    /// caller's unit of work.
    /// </summary>
    public async Task<int> EndForClosedAccountAsync(Guid patientId, string accountPhone, CancellationToken ct)
    {
        var links = await db.Set<PortalFamilyLink>()
            .Where(l => l.RevokedAt == null && (l.AccountPhone == accountPhone || l.PatientId == patientId))
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        foreach (var link in links)
        {
            link.Revoke("Ended when the portal account was closed at the patient's request.", currentUser.UserId, now);
            Audit(link, "portal.family.ended",
                $"The portal account of {Phone.Mask(link.AccountPhone)} no longer sees the record; ended when a portal account was closed");
        }

        return links.Count;
    }

    // ------------------------------------------------------------------ shared

    private static Error NotSignedIn => Error.Forbidden("portal.account_required", "Sign in to the patient portal first.");

    private static string HashOf(Guid codeId, string code) => SecretTokens.Hash($"{codeId:N}:{code}");

    /// <summary>
    /// Holds a lock per key until the caller's transaction ends (<c>pg_advisory_xact_lock</c>), in ascending order so two
    /// requests never wait on each other's keys.
    /// </summary>
    private async Task LockAsync(IEnumerable<long> keys, CancellationToken ct)
    {
        foreach (var key in keys.Distinct().Order())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        }
    }

    /// <summary>A lock key for an account, a number or a code; two values sharing a key only wait for each other.</summary>
    private static long LockKey(string kind, string value) =>
        BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"portal.family.{kind}:{value}")));

    /// <summary>How the code's message names the person asking: their name and masked number, or just the number.</summary>
    private string Requester(string accountPhone)
    {
        var masked = Phone.Mask(accountPhone);
        return currentUser.DisplayName is { Length: > 0 } name && name != masked ? $"{name} ({masked})" : masked;
    }

    private async Task<Result> EndAsync(PortalFamilyLink link, string reason, string where, CancellationToken ct)
    {
        var ended = link.Revoke(reason, currentUser.UserId, time.GetUtcNow());
        if (ended.IsFailure)
        {
            return ended;
        }

        Audit(link, "portal.family.ended", $"The portal account of {Phone.Mask(link.AccountPhone)} no longer sees the record; ended {where}");
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private void Audit(PortalFamilyLink link, string action, string description) =>
        audit.Add(new AuditEntry(AuditCategories.Data, action, description)
        {
            PatientId = link.PatientId,
            EntityType = nameof(PortalFamilyLink),
            EntityId = link.Id.ToString(),
        });
}
