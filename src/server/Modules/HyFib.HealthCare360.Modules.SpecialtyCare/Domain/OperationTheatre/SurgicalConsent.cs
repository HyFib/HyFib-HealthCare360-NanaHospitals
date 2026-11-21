namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>
/// The consents an operation needs (NABH COP: informed consent before surgery and before anaesthesia), as the front office
/// records them under the patient's consents.
/// </summary>
public enum SurgicalConsentKind
{
    /// <summary>To the operation itself: the front office's "high-risk procedure" consent.</summary>
    Procedure = 0,

    /// <summary>To the anaesthesia: the front office's "anaesthesia" consent. Not asked for local or topical anaesthesia.</summary>
    Anaesthesia,
}

/// <summary>Where one consent the operation needs stands.</summary>
public enum SurgicalConsentState
{
    /// <summary>Given since the operation was booked, after the patient's latest refusal or withdrawal, and not withdrawn.</summary>
    Recorded = 0,

    /// <summary>Nothing is recorded for it.</summary>
    Missing,

    /// <summary>Given only before this operation was booked: a consent is for one operation, so it is taken again for this one.</summary>
    BeforeBooking,

    /// <summary>
    /// The patient's latest word since the operation was booked is a withdrawal: a consent was withdrawn since then (whenever
    /// it was given: the front office cannot tell which operation an older consent was for), and no consent was given after
    /// it (an older consent still on record does not count).
    /// </summary>
    Withdrawn,

    /// <summary>The patient's latest word since the operation was booked is a refusal.</summary>
    Refused,

    /// <summary>Not asked for: local or topical anaesthesia is given by the surgeon.</summary>
    NotNeeded,

    /// <summary>
    /// Given since the operation was booked, but another operation's incision relied on it: a consent is for one operation,
    /// so it is taken again for this one.
    /// </summary>
    OtherOperation,
}

/// <summary>A consent of the patient's as the front office recorded it: given or refused, and when it was withdrawn.</summary>
/// <param name="CapturedByName">Who recorded it, as their account names them; null when it is not known.</param>
/// <param name="SignedFormAttached">The signed consent form is attached to the record.</param>
/// <param name="ReliedOnBy">
/// Another operation whose incision relied on this consent, which does not count for this one: its booking number, or
/// <see cref="SurgicalConsent.AnotherBranch"/> when that operation is another branch's (whose bookings this one does not show).
/// </param>
public sealed record ConsentOnRecord(
    Guid Id,
    SurgicalConsentKind Kind,
    bool Granted,
    DateTimeOffset CapturedAt,
    DateTimeOffset? RevokedAt = null,
    string? CapturedByName = null,
    bool SignedFormAttached = false,
    string? ReliedOnBy = null);

/// <summary>
/// The patient's latest refusal of a consent, recorded before the operation was booked (for instance in the emergency
/// department, before the patient lost consciousness): shown to the surgeon, the second doctor and the audit, but it counts
/// for nothing in <see cref="SurgicalConsentStatus.SaidNo"/> or any gate. Whether it should block the emergency waiver is an
/// open question for the hospital.
/// </summary>
/// <param name="ConsentId">The refusal on the patient's consents.</param>
/// <param name="RecordedAt">When it was recorded.</param>
/// <param name="RecordedByName">Who recorded it, as their account names them; null when it is not known.</param>
public sealed record EarlierRefusal(Guid ConsentId, DateTimeOffset RecordedAt, string? RecordedByName);

/// <summary>
/// One consent the operation needs, where it stands, and the record that says so (none when nothing is recorded).
/// <paramref name="EarlierRefusal"/> is the patient's latest refusal of it recorded before the booking, carried only while
/// no consent or refusal of that kind has been recorded since; it does not change <paramref name="State"/>.
/// </summary>
public sealed record SurgicalConsentCheck(SurgicalConsentKind Kind, SurgicalConsentState State, ConsentOnRecord? Consent, EarlierRefusal? EarlierRefusal = null);

