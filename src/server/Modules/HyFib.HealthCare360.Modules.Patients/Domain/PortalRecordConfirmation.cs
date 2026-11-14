namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>What a portal account gave to show that a record on its number is the patient's (<see cref="PortalRecordConfirmation"/>).</summary>
public enum RecordConfirmationMethod
{
    /// <summary>The patient's date of birth, as the record has it.</summary>
    DateOfBirth = 0,

    /// <summary>The UHID printed on the patient's hospital card, bills and receipts.</summary>
    Uhid,
}

/// <summary>
/// One try by a patient portal account to show that a record registered with its mobile number is the patient's, so that
/// the portal shows it (ASVS V6.6.1). A record whose number nobody has verified (<see cref="Patient.IsPhoneVerified"/>)
/// is kept from the account until it gives the patient's date of birth or UHID: whoever holds a number the desk typed one
/// digit wrong, or one the telco gave to someone new, knows neither. A right answer verifies the record's number.
/// <para>
/// Wrong answers are limited per number, not per record or per session, so they cannot be spread out: at most
/// <see cref="MaxFailures"/> in <see cref="Window"/>, counted under a lock on the number. Nothing typed is kept: only how,
/// when, from which number, and how many records the try confirmed. Each try is in the audit trail.
/// </para>
/// </summary>
public sealed class PortalRecordConfirmation : AuditableEntity
{
    /// <summary>Wrong answers a number may give in <see cref="Window"/>.</summary>
    public const int MaxFailures = 5;

    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    public const string NoMatchCode = "portal.confirm_no_match";
    public const string ThrottledCode = "portal.confirm_throttled";
    public const string NothingToConfirmCode = "portal.confirm_nothing";

    /// <summary>The portal account's number (E.164).</summary>
    public string AccountPhone { get; set; } = string.Empty;

    public RecordConfirmationMethod Method { get; set; }

    /// <summary>How many records the try confirmed: none for a wrong answer.</summary>
    public int Confirmed { get; set; }

    /// <summary>
    /// Whether a record is the one the account gave the details of: every detail given matches, and at least one was given.
    /// A UHID matches as printed, in any case. A date of birth matches only a record that has it as the patient gave it: one
    /// worked out from an age at registration is the desk's estimate, not something the patient knows (they give the UHID
    /// instead).
    /// </summary>
    public static bool Matches(string recordUhid, DateOnly? recordDateOfBirth, bool dateOfBirthEstimated, DateOnly? dateOfBirth, string? uhid)
    {
        ArgumentNullException.ThrowIfNull(recordUhid);
        var uhidGiven = !string.IsNullOrWhiteSpace(uhid);
        if (!uhidGiven && dateOfBirth is null)
        {
            return false;
        }

        if (uhidGiven && !string.Equals(uhid!.Trim(), recordUhid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return dateOfBirth is not { } given || (recordDateOfBirth == given && !dateOfBirthEstimated);
    }

    /// <summary>
    /// How long until a number that gave wrong answers at <paramref name="failuresNewestFirst"/> (the latest
    /// <see cref="MaxFailures"/> in <see cref="Window"/>, newest first) may try again, or null while it may try now.
    /// </summary>
    public static TimeSpan? WaitBeforeNext(IReadOnlyList<DateTimeOffset> failuresNewestFirst, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(failuresNewestFirst);
        if (failuresNewestFirst.Count < MaxFailures)
        {
            return null;
        }

        var wait = failuresNewestFirst[MaxFailures - 1] + Window - now;
        return wait > TimeSpan.Zero ? wait : null;
    }

    /// <summary>Wrong answers the number may still give, after <paramref name="failuresInWindow"/> in <see cref="Window"/>.</summary>
    public static int AttemptsLeft(int failuresInWindow) => Math.Max(0, MaxFailures - failuresInWindow);

    /// <summary>The refusal once the number has used its tries, saying when the next is allowed.</summary>
    public static Error Throttled(TimeSpan wait)
    {
        var refusal = Error.Rule(ThrottledCode,
            "Too many wrong tries for this mobile number. Try again later, or ask at the hospital's front desk to confirm your mobile number.");
        return refusal with { Data = new { retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)), attemptsLeft = 0 } };
    }

    /// <summary>The answer to details that match no record waiting on the number, with the tries left.</summary>
    public static Error NoMatch(int attemptsLeft)
    {
        var refusal = Error.Rule(NoMatchCode, "These details do not match a record registered with this mobile number. Check them and try again.");
        return refusal with { Data = new { attemptsLeft } };
    }
}
