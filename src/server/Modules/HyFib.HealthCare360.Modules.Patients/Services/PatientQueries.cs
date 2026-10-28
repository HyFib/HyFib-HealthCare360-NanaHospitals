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
            // No null check on the phone: SQL's LIKE is already false for none, and the check would let PostgreSQL pick
            // "has a phone" (nearly everyone) over the trigram index.
            return patients.Where(p => p.Uhid.EndsWith(digits) || p.Phone!.EndsWith(digits));
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
    public static DateTimeOffset IndiaTime(TimeProvider time) => TimeZoneInfo.ConvertTime(time.GetUtcNow(), HospitalTimeZone.Zone);
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

    /// <summary>
    /// Whether the user may serve the patient: see their details, bills, orders, results in the departments. Only a
    /// restricted (VIP) record is closed here; sensitive categories close the clinical record (<see cref="EnsureCanOpenChartAsync"/>).
    /// </summary>
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

    /// <summary>
    /// Whether the user may open the patient's clinical record (notes, diagnoses, care, documents): closed for a restricted
    /// (VIP) record as <see cref="EnsureCanOpenAsync"/> is, and for a record in a sensitive category (HIV, mental health,
    /// MTP) unless the user holds the permission of each category it is in (R-4). Break-the-glass opens both.
    /// </summary>
    public async Task<Result> EnsureCanOpenChartAsync(Guid patientId, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => new { p.IsVip, p.SensitiveCategories }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (patient.IsVip)
        {
            return await CheckRestrictedAsync(patientId, ct);
        }

        if (patient.SensitiveCategories == SensitiveCategory.None)
        {
            return Result.Success();
        }

        if (currentUser.UserId is { } userId && await HoldsCategoriesAsync(userId, patient.SensitiveCategories, ct))
        {
            return Result.Success();
        }

        var restricted = await CheckRestrictedAsync(patientId, ct);
        return restricted.IsSuccess
            ? restricted
            : new Error("patient.restricted",
                $"This record is confidential ({SensitiveCategories.Describe(patient.SensitiveCategories)}). Request emergency access (break-the-glass) if it is clinically necessary.",
                ErrorType.Forbidden)
            {
                Data = new { breakGlassAvailable = true },
            };
    }

    // Files are documents of the clinical record (reports, scans, letters): the chart's rules apply to them.
    Task<Result> IPatientRecordAccess.EnsureCanOpenAsync(Guid patientId, CancellationToken cancellationToken) =>
        EnsureCanOpenChartAsync(patientId, cancellationToken);

    /// <summary>
    /// Puts the record in the categories of a diagnosis (<see cref="SensitiveCategories.FromIcd"/>), as the diagnosis is
    /// recorded; nothing for an ordinary one. The change is saved with the caller's unit of work and audited.
    /// </summary>
    public async Task FlagFromDiagnosisAsync(Guid patientId, string? icdCode, CancellationToken ct)
    {
        var category = SensitiveCategories.FromIcd(icdCode);
        if (category != SensitiveCategory.None)
        {
            await FlagAsync(patientId, category, $"a diagnosis ({icdCode!.Trim().ToUpperInvariant()})", ct);
        }
    }

    /// <summary>
    /// Puts the record in <paramref name="category"/> because of <paramref name="source"/> ("an MTP case"), if it is not
    /// already. The change is saved with the caller's unit of work and audited.
    /// </summary>
    public async Task FlagAsync(Guid patientId, SensitiveCategory category, string source, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null || patient.SensitiveCategories.HasFlag(category))
        {
            return;
        }

        patient.SensitiveCategories |= category;
        audit.Add(new AuditEntry(AuditCategories.Security, "patient.sensitive_flagged",
            $"Record put in the {SensitiveCategories.Describe(category)} category by {source}")
        {
            PatientId = patientId,
            EntityType = nameof(Patient),
            EntityId = patientId.ToString(),
        });
    }

    private async Task<bool> HoldsCategoriesAsync(Guid userId, SensitiveCategory categories, CancellationToken ct)
    {
        foreach (var (category, permission) in CategoryPermissions)
        {
            if (categories.HasFlag(category) && !await permissions.HasPermissionAsync(userId, permission, ct))
            {
                return false;
            }
        }

        return true;
    }

    internal static readonly (SensitiveCategory Category, string Permission)[] CategoryPermissions =
    [
        (SensitiveCategory.Hiv, PatientsPermissions.SensitiveHiv),
        (SensitiveCategory.MentalHealth, PatientsPermissions.SensitiveMentalHealth),
        (SensitiveCategory.Mtp, PatientsPermissions.SensitiveMtp),
    ];

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

    /// <summary>
    /// Which of these restricted records the user may open: all of them with the restricted-read permission, otherwise
    /// those with emergency access still running. Lists show the others' names only as initials (R-3).
    /// </summary>
    public async Task<IReadOnlySet<Guid>> OpenableAsync(IReadOnlyCollection<Guid> restrictedIds, CancellationToken ct)
    {
        if (restrictedIds.Count == 0 || currentUser.UserId is not { } userId)
        {
            return new HashSet<Guid>();
        }

        if (await permissions.HasPermissionAsync(userId, PatientsPermissions.ReadRestricted, ct))
        {
            return restrictedIds.ToHashSet();
        }

        var now = time.GetUtcNow();
        return (await db.Set<BreakGlassAccess>().Where(g => g.UserId == userId && g.ExpiresAt > now && restrictedIds.Contains(g.PatientId))
            .Select(g => g.PatientId).ToListAsync(ct)).ToHashSet();
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
