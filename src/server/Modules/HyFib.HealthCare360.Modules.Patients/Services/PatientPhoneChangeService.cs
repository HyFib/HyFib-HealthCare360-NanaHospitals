using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Changing a patient's registered mobile number, which the patient portal signs in with (PP-01). Staff cannot change it
/// alone (the edit refuses a new number): a desk that could would hand the patient's records to whoever holds the number
/// it typed, from a social-engineering call or a recycled SIM. A 6-digit code goes by SMS to the new number; the change is
/// made when staff enter it as the patient reads it out, and nothing goes to the old number. Codes follow the portal's
/// sign-in: three per number in 15 minutes, counted under a lock on the number; 10 minutes and five tries each, judged one
/// at a time under the change's row lock; used once; stored only hashed.
/// <para>
/// The change, in one transaction: the record takes the new number, links to the portal sent to the old one stop, the
/// family members the old number's portal account saw follow the patient to the new number when nobody else keeps the old
/// one (<see cref="PortalFamilyService.FollowNumberChangeAsync"/>), and when the old number then reaches no patient's
/// records at all, its portal account is signed out everywhere and forgets the patient's name, so that whoever gets the
/// number next never sees it.
/// </para>
/// </summary>
public sealed class PatientPhoneChangeService(
    HospitalDbContext db,
    PatientAccessGuard guard,
    PortalFamilyService family,
    INotificationService notifications,
    ISessionValidator sessions,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>The SMS with the code, to the new number.</summary>
    public const string Template = "patients.phone_code";

    public const string CodeSentAction = "patient.phone_code_sent";
    public const string ChangedAction = "patient.phone_changed";

    /// <summary>Why the old number's portal sessions ended (<see cref="UserSession.RevokedReason"/>).</summary>
    public const string SessionEndedReason = "number_changed";

    /// <summary>The edit's refusal of a new number: it changes only with a code (<see cref="PatientRegistrationService.UpdateAsync"/>).</summary>
    public const string NeedsCodeMessage = "The mobile number changes only with a code sent to the new number. Use Change number.";

    /// <summary>The change waiting for its code, or null when the patient has none that can still be confirmed.</summary>
    public async Task<Result<PhoneChangeDto?>> PendingAsync(Guid patientId, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var now = time.GetUtcNow();
        var pending = await db.Set<PatientPhoneChange>().AsNoTracking()
            .Where(c => c.PatientId == patientId && c.Status == PatientPhoneChangeStatus.Pending)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return pending is not null && pending.IsOpen(now) ? View(pending) : null;
    }

    /// <summary>
    /// Sends a code to the new number, replacing the patient's change still waiting for one. Refused for a number that is
    /// not an Indian mobile number or that the record has already, for a merged record, and once three codes went to the
    /// number in 15 minutes (with how long to wait).
    /// </summary>
    public async Task<Result<PhoneChangeDto>> RequestAsync(Guid patientId, RequestPhoneChangeRequest request, CancellationToken ct)
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

        var phone = Phone.Normalize(request.Phone);
        if (phone is null || !Phone.IsIndianMobile(phone))
        {
            return Error.Field("phone", PatientPhoneChange.IndianMobileMessage);
        }

        if (phone == patient.Phone)
        {
            const string same = "The patient is registered with this number already.";
            return Error.Validation("patient.phone_unchanged", same, new Dictionary<string, string[]> { ["phone"] = [same] });
        }

        // A patient's requests take turns, so two desks cannot leave two changes waiting; then a number's, so codes asked
        // for at once cannot get past three in 15 minutes: each code is five guesses at the number's portal account. Always
        // the patient's lock before the number's, so two requests never wait on each other.
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
                     .Where(c => c.PatientId == patientId && c.Status == PatientPhoneChangeStatus.Pending)
                     .ToListAsync(ct))
        {
            earlier.Supersede();
        }

        var facilityId = currentUser.FacilityId ?? patient.RegisteredFacilityId;
        var code = SecretTokens.NewNumericCode(6);
        var change = new PatientPhoneChange
        {
            FacilityId = facilityId,
            PatientId = patientId,
            Phone = phone,
            PreviousPhone = patient.Phone,
            ExpiresAt = now + PatientPhoneChange.Lifetime,
            Status = PatientPhoneChangeStatus.Pending,
        };
        change.CodeHash = PatientPhoneChange.HashOf(change.Id, code);
        db.Add(change);

        // To the new number only, without the patient's name: the number is not known to be theirs until the code comes back.
        notifications.Queue(
            Template,
            new NotificationRecipient(phone, null, patientId, null, PatientLanguages.OrDefault(PatientLanguages.FromPreference(patient.PreferredLanguage))),
            new Dictionary<string, string> { ["code"] = code, ["minutes"] = ((int)PatientPhoneChange.Lifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture) },
            [NotificationChannel.Sms],
            PatientPhoneChange.Lifetime,
            facilityId);

        audit.Add(new AuditEntry(AuditCategories.Data, CodeSentAction,
            $"Code sent by SMS to {Phone.Mask(phone)} to confirm it as the patient's mobile number (now {Describe(patient.Phone)})")
        {
            PatientId = patientId,
            EntityType = nameof(Patient),
            EntityId = patientId.ToString(),
            Details = new { changeId = change.Id, phone = Phone.Mask(phone), previousPhone = Masked(patient.Phone), expiresAt = change.ExpiresAt },
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return View(change);
    }

    /// <summary>
    /// Makes the change when the code entered is the one sent. Wrong codes count (five tries), and the change is made only
    /// while the record still has the number it had when the code was sent (409 otherwise: send a new code).
    /// </summary>
    public async Task<Result<PhoneChangeConfirmed>> ConfirmAsync(Guid patientId, Guid changeId, ConfirmPhoneChangeRequest request, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        // Tries of one code are judged one at a time, each saved before the next is read, so codes sent at once cannot get
        // past five tries. The record is held too: an edit saved meanwhile waits, and then finds the record changed.
        List<Guid> endedSessions;
        PhoneChangeConfirmed confirmed;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM patients.patients WHERE id = {patientId} FOR NO KEY UPDATE", ct);
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM patients.patient_phone_changes WHERE id = {changeId} FOR NO KEY UPDATE", ct);
            var change = await db.Set<PatientPhoneChange>().FirstOrDefaultAsync(c => c.Id == changeId && c.PatientId == patientId, ct);
            var patient = change is null ? null : await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == patientId, ct);
            if (change is null || patient is null)
            {
                return NotFound;
            }

            if (patient.MergedIntoPatientId is not null)
            {
                return Merged;
            }

            var now = time.GetUtcNow();
            if (change.WhyClosed(now) is { } closed)
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return closed;
            }

            if (patient.Phone != change.PreviousPhone)
            {
                change.Supersede();
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Error.Conflict("patient.phone_changed_meanwhile",
                    $"The patient's mobile number was changed to {Describe(patient.Phone)} after this code was sent. Send a new code if it should still change.");
            }

            var checkedCode = change.Confirm(PatientPhoneChange.HashOf(change.Id, request.Code), currentUser.UserId, now);
            if (checkedCode.IsFailure)
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return checkedCode.Error!;
            }

            var oldPhone = patient.Phone;
            patient.Phone = change.Phone;
            await PortalLinkService.RevokeLiveLinksAsync(db, patient.Id, PortalLink.NumberChangedReason, now, ct);
            var moved = await family.FollowNumberChangeAsync(patient, oldPhone, change.Phone, ct);
            await db.SaveChangesAsync(ct);

            // Saved first, so the check reads the record and the links as they are now.
            var reachesNobody = oldPhone is not null && !await PortalPatientScope.ReachedBy(db, oldPhone).AnyAsync(ct);
            endedSessions = reachesNobody ? await SignOutAsync(oldPhone!, now, ct) : [];

            var askedBy = await NameOfAsync(change.CreatedBy, ct);
            var confirmedBy = currentUser.DisplayName ?? currentUser.UserName;
            audit.Add(new AuditEntry(AuditCategories.Data, ChangedAction,
                $"Mobile number changed from {Describe(oldPhone)} to {Phone.Mask(change.Phone)} with a code sent to the new number; asked for by {askedBy ?? "unknown"}, confirmed by {confirmedBy ?? "unknown"}")
            {
                PatientId = patient.Id,
                EntityType = nameof(Patient),
                EntityId = patient.Id.ToString(),
                Severity = AuditSeverity.Warning,
                Details = new
                {
                    changeId = change.Id,
                    phone = Phone.Mask(change.Phone),
                    previousPhone = Masked(oldPhone),
                    askedById = change.CreatedBy,
                    askedBy,
                    confirmedById = currentUser.UserId,
                    confirmedBy,
                    familyLinksMoved = moved,
                    oldNumberReachesNoPatient = reachesNobody,
                    portalSessionsEnded = endedSessions.Count,
                },
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            confirmed = new PhoneChangeConfirmed(Phone.Mask(change.Phone), moved, endedSessions.Count > 0);
        }

        // Once saved, never before: another server told sooner could read the sessions as they were and keep them.
        foreach (var session in endedSessions)
        {
            sessions.Invalidate(session);
        }

        return confirmed;
    }

    private static Error Merged => Error.Rule("patient.merged", "This record was merged into another patient and can no longer be edited.");

    private static Error NotFound => new("patient.phone_change_not_found", "This number change was not found. Send a new code.", ErrorType.NotFound);

    private static PhoneChangeDto View(PatientPhoneChange change) => new(change.Id, Phone.Mask(change.Phone), change.ExpiresAt, change.AttemptsLeft);

    private static string Describe(string? phone) => phone is null ? "none" : Phone.Mask(phone);

    private static string? Masked(string? phone) => phone is null ? null : Phone.Mask(phone);

    /// <summary>
    /// The old number reaches nobody's records any more: its portal account's open sessions end, and the account forgets
    /// the patient (the name it showed, the patient it pointed to). The account itself stays, for whoever holds the number
    /// to sign in to later with a code, seeing only what that number reaches then. Returns the sessions ended.
    /// </summary>
    private async Task<List<Guid>> SignOutAsync(string oldPhone, DateTimeOffset now, CancellationToken ct)
    {
        var account = await db.Set<User>().FirstOrDefaultAsync(u => u.Type == UserType.Patient && u.Phone == oldPhone, ct);
        if (account is null)
        {
            return [];
        }

        account.DisplayName = Phone.Mask(oldPhone);
        account.PatientId = null;
        var open = await db.Set<UserSession>().Where(s => s.UserId == account.Id && s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in open)
        {
            session.RevokedAt = now;
            session.RevokedReason = SessionEndedReason;
        }

        return [.. open.Select(s => s.Id)];
    }

    private async Task<string?> NameOfAsync(Guid? userId, CancellationToken ct) =>
        userId is { } id ? await db.Set<User>().IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
}
