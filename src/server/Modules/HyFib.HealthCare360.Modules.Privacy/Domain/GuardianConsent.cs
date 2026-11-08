namespace HyFib.HealthCare360.Modules.Privacy.Domain;

public enum GuardianConsentStatus
{
    /// <summary>The code went to the guardian's number; not yet entered.</summary>
    Pending = 0,

    /// <summary>Verified: by the code, or by the guardian's identity document seen at the desk.</summary>
    Verified,

    /// <summary>Withdrawn by the guardian, or replaced by a newer consent.</summary>
    Revoked,

    /// <summary>The code was not entered in time or was entered wrong too many times.</summary>
    Expired,
}

public enum GuardianVerificationMethod
{
    /// <summary>A 6-digit code sent by SMS to the guardian's own mobile number, read out by them at the desk.</summary>
    Otp = 0,

    /// <summary>The guardian's identity document, seen by the member of staff who records it.</summary>
    Document,
}

public enum GuardianRelationship
{
    Parent = 0,

    /// <summary>A guardian appointed by a court or under the law (Guardians and Wards Act 1890, for example).</summary>
    LawfulGuardian,
}

/// <summary>
/// The verifiable consent of a parent or lawful guardian for a child's personal data (DPDP Act 2023 s.9(1); DPDP Rules
/// 2025, rule 10). A child's health care needs none (Fourth Schedule, Part A, item 1); this is what is needed before
/// anything outside it that needs consent: sharing the record, research, photography, a consent manager's artefact.
/// <list type="bullet">
/// <item>Who: the guardian's name, relationship and mobile number, and that they are an adult, as rule 10 asks the
/// hospital to check.</item>
/// <item>How it was verified: a code sent to the guardian's own number (only its salted SHA-256 is kept), or the type of
/// the guardian's identity document and its last four characters (never the whole number), and who saw it.</item>
/// <item>One verified consent per child at a time: a newer one replaces it; withdrawing it is as easy as giving it (s.6(4)).</item>
/// </list>
/// </summary>
public sealed class GuardianConsent : AuditableEntity
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public const int MaxAttempts = 5;
    public const int MaxCodesPerNumber = 3;
    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(15);

    public const string WrongCode = "privacy.guardian_code_wrong";
    public const string ClosedCode = "privacy.guardian_code_expired";

    public Guid PatientId { get; set; }
    public string GuardianName { get; set; } = string.Empty;
    public GuardianRelationship Relationship { get; set; }

    /// <summary>The guardian's mobile number (E.164).</summary>
    public string GuardianPhone { get; set; } = string.Empty;

    /// <summary>The member of staff confirmed the guardian is an adult (rule 10(1)).</summary>
    public bool GuardianIsAdult { get; set; }

    public GuardianVerificationMethod Method { get; set; }

    /// <summary>The identity document seen, for example "Aadhaar", "Passport", "Court order".</summary>
    public string? DocumentType { get; set; }

    /// <summary>The document number's last four characters only.</summary>
    public string? DocumentLast4 { get; set; }

    [SensitiveData]
    public string? CodeHash { get; set; }

    public DateTimeOffset? CodeExpiresAt { get; set; }
    public int Attempts { get; set; }

    public GuardianConsentStatus Status { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public string? RevokedReason { get; set; }

    public static string HashOf(Guid consentId, string code) => SecretTokens.Hash($"{consentId:N}:{code.Trim()}");

    public bool IsOpen(DateTimeOffset now) => Status == GuardianConsentStatus.Pending && CodeExpiresAt is { } expires && now < expires && Attempts < MaxAttempts;

    /// <summary>Checks the code the guardian read out. Each try counts; the last wrong try ends it.</summary>
    public Result Confirm(string enteredHash, Guid? userId, DateTimeOffset now)
    {
        if (Status == GuardianConsentStatus.Pending && !IsOpen(now))
        {
            Status = GuardianConsentStatus.Expired;
        }

        if (Status != GuardianConsentStatus.Pending)
        {
            return Error.Rule(ClosedCode, "This code can no longer be used: it expired, was entered wrong too many times, or the consent was already verified. Send a new code.");
        }

        Attempts++;
        if (CodeHash is null || !SecretTokens.FixedTimeEquals(CodeHash, enteredHash))
        {
            if (Attempts >= MaxAttempts)
            {
                Status = GuardianConsentStatus.Expired;
            }

            return Error.Rule(WrongCode, Attempts >= MaxAttempts
                ? "The code was entered wrong too many times. Send a new code."
                : $"That is not the code sent to the guardian. {MaxAttempts - Attempts} tries left.");
        }

        Status = GuardianConsentStatus.Verified;
        VerifiedAt = now;
        VerifiedBy = userId;
        CodeHash = null;
        return Result.Success();
    }

    public void Revoke(string reason, Guid? userId, DateTimeOffset now)
    {
        Status = GuardianConsentStatus.Revoked;
        RevokedAt = now;
        RevokedBy = userId;
        RevokedReason = reason;
        CodeHash = null;
    }
}
