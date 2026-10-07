namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>What the linked patient is to the family member who sees their records.</summary>
public enum FamilyRelation
{
    Parent = 0,
    Child,
    Spouse,
    Sibling,
    Grandparent,
    Grandchild,
    Other,
}

public enum FamilyLinkSource
{
    /// <summary>Made by staff at the front desk after checking identities and recording the patient's consent.</summary>
    FrontDesk = 0,

    /// <summary>Made in the portal with a code sent to the patient's own mobile number.</summary>
    PortalCode,
}

/// <summary>
/// Lets the portal account of <see cref="AccountPhone"/> see the records of a patient registered under another mobile
/// number (PP-01). Everyone registered with the account's own number is its family without a link. A link is made at the
/// front desk, when staff have checked who both people are and recorded the patient's consent, or in the portal, when the
/// account holder enters a code sent to the patient's own mobile number. The patient, the account holder or staff can
/// end it at any time, and it stops at once wherever the portal checks whose records an account may see.
/// </summary>
public sealed class PortalFamilyLink : AuditableEntity
{
    /// <summary>The mobile number (E.164) the family member signs in to the portal with.</summary>
    public string AccountPhone { get; set; } = string.Empty;

    public Guid PatientId { get; set; }
    public FamilyRelation Relation { get; set; }
    public FamilyLinkSource Source { get; set; }
    public DateTimeOffset LinkedAt { get; set; }

    /// <summary>The member of staff at the front desk, or the portal user who entered the code.</summary>
    public Guid? LinkedByUserId { get; set; }

    /// <summary>Front desk: who agreed, the patient or, for a child or a patient who cannot, their guardian.</summary>
    public string? ConsentGivenBy { get; set; }

    /// <summary>Portal: the patient's number the code was sent to.</summary>
    public string? CodeSentTo { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevokedReason { get; set; }

    public bool IsActive => RevokedAt is null;

    public Result Revoke(string reason, Guid? userId, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return Error.Rule("portal.family_link_ended", "This family member can no longer see the records.");
        }

        RevokedAt = now;
        RevokedByUserId = userId;
        RevokedReason = reason.Trim();
        return Result.Success();
    }
}

/// <summary>
/// A code sent to a patient's own mobile number when a portal account asks to see their records (PP-01). The account
/// holder enters it to make the link: the patient agrees by passing it on. A request that matches no patient is kept too,
/// with no patient and no message sent, so the portal never tells whether a UHID and a mobile number belong together.
/// </summary>
public sealed class PortalFamilyCode : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxAttempts = 5;

    /// <summary>The account asking, by the number it signs in with (E.164).</summary>
    public string AccountPhone { get; set; } = string.Empty;

    /// <summary>The number entered for the patient, and the one the code went to when a patient matched.</summary>
    public string TargetPhone { get; set; } = string.Empty;

    public Guid? PatientId { get; set; }
    public FamilyRelation Relation { get; set; }
    public string? CodeHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsOpen(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now && Attempts < MaxAttempts;

    /// <summary>
    /// Checks an entered code against <paramref name="enteredHash"/>, the hash of what was typed. Each try counts, and a
    /// request that matched no patient never succeeds.
    /// </summary>
    public Result<Guid> Verify(string enteredHash, DateTimeOffset now)
    {
        if (!IsOpen(now))
        {
            return Error.Rule("portal.family_code_expired", "This code can no longer be used. Ask for a new one.");
        }

        Attempts++;
        if (PatientId is not { } patientId || CodeHash is null || !SecretTokens.FixedTimeEquals(CodeHash, enteredHash))
        {
            return Error.Field("code", "The code is incorrect.");
        }

        ConsumedAt = now;
        return patientId;
    }
}
