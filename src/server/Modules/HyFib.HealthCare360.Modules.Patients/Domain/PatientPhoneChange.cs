namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>Where a change of a patient's registered mobile number stands.</summary>
public enum PatientPhoneChangeStatus
{
    /// <summary>The code went to the new number; the desk has not entered it yet.</summary>
    Pending = 0,

    /// <summary>The desk entered the code: the record has the new number.</summary>
    Confirmed,

    /// <summary>A newer code was sent for the patient, or the record's number changed after this one was sent.</summary>
    Superseded,

    /// <summary>Not entered in time, or the code was given wrong <see cref="PatientPhoneChange.MaxAttempts"/> times.</summary>
    Expired,
}

/// <summary>What a code sent to a patient's mobile number is for (<see cref="PatientPhoneChange.Purpose"/>).</summary>
public enum PhoneCodePurpose
{
    /// <summary>To change the record's number to the one the code went to.</summary>
    Change = 0,

    /// <summary>
    /// To verify the number the record has already (ASVS V6.6.1): the code goes to that number, and the record keeps it,
    /// marked verified (<see cref="Patient.PhoneVerifiedAt"/>), when staff enter the code.
    /// </summary>
    Verify,
}

/// <summary>
/// A change of the patient's registered mobile number, which the patient portal signs in with (PP-01): staff cannot make
/// it alone. A 6-digit code goes by SMS to the new number and the change is made only when staff enter it, as the patient
/// reads it out at the desk, within <see cref="Lifetime"/> and <see cref="MaxAttempts"/> tries, and only while the record
/// still has the number it had when the code was sent (<see cref="PreviousPhone"/>). Nothing goes to the old number. Only
/// the SHA-256 of the code, salted with the change's id, is kept. Who asked is <see cref="AuditableEntity.CreatedBy"/>.
/// <para>
/// The same code, with the same limits, verifies the number the record has already (<see cref="PhoneCodePurpose.Verify"/>):
/// <see cref="Phone"/> and <see cref="PreviousPhone"/> are then that number. Both kinds count towards a number's
/// <see cref="MaxCodesPerNumber"/>.
/// </para>
/// </summary>
public sealed class PatientPhoneChange : AuditableEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public const int MaxAttempts = 5;

    /// <summary>The most codes one number is sent in <see cref="ThrottleWindow"/>, whichever patients they were for.</summary>
    public const int MaxCodesPerNumber = 3;

    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(15);

    public const string ExpiredCode = "patient.phone_code_expired";
    public const string WrongCode = "patient.phone_code_wrong";
    public const string ThrottledCode = "patient.phone_code_throttled";

    /// <summary>The message for a number that is not an Indian mobile number.</summary>
    public const string IndianMobileMessage =
        "Enter a 10-digit Indian mobile number: the patient gets messages and signs in to the portal with it. Put any other number in Alternate phone.";

    /// <summary>The facility whose desk asked, whose name the message carries.</summary>
    public Guid FacilityId { get; set; }

    public Guid PatientId { get; set; }

    /// <summary>A change of the number, or a verification of the one the record has.</summary>
    public PhoneCodePurpose Purpose { get; set; }

    /// <summary>The number asked for (E.164): the one the code went to.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>The record's number when the code was sent (null for none): the change is made only while it still has it.</summary>
    public string? PreviousPhone { get; set; }

    /// <summary>SHA-256 of the change's id and the code (<see cref="HashOf"/>); never the code.</summary>
    [SensitiveData]
    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public PatientPhoneChangeStatus Status { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>The member of staff who entered the code.</summary>
    public Guid? ConfirmedBy { get; set; }

    public int AttemptsLeft => Math.Max(0, MaxAttempts - Attempts);

    /// <summary>Waiting for its code, which can still be entered at <paramref name="now"/>.</summary>
    public bool IsOpen(DateTimeOffset now) => Status == PatientPhoneChangeStatus.Pending && now < ExpiresAt && Attempts < MaxAttempts;

    /// <summary>What is stored for <paramref name="code"/>: salted with the change's id, so one hash says nothing of another change's code.</summary>
    public static string HashOf(Guid changeId, string code) => SecretTokens.Hash($"{changeId:N}:{code.Trim()}");

    /// <summary>
    /// Why the code can no longer be entered, or null while it can. A pending change found past its time or its tries is
    /// marked <see cref="PatientPhoneChangeStatus.Expired"/> on the way.
    /// </summary>
    public Error? WhyClosed(DateTimeOffset now)
    {
        if (Status == PatientPhoneChangeStatus.Pending && !IsOpen(now))
        {
            Status = PatientPhoneChangeStatus.Expired;
        }

        return Status switch
        {
            PatientPhoneChangeStatus.Pending => null,
            PatientPhoneChangeStatus.Confirmed => Error.Rule(ExpiredCode,
                Purpose == PhoneCodePurpose.Verify ? "This number is verified already." : "This number change is already done."),
            PatientPhoneChangeStatus.Superseded => Error.Rule(ExpiredCode, "A newer code was sent for this patient, or the number changed since. Enter the latest code, or send a new one."),
            _ => Error.Rule(ExpiredCode, "This code can no longer be used: it expired, or was entered wrong too many times. Send a new code."),
        };
    }

    /// <summary>
    /// Checks the code entered at the desk against <paramref name="enteredHash"/> (<see cref="HashOf"/> of what was typed).
    /// Each try counts; the last wrong try ends the change, and the right code confirms it once.
    /// </summary>
    public Result Confirm(string enteredHash, Guid? userId, DateTimeOffset now)
    {
        if (WhyClosed(now) is { } closed)
        {
            return closed;
        }

        Attempts++;
        if (!SecretTokens.FixedTimeEquals(CodeHash, enteredHash))
        {
            if (Attempts >= MaxAttempts)
            {
                Status = PatientPhoneChangeStatus.Expired;
            }

            return Wrong(AttemptsLeft);
        }

        Status = PatientPhoneChangeStatus.Confirmed;
        ConfirmedAt = now;
        ConfirmedBy = userId;
        return Result.Success();
    }

    /// <summary>A newer code for the patient, or a change of the record's number since, ends a pending change.</summary>
    public void Supersede()
    {
        if (Status == PatientPhoneChangeStatus.Pending)
        {
            Status = PatientPhoneChangeStatus.Superseded;
        }
    }

    /// <summary>
    /// How long until another code may go to a number that was sent codes at <paramref name="sentNewestFirst"/> (the
    /// latest <see cref="MaxCodesPerNumber"/> in <see cref="ThrottleWindow"/>, newest first), or null when one may go now.
    /// </summary>
    public static TimeSpan? WaitBeforeNext(IReadOnlyList<DateTimeOffset> sentNewestFirst, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sentNewestFirst);
        if (sentNewestFirst.Count < MaxCodesPerNumber)
        {
            return null;
        }

        var wait = sentNewestFirst[MaxCodesPerNumber - 1] + ThrottleWindow - now;
        return wait > TimeSpan.Zero ? wait : null;
    }

    /// <summary>The refusal of a code asked for too soon, saying when the next is allowed (as the portal's sign-in does).</summary>
    public static Error Throttled(TimeSpan wait)
    {
        var refusal = Error.Rule(ThrottledCode,
            $"{MaxCodesPerNumber} codes have been sent to this number in the last {(int)ThrottleWindow.TotalMinutes} minutes. Wait a few minutes before sending another.");
        return refusal with { Data = new { retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) } };
    }

    private static Error Wrong(int left)
    {
        var message = left > 0
            ? $"The code is incorrect. {left} {(left == 1 ? "try" : "tries")} left."
            : "The code is incorrect, and that was the last try. Send a new code.";
        return Error.Validation(WrongCode, message, new Dictionary<string, string[]> { ["code"] = [message] }) with { Data = new { attemptsLeft = left } };
    }
}
