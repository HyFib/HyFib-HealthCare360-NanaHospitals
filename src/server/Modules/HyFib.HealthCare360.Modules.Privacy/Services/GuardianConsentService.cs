using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

public sealed record GuardianConsentDto(
    Guid Id, string GuardianName, GuardianRelationship Relationship, string GuardianPhone, bool GuardianIsAdult, GuardianVerificationMethod Method,
    string? DocumentType, string? DocumentLast4, GuardianConsentStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? CodeExpiresAt, int AttemptsLeft,
    DateTimeOffset? VerifiedAt, DateTimeOffset? RevokedAt, string? RevokedReason);

/// <summary>A patient's standing for DPDP Act s.9: whether they are a child, and the guardian consents recorded for them, newest first.</summary>
public sealed record GuardianConsentView(Guid PatientId, bool IsChild, DateOnly? DateOfBirth, bool Verified, IReadOnlyList<GuardianConsentDto> Consents);

/// <param name="DocumentLast4">The identity document's last four characters only; required with <see cref="GuardianVerificationMethod.Document"/>.</param>
public sealed record StartGuardianConsentRequest(
    string GuardianName, GuardianRelationship Relationship, string GuardianPhone, bool GuardianIsAdult, GuardianVerificationMethod Method,
    string? DocumentType, string? DocumentLast4);

public sealed record VerifyGuardianCodeRequest(string Code);

public sealed record RevokeGuardianConsentRequest(string Reason);

public sealed class StartGuardianConsentRequestValidator : AbstractValidator<StartGuardianConsentRequest>
{
    public StartGuardianConsentRequestValidator()
    {
        RuleFor(x => x.GuardianName).NotEmpty().WithMessage("Enter the parent's or guardian's name.").SafeText(200);
        RuleFor(x => x.Relationship).IsInEnum();
        RuleFor(x => x.GuardianPhone).NotEmpty().WithMessage("Enter the guardian's mobile number.").PhoneNumber();
        RuleFor(x => x.GuardianIsAdult).Equal(true).WithMessage("Confirm the parent or guardian is an adult: rule 10 of the DPDP Rules asks the hospital to check.");
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.DocumentType).NotEmpty().WithMessage("Say which identity document of the guardian's you saw.")
            .When(x => x.Method == GuardianVerificationMethod.Document).SafeText(60);
        RuleFor(x => x.DocumentLast4).NotEmpty().WithMessage("Enter the document number's last four characters.")
            .Matches("^[A-Za-z0-9]{4}$").WithMessage("Enter only the last four letters or digits of the document number, never the whole number.")
            .When(x => x.Method == GuardianVerificationMethod.Document);
        RuleFor(x => x.DocumentLast4).Empty().When(x => x.Method == GuardianVerificationMethod.Otp);
    }
}

public sealed class VerifyGuardianCodeRequestValidator : AbstractValidator<VerifyGuardianCodeRequest>
{
    public VerifyGuardianCodeRequestValidator() =>
        RuleFor(x => x.Code).NotEmpty().Matches("^[0-9]{6}$").WithMessage("Enter the 6-digit code the guardian received.");
}

public sealed class RevokeGuardianConsentRequestValidator : AbstractValidator<RevokeGuardianConsentRequest>
{
    public RevokeGuardianConsentRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say why the consent is withdrawn.").SafeText(300);
}

