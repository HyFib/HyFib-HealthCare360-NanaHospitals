using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// "Confirm your details" in the patient portal (ASVS V6.6.1). After sign-in the portal tells the account that records on
/// its number wait to be confirmed (<see cref="PortalPatientScope.UnconfirmedPatientIdsAsync"/>, a count and nothing more),
/// and asks for the patient's date of birth, or the UHID on their hospital card. Each record the details match is shown
/// from then on, and its number is verified (<see cref="Patient.MarkPhoneVerified"/>), which also lets its documents go to
/// the number (<see cref="VerifiedNumbers"/>).
/// <para>
/// Wrong answers are limited per number: <see cref="PortalRecordConfirmation.MaxFailures"/> in a day, counted under a lock
/// on the number so tries sent at once cannot get past it, then refused with how long to wait. Every try, right or wrong,
/// is in the audit trail; what was typed is not kept. The front desk can always verify the number with a code instead
/// (<see cref="PatientPhoneVerificationService"/>).
/// </para>
/// </summary>
public sealed class PortalRecordConfirmationService(HospitalDbContext db, PortalPatientScope scope, IAuditLogger audit, TimeProvider time)
{
    public const string FailedAction = "portal.record_confirm_failed";
    public const string ThrottledAction = "portal.record_confirm_throttled";

    /// <summary>How many records wait on the account's number, and how many wrong tries it has left.</summary>
    public async Task<Result<RecordsToConfirmDto>> StatusAsync(CancellationToken ct)
    {
        if (await scope.AccountPhoneAsync(ct) is not { } phone)
        {
            return NotSignedIn;
        }

        var now = time.GetUtcNow();
        var waiting = await scope.UnconfirmedPatientIdsAsync(ct);
        var failures = await FailuresAsync(phone, now, ct);
        return PortalRecordConfirmation.WaitBeforeNext(failures, now) is { } wait
            ? new RecordsToConfirmDto(waiting.Count, 0, Seconds(wait))
            : new RecordsToConfirmDto(waiting.Count, PortalRecordConfirmation.AttemptsLeft(failures.Count), null);
    }

    /// <summary>
    /// Confirms the records waiting on the account's number that the details match: shown from now on, their number verified.
    /// Details that match none count as a wrong try (422 <c>portal.confirm_no_match</c> with the tries left); once the number
    /// has none left, 422 <c>portal.confirm_throttled</c> with how long to wait, whatever is given.
    /// </summary>
    public async Task<Result<RecordsConfirmedDto>> ConfirmAsync(ConfirmRecordsRequest request, CancellationToken ct)
    {
        if (await scope.AccountPhoneAsync(ct) is not { } phone)
        {
            return NotSignedIn;
        }

        // The number's tries take turns, each saved before the next is counted: tries sent at once would otherwise all count
        // the same wrong answers and all be judged.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"patients.portal_confirm:" + phone}, 0))", ct);
        var now = time.GetUtcNow();
        var masked = Phone.Mask(phone);
        var method = string.IsNullOrWhiteSpace(request.Uhid) ? RecordConfirmationMethod.DateOfBirth : RecordConfirmationMethod.Uhid;
        var failures = await FailuresAsync(phone, now, ct);
        if (PortalRecordConfirmation.WaitBeforeNext(failures, now) is { } wait)
        {
            audit.Add(new AuditEntry(AuditCategories.Security, ThrottledAction,
                $"The portal account of {masked} tried to confirm a record on its number after {PortalRecordConfirmation.MaxFailures} wrong tries in a day; refused")
            {
                Severity = AuditSeverity.Warning,
                Details = new { phone = masked, method = method.ToString(), retryAfterSeconds = Seconds(wait) },
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return PortalRecordConfirmation.Throttled(wait);
        }

        var waiting = await scope.UnconfirmedPatientIdsAsync(ct);
        if (waiting.Count == 0)
        {
            return Error.Rule(PortalRecordConfirmation.NothingToConfirmCode, "There are no records waiting to be confirmed on this mobile number.");
        }

        var candidates = await db.Set<Patient>().Where(p => waiting.Contains(p.Id)).OrderBy(p => p.RegisteredAt).ToListAsync(ct);
        var matched = candidates
            .Where(p => PortalRecordConfirmation.Matches(p.Uhid, p.DateOfBirth, p.IsDateOfBirthEstimated, request.DateOfBirth, request.Uhid))
            .ToList();
        db.Add(new PortalRecordConfirmation { AccountPhone = phone, Method = method, Confirmed = matched.Count });
        var what = method == RecordConfirmationMethod.Uhid ? "UHID" : "date of birth";
        if (matched.Count == 0)
        {
            List<DateTimeOffset> after = [now, .. failures];
            var left = PortalRecordConfirmation.AttemptsLeft(after.Count);
            audit.Add(new AuditEntry(AuditCategories.Security, FailedAction,
                $"The portal account of {masked} gave a {what} that matches no record waiting on its number; {left} {(left == 1 ? "try" : "tries")} left")
            {
                Severity = AuditSeverity.Warning,
                Details = new { phone = masked, method = method.ToString(), attemptsLeft = left, waiting = waiting.Count },
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return PortalRecordConfirmation.WaitBeforeNext(after, now) is { } next
                ? PortalRecordConfirmation.Throttled(next)
                : PortalRecordConfirmation.NoMatch(left);
        }

        foreach (var patient in matched)
        {
            patient.MarkPhoneVerified(now);
            audit.Add(new AuditEntry(AuditCategories.Data, PatientPhoneVerificationService.VerifiedAction,
                $"Mobile number {masked} verified in the patient portal: the account signed in with it gave the patient's {what}")
            {
                PatientId = patient.Id,
                EntityType = nameof(Patient),
                EntityId = patient.Id.ToString(),
                Details = new { phone = masked, how = "portal", method = method.ToString() },
            });
        }

        // The account takes the name a first sign-in gives (PortalAccountResolver), now that a record on its number is verified.
        var account = await db.Set<User>().FirstOrDefaultAsync(u => u.Type == UserType.Patient && u.Phone == phone && u.PatientId == null, ct);
        if (account is not null)
        {
            (account.PatientId, account.DisplayName) = (matched[0].Id, matched[0].FullName);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new RecordsConfirmedDto(matched.Count, waiting.Count - matched.Count);
    }

    /// <summary>The number's wrong answers in the last day, newest first (as many as the limit needs).</summary>
    private Task<List<DateTimeOffset>> FailuresAsync(string phone, DateTimeOffset now, CancellationToken ct)
    {
        var windowStart = now - PortalRecordConfirmation.Window;
        return db.Set<PortalRecordConfirmation>().AsNoTracking()
            .Where(a => a.AccountPhone == phone && a.Confirmed == 0 && a.CreatedAt > windowStart)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.CreatedAt)
            .Take(PortalRecordConfirmation.MaxFailures)
            .ToListAsync(ct);
    }

    private static int Seconds(TimeSpan wait) => Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));

    private static Error NotSignedIn => Error.Forbidden("portal.account_required", "Sign in to the patient portal first.");
}