/// <summary>
/// An emergency operation going ahead without the patient's consent (the emergency doctrine): the operating surgeon, from
/// their own sign-in, with the clinical reason it cannot wait, and a second doctor who agrees, confirming at the surgeon's
/// screen with their own user name and password.
/// </summary>
public sealed record SurgicalConsentWaiver(
    DateTimeOffset At,
    Guid? SurgeonUserId,
    string SurgeonName,
    Guid? ConcurringUserId,
    string ConcurringName,
    string Reason);

/// <summary>The second doctor who agrees that an emergency operation cannot wait for consent, as their sign-in names them.</summary>
/// <param name="PractitionerId">The practitioner record their account is linked to, if any.</param>
/// <param name="IsDoctor">That record is an active doctor's (consultant, resident or visiting).</param>
public sealed record ConcurringDoctor(Guid UserId, string Name, Guid? PractitionerId, bool IsDoctor);

/// <summary>The patient's consent to one operation, as the theatre's gates read it.</summary>
/// <param name="Since">When the operation was booked: a consent counts from then.</param>
public sealed record SurgicalConsentStatus(DateTimeOffset Since, IReadOnlyList<SurgicalConsentCheck> Checks, SurgicalConsentWaiver? Waiver)
{
    /// <summary>Every consent the operation needs is recorded.</summary>
    public bool Recorded => Checks.All(c => c.State is SurgicalConsentState.Recorded or SurgicalConsentState.NotNeeded);

    /// <summary>The patient's latest word since the booking on a consent the operation needs is a refusal.</summary>
    public bool Refused => Checks.Any(c => c.State == SurgicalConsentState.Refused);

    /// <summary>
    /// The patient's latest word since the booking on a consent the operation needs is "no": a refusal, or a withdrawal. The
    /// emergency doctrine is for a patient who cannot consent, so no waiver stands against it.
    /// </summary>
    public bool SaidNo => Checks.Any(c => c.State is SurgicalConsentState.Refused or SurgicalConsentState.Withdrawn);

    /// <summary>The consent the patient said no to (<see cref="SaidNo"/>), a refusal before a withdrawal; none when they did not.</summary>
    public SurgicalConsentCheck? No =>
        Checks.FirstOrDefault(c => c.State == SurgicalConsentState.Refused) ?? Checks.FirstOrDefault(c => c.State == SurgicalConsentState.Withdrawn);

    /// <summary>
    /// Consent was waived in an emergency (<see cref="Waiver"/>, given only while the booking is one) and the patient has not
    /// said no since: whichever was recorded first, a refusal or a withdrawal of a consent needed ends the waiver's effect.
    /// </summary>
    public bool WaiverStands => Waiver is not null && !SaidNo;

    /// <summary>The gates open: the consents are recorded, or the emergency waiver stands (<see cref="WaiverStands"/>).</summary>
    public bool Satisfied => Recorded || WaiverStands;

    /// <summary>The gates stand on the emergency waiver: it stands, and a consent the operation needs is not recorded.</summary>
    public bool OnWaiver => WaiverStands && !Recorded;

    /// <summary>The consents needed that are not recorded, with why.</summary>
    public IReadOnlyList<SurgicalConsentCheck> Missing =>
        [.. Checks.Where(c => c.State is not (SurgicalConsentState.Recorded or SurgicalConsentState.NotNeeded))];

    /// <summary>The consent in force of <paramref name="kind"/>, the one the incision relies on.</summary>
    public Guid? ConsentIdOf(SurgicalConsentKind kind) =>
        Checks.FirstOrDefault(c => c.Kind == kind && c.State == SurgicalConsentState.Recorded)?.Consent?.Id;
}

