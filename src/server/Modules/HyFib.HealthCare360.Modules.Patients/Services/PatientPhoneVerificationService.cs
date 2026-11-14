using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Verifying, at the desk, the mobile number a patient is registered with (ASVS V6.6.1): "Verify mobile" at registration
/// and on the patient's record. A 6-digit code goes to the number (by SMS, and on WhatsApp where its template is in use),
/// the patient reads it out, and staff enter it; the record keeps its number, now verified (<see cref="Patient.PhoneVerifiedAt"/>).
/// It is optional, so a quick registration stays quick. Until a number is verified the patient portal keeps the record from
/// whoever signs in with it unless they give the patient's date of birth or UHID (<see cref="PortalPatientScope"/>), and
/// messages to it go without their document (<see cref="VerifiedNumbers"/>).
/// <para>
/// The code is the one a number change uses (<see cref="PatientPhoneChange"/>, <see cref="PhoneCodePurpose.Verify"/>): six
/// digits, ten minutes, five tries judged one at a time under the code's row lock, used once, kept only hashed, and three
/// codes to a number in 15 minutes, whether they change a number or verify one. It verifies the number only while the
/// record still has the number the code went to.
/// </para>
/// </summary>
public sealed class PatientPhoneVerificationService(
    HospitalDbContext db,
    PatientAccessGuard guard,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>The message with the code, to the number being verified.</summary>
    public const string Template = "patients.verify_code";

    /// <summary>SMS always; WhatsApp too once the hospital switches its template on (it is seeded not in use until Meta approves it).</summary>
    public static readonly IReadOnlyList<NotificationChannel> Channels = [NotificationChannel.Sms, NotificationChannel.WhatsApp];

    public const string CodeSentAction = "patient.phone_verify_code_sent";
    public const string VerifiedAction = "patient.phone_verified";

    public const string AlreadyVerifiedCode = "patient.phone_already_verified";
    public const string NoMobileCode = "patient.no_mobile";

    /// <summary>The verification waiting for its code, or null when the patient has none that can still be confirmed.</summary>
    public async Task<Result<PhoneVerificationDto?>> PendingAsync(Guid patientId, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var now = time.GetUtcNow();
        var phone = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => p.Phone).FirstOrDefaultAsync(ct);
        var pending = await db.Set<PatientPhoneChange>().AsNoTracking()
            .Where(c => c.PatientId == patientId && c.Purpose == PhoneCodePurpose.Verify && c.Status == PatientPhoneChangeStatus.Pending)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return pending is not null && pending.IsOpen(now) && pending.Phone == phone ? View(pending) : null;
    }

    /// <summary>
    /// Sends a code to the number on the record, replacing the patient's verification still waiting for one. Refused for a
    /// record with no Indian mobile number, for a number verified already, for a merged record, and once three codes went to
    /// the number in 15 minutes (with how long to wait).
    /// </summary>
    public async Task<Result<PhoneVerificationDto>> RequestAsync(Guid patientId, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Merged;
        }

        if (patient.Phone is not { } phone || !Phone.IsIndianMobile(phone))
        {
            return Error.Rule(NoMobileCode,
                "The patient has no Indian mobile number on the record to send a code to. Add one with Add number or Change number first.");
        }

        if (patient.IsPhoneVerified)
        {
            return Error.Rule(AlreadyVerifiedCode, "The patient's mobile number is verified already.");
        }

        // As a number change takes turns (PatientPhoneChangeService): the patient's lock, then the number's, so a change and
        // a verification asked for at once cannot get past three codes to the number in 15 minutes between them.
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"patients.phone_change:" + patientId.ToString("N")}, 0))", ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"patients.phone_code:" + phone}, 0))", ct);
        var windowStart = now - PatientPhoneChange.ThrottleWindow;
        var sent = await db.Set<PatientPhoneChange>()
            .Where(c => c.Phone == phone && c.CreatedAt > windowStart)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.CreatedAt)
            .Take(PatientPhoneChange.MaxCodesPerNumber)
            .ToListAsync(ct);
        if (PatientPhoneChange.WaitBeforeNext(sent, now) is { } wait)
        {
            return PatientPhoneChange.Throttled(wait);
        }

        foreach (var earlier in await db.Set<PatientPhoneChange>()
                     .Where(c => c.PatientId == patientId && c.Purpose == PhoneCodePurpose.Verify && c.Status == PatientPhoneChangeStatus.Pending)
                     .ToListAsync(ct))
        {
            earlier.Supersede();
        }

        var facilityId = currentUser.FacilityId ?? patient.RegisteredFacilityId;
        var code = SecretTokens.NewNumericCode(6);
        var verification = new PatientPhoneChange
        {
            FacilityId = facilityId,
            PatientId = patientId,
            Purpose = PhoneCodePurpose.Verify,
            Phone = phone,
            PreviousPhone = phone,
            ExpiresAt = now + PatientPhoneChange.Lifetime,
            Status = PatientPhoneChangeStatus.Pending,
        };
        verification.CodeHash = PatientPhoneChange.HashOf(verification.Id, code);
        db.Add(verification);

        // Without the patient's name: the number is not known to be theirs until the code comes back.
        notifications.Queue(
            Template,
            new NotificationRecipient(phone, null, patientId, null, PatientLanguages.OrDefault(PatientLanguages.FromPreference(patient.PreferredLanguage))),
            new Dictionary<string, string> { ["code"] = code, ["minutes"] = ((int)PatientPhoneChange.Lifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture) },
            Channels,
            PatientPhoneChange.Lifetime,
            facilityId);

        audit.Add(new AuditEntry(AuditCategories.Data, CodeSentAction, $"Code sent to {Phone.Mask(phone)} to verify the patient's mobile number")
        {
            PatientId = patientId,
            EntityType = nameof(Patient),
            EntityId = patientId.ToString(),
            Details = new { verificationId = verification.Id, phone = Phone.Mask(phone), expiresAt = verification.ExpiresAt },
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return View(verification);
    }

    /// <summary>
    /// Verifies the number when the code entered is the one sent. Wrong codes count (five tries), and the number is verified
    /// only while the record still has the number the code went to (409 otherwise: send a new code).
    /// </summary>
    public async Task<Result<PhoneVerified>> ConfirmAsync(Guid patientId, Guid verificationId, ConfirmPhoneVerificationRequest request, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        // As a change's code is judged (PatientPhoneChangeService.ConfirmAsync): one try at a time, each saved before the
        // next is read, with the record held so an edit saved meanwhile waits and then finds the record changed.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM patients.patients WHERE id = {patientId} FOR NO KEY UPDATE", ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM patients.patient_phone_changes WHERE id = {verificationId} FOR NO KEY UPDATE", ct);
        var verification = await db.Set<PatientPhoneChange>()
            .FirstOrDefaultAsync(c => c.Id == verificationId && c.PatientId == patientId && c.Purpose == PhoneCodePurpose.Verify, ct);
        var patient = verification is null ? null : await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (verification is null || patient is null)
        {
            return NotFound;
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Merged;
        }

        var now = time.GetUtcNow();
        if (verification.WhyClosed(now) is { } closed)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return closed;
        }

        if (patient.Phone != verification.Phone)
        {
            verification.Supersede();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Error.Conflict("patient.phone_changed_meanwhile",
                "The patient's mobile number was changed after this code was sent. Send a new code to the number they have now.");
        }

        var checkedCode = verification.Confirm(PatientPhoneChange.HashOf(verification.Id, request.Code), currentUser.UserId, now);
        if (checkedCode.IsFailure)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return checkedCode.Error!;
        }

        patient.MarkPhoneVerified(now);
        audit.Add(new AuditEntry(AuditCategories.Data, VerifiedAction,
            $"Mobile number {Phone.Mask(verification.Phone)} verified at the desk with a code the patient read out; confirmed by {currentUser.DisplayName ?? currentUser.UserName ?? "unknown"}")
        {
            PatientId = patient.Id,
            EntityType = nameof(Patient),
            EntityId = patient.Id.ToString(),
            Details = new { verificationId = verification.Id, phone = Phone.Mask(verification.Phone), how = "desk", confirmedById = currentUser.UserId },
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new PhoneVerified(Phone.Mask(verification.Phone), now);
    }

    private static Error Merged => Error.Rule("patient.merged", "This record was merged into another patient and can no longer be edited.");

    private static Error NotFound => new("patient.phone_verification_not_found", "This verification was not found. Send a new code.", ErrorType.NotFound);

    private static PhoneVerificationDto View(PatientPhoneChange verification) =>
        new(verification.Id, Phone.Mask(verification.Phone), verification.ExpiresAt, verification.AttemptsLeft);
}
