using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>Reusable patient look-ups for this and higher-layer modules.</summary>
public static class PatientQueries
{
    /// <summary>
    /// Front-desk "type anything" search: UHID (exact/prefix), mobile number, ABHA (blind index), UHID/mobile
    /// suffix, or name words (each word must match; served by the trigram index).
    /// </summary>
    public static IQueryable<Patient> Search(this IQueryable<Patient> patients, string? term, IFieldProtector protector)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return patients;
        }

        term = term.Trim();
        var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
        var hasLetters = term.Any(char.IsLetter);

        if (hasLetters && digits.Length >= 3 && !term.Contains(' ', StringComparison.Ordinal))
        {
            var uhid = term.ToUpperInvariant();
            return patients.Where(p => p.Uhid == uhid || p.Uhid.StartsWith(uhid));
        }

        if (!hasLetters && digits.Length >= 10)
        {
            var phone = Phone.Normalize(digits);
            var abhaIndexes = digits.Length == 14 ? protector.BlindIndexes(digits).ToArray() : null;
            var aadhaarIndex = digits.Length == 12 ? protector.FixedBlindIndex(digits) : null;
            return patients.Where(p => (phone != null && (p.Phone == phone || p.AlternatePhone == phone))
                                       || (abhaIndexes != null && p.AbhaNumberIndex != null && abhaIndexes.Contains(p.AbhaNumberIndex))
                                       || (aadhaarIndex != null && p.AadhaarIndex == aadhaarIndex));
        }

        if (!hasLetters && digits.Length >= 4)
        {
            return patients.Where(p => p.Uhid.EndsWith(digits) || (p.Phone != null && p.Phone.EndsWith(digits)));
        }

        foreach (var word in term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4))
        {
            var like = word.ToLikePattern();
            patients = patients.Where(p => EF.Functions.ILike(p.FullName, like, QueryExtensions.LikeEscape));
        }

        return patients;
    }

    /// <summary>
    /// Summaries for a set of patients (age is computed in memory), their phone numbers in full or masked as the caller may see
    /// them (<see cref="PatientContactAccess"/>, SRS PRV-03).
    /// </summary>
    public static async Task<Dictionary<Guid, PatientSummary>> SummariesAsync(
        HospitalDbContext db, IEnumerable<Guid> ids, DateOnly today, bool fullContacts, CancellationToken ct)
    {
        var idList = ids.Distinct().ToList();
        var rows = await db.Set<Patient>().AsNoTracking()
            .Where(p => idList.Contains(p.Id))
            .Select(p => new { p.Id, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, p.Phone, p.BloodGroup, p.Category, p.IsVip, p.IsDeceased, p.PhotoFileId })
            .ToListAsync(ct);

        return rows.ToDictionary(p => p.Id, p => new PatientSummary(
            p.Id, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, p.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            PatientContacts.Phone(p.Phone, fullContacts), p.BloodGroup, p.Category, p.IsVip, p.IsDeceased, p.PhotoFileId));
    }

    public static DateOnly Today(this TimeProvider time) => DateOnly.FromDateTime(IndiaTime(time).DateTime);

    /// <summary>Current time in the hospital's time zone (IST by default).</summary>
    public static DateTimeOffset IndiaTime(TimeProvider time)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        }
        catch (TimeZoneNotFoundException)
        {
            return time.GetUtcNow();
        }
    }
}

/// <summary>
/// Enforces restricted-record rules (EHR-07): VIP/confidential patients can be opened only with the
/// <c>patients.restricted.read</c> permission or after an audited, time-boxed break-the-glass request.
/// Higher modules (EMR, billing, lab) should call <see cref="EnsureCanOpenAsync"/> before showing PHI. A break-the-glass
/// grant is a row in <c>patients.break_glass_grants</c>, so it holds on whichever API node answers (SCAL-01).
/// </summary>
public sealed class PatientAccessGuard(
    HospitalDbContext db,
    ICurrentUser currentUser,
    IPermissionService permissions,
    IAuditLogger audit,
    INotificationService notifications,
    TimeProvider time) : IPatientRecordAccess
{
    private static readonly TimeSpan GrantDuration = TimeSpan.FromMinutes(30);

    public async Task<Result> EnsureCanOpenAsync(Guid patientId, CancellationToken ct)
    {
        var isVip = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => (bool?)p.IsVip).FirstOrDefaultAsync(ct);
        return isVip switch
        {
            null => Error.NotFound(nameof(Patient), patientId),
            false => Result.Success(),
            true => await CheckRestrictedAsync(patientId, ct),
        };
    }

    public async Task<Result<BreakGlassGrant>> BreakGlassAsync(Guid patientId, string reason, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => new { p.Id, p.Uhid, p.IsVip }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "Sign in first.");
        }

        var now = time.GetUtcNow();
        var expires = now + GrantDuration;

        // The grant and its audit entry are saved together: no emergency access without the record of it.
        db.Add(new BreakGlassAccess { PatientId = patientId, UserId = userId, GrantedAt = now, ExpiresAt = expires });
        audit.Add(new AuditEntry(AuditCategories.Security, "patient.break_glass", reason)
        {
            PatientId = patientId,
            EntityType = nameof(Patient),
            EntityId = patientId.ToString(),
            Severity = AuditSeverity.Critical,
        });
        await db.SaveChangesAsync(ct);

        await notifications.NotifyPermissionHoldersAsync(
            PlatformPermissions.AuditRead,
            currentUser.FacilityId,
            "Emergency access to a restricted record",
            $"{currentUser.DisplayName} opened restricted record {patient.Uhid}. Reason: {reason}",
            $"/admin/audit?patientId={patientId}",
            "security",
            "critical",
            ct);

        return new BreakGlassGrant(patientId, expires);
    }

    private async Task<Result> CheckRestrictedAsync(Guid patientId, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "Sign in first.");
        }

        var now = time.GetUtcNow();
        if (await permissions.HasPermissionAsync(userId, PatientsPermissions.ReadRestricted, ct)
            || await db.Set<BreakGlassAccess>().AnyAsync(g => g.PatientId == patientId && g.UserId == userId && g.ExpiresAt > now, ct))
        {
            return Result.Success();
        }

        return new Error("patient.restricted", "This is a restricted record. Request emergency access (break-the-glass) if it is clinically necessary.", ErrorType.Forbidden)
        {
            Data = new { breakGlassAvailable = true },
        };
    }
}

/// <summary>Links a patient-portal sign-in (mobile OTP) to the earliest active patient record with that mobile number.</summary>
internal sealed class PortalAccountResolver(HospitalDbContext db) : IPortalAccountResolver
{
    public async Task<PortalAccount?> ResolveAsync(string phoneE164, CancellationToken cancellationToken)
    {
        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Phone == phoneE164 && p.IsActive && p.MergedIntoPatientId == null)
            .OrderBy(p => p.RegisteredAt)
            .Select(p => new { p.Id, p.FullName })
            .FirstOrDefaultAsync(cancellationToken);
        return patient is null ? null : new PortalAccount(patient.Id, patient.FullName);
    }
}

/// <summary>The free-text preferred language on a patient's record, for the text HC360 writes for them (PP-06).</summary>
internal sealed class PatientLanguageSource(HospitalDbContext db) : IPatientLanguageSource
{
    public Task<string?> PreferredLanguageAsync(Guid patientId, CancellationToken cancellationToken) =>
        db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => p.PreferredLanguage).FirstOrDefaultAsync(cancellationToken);
}