/// <summary>
/// Informed consent before surgery and anaesthesia (NABH COP), checked by the theatre's own gates rather than ticked: the
/// pre-op "consent" item, the WHO Sign-in and Time-out, and the incision need the patient's consent to the procedure and,
/// unless the surgeon gives local or topical anaesthesia, to the anaesthesia. Each counts only when it was given since the
/// operation was booked and is not withdrawn, and the patient's latest word on it since then is that consent: a consent
/// given before the patient's latest refusal, or before the withdrawal of any consent given since the booking, does not
/// count, so a withdrawal is never undone by another consent still on record. A withdrawal since the booking counts as the
/// patient's latest word whenever the consent withdrawn was given: the front office cannot tell which operation an older
/// consent was for. A consent is for one operation: one given for an earlier operation, or relied on by another
/// operation's incision, does not count for this one. In an emergency the operating surgeon may go ahead without it
/// (<see cref="SurgicalConsentWaiver"/>), never against the patient's refusal or withdrawal.
/// </summary>
public static class SurgicalConsent
{
    /// <summary>The shortest clinical reason for operating without consent.</summary>
    public const int MinimumReasonLength = 10;

    public const string RequiredCode = "surgery.consent_required";

    /// <summary>The emergency waiver refused: the patient's latest word on a consent the operation needs is a refusal.</summary>
    public const string RefusedCode = "surgery.consent_refused";

    /// <summary>The emergency waiver refused: the patient's latest word on a consent the operation needs is a withdrawal.</summary>
    public const string WithdrawnCode = "surgery.consent_withdrawn";

    /// <summary>
    /// <see cref="ConsentOnRecord.ReliedOnBy"/> for a consent another branch's operation relied on: that booking's number is
    /// not shown here, as the patient's operations are listed by branch.
    /// </summary>
    public const string AnotherBranch = "another branch";

    /// <summary>The consents an operation under <paramref name="anaesthesia"/> needs.</summary>
    public static IReadOnlyList<SurgicalConsentKind> Needed(AnaesthesiaType anaesthesia) =>
        AnaesthesiaTypes.GivenBySurgeon(anaesthesia) ? [SurgicalConsentKind.Procedure] : [SurgicalConsentKind.Procedure, SurgicalConsentKind.Anaesthesia];

    /// <summary>
    /// Where the consents of an operation booked at <paramref name="bookedAt"/> stand, from the patient's consents on record:
    /// each of <paramref name="needed"/> is checked, the others are not needed.
    /// </summary>
    public static SurgicalConsentStatus Assess(
        DateTimeOffset bookedAt, IReadOnlyCollection<SurgicalConsentKind> needed, IEnumerable<ConsentOnRecord> consents, SurgicalConsentWaiver? waiver)
    {
        ArgumentNullException.ThrowIfNull(needed);
        ArgumentNullException.ThrowIfNull(consents);
        var all = consents.ToList();
        return new SurgicalConsentStatus(bookedAt,
            [
                .. Enum.GetValues<SurgicalConsentKind>().Select(kind => needed.Contains(kind)
                    ? Check(kind, bookedAt, all)
                    : new SurgicalConsentCheck(kind, SurgicalConsentState.NotNeeded, null)),
            ],
            waiver);
    }

    /// <summary>The refusal of a gate while the consents are neither recorded nor waived, saying what is missing and why.</summary>
    public static Result Require(SurgicalConsentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Satisfied)
        {
            return Result.Success();
        }