/// <summary>
/// Records and verifies a parent's or lawful guardian's consent for a child (DPDP Act 2023 s.9(1); DPDP Rules 2025,
/// rule 10): by a code sent to the guardian's own mobile number, or by their identity document seen at the desk. Its
/// <see cref="HasVerifiedAsync"/> is what the consent screens and the consent managers check before recording anything
/// outside health care for a child (<see cref="ChildData"/>).
/// </summary>
public sealed class GuardianConsentService(
    HospitalDbContext db,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time) : IGuardianConsents
{
    public const string CodeTemplate = "privacy.guardian.code";
    public const string NotAChildCode = "privacy.not_a_child";
    public const string ThrottledCode = "privacy.guardian_code_throttled";

    public async Task<bool> HasVerifiedAsync(Guid patientId, CancellationToken ct) =>
        await db.Set<GuardianConsent>().AnyAsync(c => c.PatientId == patientId && c.Status == GuardianConsentStatus.Verified, ct);

    public async Task<GuardianConsentView?> GetAsync(Guid patientId, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => new { p.DateOfBirth }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var consents = await db.Set<GuardianConsent>().AsNoTracking().Where(c => c.PatientId == patientId).OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        return new GuardianConsentView(patientId, ChildData.IsChild(patient.DateOfBirth, Today()), patient.DateOfBirth,
            consents.Any(c => c.Status == GuardianConsentStatus.Verified), [.. consents.Select(c => Dto(c, now))]);
    }

    /// <summary>
    /// Records the guardian. By document it is verified at once by the member of staff who saw it; by code, the code goes to
    /// the guardian's number and the consent waits for it (<see cref="VerifyAsync"/>).
    /// </summary>
    public async Task<Result<GuardianConsentDto>> StartAsync(Guid patientId, StartGuardianConsentRequest request, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().Where(p => p.Id == patientId)
            .Select(p => new { p.DateOfBirth, p.RegisteredFacilityId, p.PreferredLanguage, p.FullName }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (!ChildData.IsChild(patient.DateOfBirth, Today()))
        {
            return Error.Rule(NotAChildCode, patient.DateOfBirth is null
                ? "The patient's date of birth is not recorded. Record it first: a guardian's consent is for a patient under 18."
                : "The patient is 18 or older and gives consent for themselves.");
        }

        var phone = Phone.Normalize(request.GuardianPhone)!;
        var now = time.GetUtcNow();
        if (request.Method == GuardianVerificationMethod.Otp
            && await db.Set<GuardianConsent>().CountAsync(c => c.GuardianPhone == phone && c.Method == GuardianVerificationMethod.Otp && c.CreatedAt > now - GuardianConsent.ThrottleWindow, ct)
               >= GuardianConsent.MaxCodesPerNumber)
        {
            return Error.Rule(ThrottledCode, "Several codes were sent to this number in the last few minutes. Wait a quarter of an hour, or verify by the guardian's identity document.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var pending in await db.Set<GuardianConsent>().Where(c => c.PatientId == patientId && c.Status == GuardianConsentStatus.Pending).ToListAsync(ct))
        {
            pending.Status = GuardianConsentStatus.Expired;
            pending.CodeHash = null;
        }

        var consent = new GuardianConsent
        {
            PatientId = patientId,
            GuardianName = request.GuardianName.Trim(),
            Relationship = request.Relationship,
            GuardianPhone = phone,
            GuardianIsAdult = request.GuardianIsAdult,
            Method = request.Method,
            DocumentType = request.Method == GuardianVerificationMethod.Document ? request.DocumentType?.Trim() : null,
            DocumentLast4 = request.Method == GuardianVerificationMethod.Document ? request.DocumentLast4?.Trim().ToUpperInvariant() : null,
        };

        if (request.Method == GuardianVerificationMethod.Document)
        {
            await ReplaceVerifiedAsync(patientId, now, ct);
            consent.Status = GuardianConsentStatus.Verified;
            consent.VerifiedAt = now;
            consent.VerifiedBy = currentUser.UserId;
        }
        else
        {
            var code = SecretTokens.NewNumericCode(6);
            consent.Status = GuardianConsentStatus.Pending;
            consent.CodeHash = GuardianConsent.HashOf(consent.Id, code);
            consent.CodeExpiresAt = now + GuardianConsent.CodeLifetime;
            notifications.Queue(
                CodeTemplate,
                new NotificationRecipient(phone, null, patientId, request.GuardianName.Trim(), PatientLanguages.OrDefault(PatientLanguages.FromPreference(patient.PreferredLanguage))),
                new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["minutes"] = ((int)GuardianConsent.CodeLifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture),
                },
                [NotificationChannel.Sms],
                GuardianConsent.CodeLifetime,
                currentUser.FacilityId ?? patient.RegisteredFacilityId);
        }

        db.Add(consent);
        audit.Add(new AuditEntry(AuditCategories.Data, request.Method == GuardianVerificationMethod.Document ? "privacy.guardian_verified" : "privacy.guardian_code_sent",
            request.Method == GuardianVerificationMethod.Document
                ? $"Guardian's consent for a child verified by {consent.DocumentType} ending {consent.DocumentLast4} ({consent.Relationship})"
                : $"Code sent by SMS to {Phone.Mask(phone)} to verify the guardian's consent for a child ({consent.Relationship})")
        {
            PatientId = patientId,
            EntityType = nameof(GuardianConsent),
            EntityId = consent.Id.ToString(),
            Details = new { consent.Relationship, phone = Phone.Mask(phone), consent.Method, consent.DocumentType },
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Dto(consent, now);
    }

    /// <summary>The guardian read out the code sent to their number: the consent is verified, and replaces an earlier one.</summary>
    public async Task<Result<GuardianConsentDto>> VerifyAsync(Guid patientId, Guid consentId, VerifyGuardianCodeRequest request, CancellationToken ct)
    {
        var consent = await db.Set<GuardianConsent>().FirstOrDefaultAsync(c => c.Id == consentId && c.PatientId == patientId, ct);
        if (consent is null)
        {
            return Error.NotFound(nameof(GuardianConsent), consentId);
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var confirmed = consent.Confirm(GuardianConsent.HashOf(consent.Id, request.Code), currentUser.UserId, now);
        if (confirmed.IsFailure)
        {
            // The try counts even when it is wrong.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return confirmed.Error!;
        }

        await ReplaceVerifiedAsync(patientId, now, ct, except: consent.Id);
        audit.Add(new AuditEntry(AuditCategories.Data, "privacy.guardian_verified",
            $"Guardian's consent for a child verified by a code sent to {Phone.Mask(consent.GuardianPhone)} ({consent.Relationship})")
        {
            PatientId = patientId,
            EntityType = nameof(GuardianConsent),
            EntityId = consent.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Dto(consent, now);
    }

    /// <summary>
    /// The guardian withdraws their consent (s.6(4)). What was recorded on it stays as it was; nothing new outside health
    /// care is recorded for the child until a guardian consents again.
    /// </summary>
    public async Task<Result> RevokeAsync(Guid patientId, Guid consentId, RevokeGuardianConsentRequest request, CancellationToken ct)
    {
        var consent = await db.Set<GuardianConsent>().FirstOrDefaultAsync(c => c.Id == consentId && c.PatientId == patientId, ct);
        if (consent is null)
        {
            return Error.NotFound(nameof(GuardianConsent), consentId);
        }

        if (consent.Status is GuardianConsentStatus.Revoked or GuardianConsentStatus.Expired)
        {
            return Error.Rule("privacy.guardian_consent_ended", "This consent has already ended.");
        }

        consent.Revoke(request.Reason.Trim(), currentUser.UserId, time.GetUtcNow());
        audit.Add(new AuditEntry(AuditCategories.Data, "privacy.guardian_revoked", $"Guardian's consent for a child withdrawn: {request.Reason.Trim()}")
        {
            PatientId = patientId,
            EntityType = nameof(GuardianConsent),
            EntityId = consent.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task ReplaceVerifiedAsync(Guid patientId, DateTimeOffset now, CancellationToken ct, Guid? except = null)
    {
        foreach (var earlier in await db.Set<GuardianConsent>()
                     .Where(c => c.PatientId == patientId && c.Status == GuardianConsentStatus.Verified && c.Id != except).ToListAsync(ct))
        {
            earlier.Revoke("Replaced by a newer consent.", currentUser.UserId, now);
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(time.GetUtcNow().ToOffset(TimeSpan.FromMinutes(330)).DateTime);

    private static GuardianConsentDto Dto(GuardianConsent c, DateTimeOffset now) => new(
        c.Id, c.GuardianName, c.Relationship, Phone.Mask(c.GuardianPhone) ?? string.Empty, c.GuardianIsAdult, c.Method, c.DocumentType, c.DocumentLast4,
        c.Status == GuardianConsentStatus.Pending && !c.IsOpen(now) ? GuardianConsentStatus.Expired : c.Status,
        c.CreatedAt, c.CodeExpiresAt, Math.Max(0, GuardianConsent.MaxAttempts - c.Attempts), c.VerifiedAt, c.RevokedAt, c.RevokedReason);
}