        var missing = status.Missing;
        var next = status.Refused
            ? "An emergency does not override the patient's refusal: if the patient now agrees, record the new consent under the patient's consents (front office)."
            : status.SaidNo
                ? "An emergency does not override a consent the patient withdrew: if the patient now agrees, record the new consent under the patient's consents (front office)."
                : "Record it under the patient's consents (front office) before going on. In an emergency the operating surgeon may go ahead without it, with a second doctor's agreement.";
        return new Error(RequiredCode, $"The patient's consent is not recorded for this operation: {string.Join("; ", missing.Select(Describe))}. {next}")
        {
            Data = missing,
        };
    }

    /// <summary>"consent to anaesthesia was withdrawn".</summary>
    public static string Describe(SurgicalConsentCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var what = What(check.Kind);
        return check.State switch
        {
            SurgicalConsentState.BeforeBooking => $"{what} was recorded before this operation was booked; take it again for this operation",
            SurgicalConsentState.Withdrawn => $"{what} was withdrawn",
            SurgicalConsentState.Refused => $"the patient refused {what}",
            SurgicalConsentState.OtherOperation =>
                $"{what} was relied on by another operation{(check.Consent?.ReliedOnBy is { } other ? $" ({other})" : string.Empty)}; take it again for this operation",
            _ => $"{what} is not recorded",
        };
    }

    /// <summary>"consent to anaesthesia".</summary>
    public static string What(SurgicalConsentKind kind) =>
        kind == SurgicalConsentKind.Procedure ? "consent to the procedure (high-risk procedure consent)" : "consent to anaesthesia";

    /// <summary>
    /// The consent of <paramref name="kind"/> in force: given since <paramref name="since"/>, not withdrawn, given after the
    /// patient's latest "no" since then (a refusal, or the withdrawal of a consent, whenever it was given), and not relied on
    /// by another operation's incision; otherwise why there is none.
    /// </summary>
    private static SurgicalConsentCheck Check(SurgicalConsentKind kind, DateTimeOffset since, List<ConsentOnRecord> all)
    {
        var check = Resolve(kind, since, all);
        if (all.Any(c => c.Kind == kind && c.CapturedAt >= since))
        {
            return check;
        }

        return all.Where(c => c.Kind == kind && !c.Granted && c.CapturedAt < since).MaxBy(c => c.CapturedAt) is { } earlier
            ? check with { EarlierRefusal = new EarlierRefusal(earlier.Id, earlier.CapturedAt, earlier.CapturedByName) }
            : check;
    }

    private static SurgicalConsentCheck Resolve(SurgicalConsentKind kind, DateTimeOffset since, List<ConsentOnRecord> all)
    {
        var ofKind = all.Where(c => c.Kind == kind).ToList();
        var current = ofKind.Where(c => c.CapturedAt >= since).ToList();
        var refusal = current.Where(c => !c.Granted).MaxBy(c => c.CapturedAt);
        var withdrawn = ofKind.Where(c => c.Granted && c.RevokedAt is { } revoked && revoked >= since).MaxBy(c => c.RevokedAt);
        var saidNoAt = Later(refusal?.CapturedAt, withdrawn?.RevokedAt);
        var standing = current.Where(c => c.Granted && c.RevokedAt is null && (saidNoAt is null || c.CapturedAt > saidNoAt)).ToList();
        if (standing.Where(c => c.ReliedOnBy is null).MaxBy(c => c.CapturedAt) is { } inForce)
        {
            return new SurgicalConsentCheck(kind, SurgicalConsentState.Recorded, inForce);
        }

        if (standing.MaxBy(c => c.CapturedAt) is { } used)
        {
            return new SurgicalConsentCheck(kind, SurgicalConsentState.OtherOperation, used);
        }

        if (refusal is not null && (withdrawn is null || refusal.CapturedAt >= withdrawn.RevokedAt))
        {
            return new SurgicalConsentCheck(kind, SurgicalConsentState.Refused, refusal);
        }

        if (withdrawn is not null)
        {
            return new SurgicalConsentCheck(kind, SurgicalConsentState.Withdrawn, withdrawn);
        }

        return ofKind.Where(c => c.Granted && c.RevokedAt is null).MaxBy(c => c.CapturedAt) is { } earlier
            ? new SurgicalConsentCheck(kind, SurgicalConsentState.BeforeBooking, earlier)
            : new SurgicalConsentCheck(kind, SurgicalConsentState.Missing, null);
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
