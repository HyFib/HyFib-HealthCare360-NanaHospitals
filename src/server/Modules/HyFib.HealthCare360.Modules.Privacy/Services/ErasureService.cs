using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>Something that can be erased at once, whether it can be erased for this patient, and why not.</summary>
public sealed record ErasureOption(string Item, string Title, string Description, bool Available, string? Reason);

/// <summary>Records the law requires the hospital to keep, why, and until when (no date: permanently).</summary>
public sealed record ErasureHoldItem(string Key, string Title, string Reason, DateOnly? Until, bool Permanently);

public sealed record ErasureAssessment(IReadOnlyList<ErasureOption> Options, IReadOnlyList<ErasureHoldItem> Holds, DateOnly? KeepUntil, bool KeepPermanently);

public sealed record ErasureOutcome(IReadOnlyList<string> Erased, DateOnly? KeepUntil, bool KeepPermanently, string Summary);

/// <summary>
/// Erasure for a data principal (DPDP Act 2023 s12(3) and s8(7)): what can go at once, and what the law requires the
/// hospital to keep, and until when. Medical records, bills and the audit trail are never erased here; they are kept
/// for their legal period and then reviewed (see <see cref="RetentionService"/>). Changes to other modules' data go
/// through their services.
/// </summary>
public sealed class ErasureService(
    HospitalDbContext db,
    CommunicationConsentService consents,
    PatientFeedbackEraser feedback,
    RetentionService retention,
    ISessionValidator sessions,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string Messages = "messages";
    public const string Feedback = "feedback";
    public const string NomineeItem = "nominee";
    public const string PortalAccount = "portal";

    private static readonly string[] Items = [Messages, Feedback, NomineeItem, PortalAccount];

    public static bool IsKnownItem(string? item) => item is not null && Items.Contains(item, StringComparer.Ordinal);

    public async Task<ErasureAssessment> AssessAsync(Guid patientId, CancellationToken ct)
    {
        var options = await OptionsAsync(patientId, ct);
        var holds = await HoldsAsync(patientId, ct);
        return new ErasureAssessment(options, holds, holds.Where(h => !h.Permanently).Max(h => h.Until), holds.Any(h => h.Permanently));
    }

    /// <summary>
    /// Carries out the chosen erasures for an erasure request, in one transaction, and records on the request what was
    /// erased and until when the rest must be kept. The requester's identity must have been checked.
    /// </summary>
    public async Task<Result<ErasureOutcome>> EraseAsync(DataRequest request, IReadOnlyList<string> items, CancellationToken ct)
    {
        var allowed = request.CanRecordErasure();
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        if (request.PatientId is not { } patientId)
        {
            return Error.Rule("privacy.no_patient", "This request is not linked to a patient record, so there is nothing in HC360 to erase.");
        }

        var chosen = items.Distinct(StringComparer.Ordinal).ToList();
        var options = await OptionsAsync(patientId, ct);
        if (chosen.Select(i => options.First(o => o.Item == i)).FirstOrDefault(o => !o.Available) is { } unavailable)
        {
            return Error.Rule("privacy.erasure_unavailable", $"{unavailable.Title}: {unavailable.Reason}");
        }

        var now = time.GetUtcNow();
        var closedSessions = new List<Guid>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var item in chosen)
        {
            switch (item)
            {
                case Messages:
                    var withdrawn = await consents.SetPreferencesAsync(patientId, marketing: false, updates: false, $"privacy request {request.RequestNumber}", ct);
                    if (withdrawn.IsFailure)
                    {
                        return withdrawn.Error!;
                    }

                    break;
                case Feedback:
                    await feedback.EraseAsync(patientId, ct);
                    break;
                case NomineeItem:
                    db.RemoveRange(await db.Set<Nominee>().Where(n => n.PatientId == patientId).ToListAsync(ct));
                    break;
                case PortalAccount:
                    closedSessions.AddRange(await ClosePortalAccountAsync(patientId, now, ct));
                    break;
            }
        }

        var holds = await HoldsAsync(patientId, ct);
        var keepUntil = holds.Where(h => !h.Permanently).Max(h => h.Until);
        var keepPermanently = holds.Any(h => h.Permanently);
        var summary = Describe(chosen, holds);
        var recorded = request.RecordErasure(chosen, summary, keepUntil, keepPermanently, currentUser.UserId, now);
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "privacy.erasure", $"Erased for privacy request {request.RequestNumber}: {string.Join(", ", chosen)}")
        {
            PatientId = patientId,
            EntityType = nameof(DataRequest),
            EntityId = request.Id.ToString(),
            Details = new { items = chosen, keepUntil, keepPermanently },
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        foreach (var session in closedSessions)
        {
            sessions.Invalidate(session);
        }

        return new ErasureOutcome(chosen, keepUntil, keepPermanently, summary);
    }

    private async Task<List<ErasureOption>> OptionsAsync(Guid patientId, CancellationToken ct)
    {
        var preferences = await consents.PreferencesAsync(patientId, ct);
        var feedbackCount = await feedback.CountAsync(patientId, ct);
        var hasNominee = await db.Set<Nominee>().AnyAsync(n => n.PatientId == patientId, ct);
        var (account, sharing) = await PortalAccountAsync(patientId, ct);

        return
        [
            new(Messages, "Stop optional messages",
                "Withdraw consent for health offers and for update messages such as reminders and report alerts. Sign-in codes are still sent when the patient uses the portal.",
                preferences.Marketing || preferences.Updates, preferences.Marketing || preferences.Updates ? null : "Optional messages are already off."),
            new(Feedback, "Remove the patient from feedback, complaints and enquiries",
                "The ratings and complaints stay in the hospital's quality records, without the patient's name, number, visit or comments. Feedback links already sent stop working.",
                feedbackCount > 0, feedbackCount > 0 ? null : "There is nothing to remove."),
            new(NomineeItem, "Remove the nominee", "The nominee will no longer be able to act for the patient.",
                hasNominee, hasNominee ? null : "No nominee is recorded."),
            new(PortalAccount, "Close the portal account",
                "The patient portal account for the patient's mobile number is closed and signed out everywhere.",
                account is not null && sharing == 0,
                account is null ? "There is no open portal account for the patient's mobile number."
                : sharing > 0 ? string.Create(CultureInfo.InvariantCulture,
                    $"{sharing} other patient(s) sign in with the same mobile number. Closing the account would lock them out; move them to another number first.")
                : null),
        ];
    }

    /// <summary>The portal account that signs in with the patient's mobile number, and how many other patients share that number.</summary>
    private async Task<(User? Account, int OtherPatients)> PortalAccountAsync(Guid patientId, CancellationToken ct)
    {
        var phone = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.Phone).FirstOrDefaultAsync(ct);
        if (phone is null)
        {
            return (null, 0);
        }

        var account = await db.Set<User>().FirstOrDefaultAsync(u => u.Type == UserType.Patient && u.IsActive && u.Phone == phone, ct);
        var others = await db.Set<Patient>().CountAsync(p => p.Phone == phone && p.Id != patientId && p.IsActive && p.MergedIntoPatientId == null, ct);
        return (account, others);
    }

    private async Task<List<Guid>> ClosePortalAccountAsync(Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        var (account, _) = await PortalAccountAsync(patientId, ct);
        if (account is null)
        {
            return [];
        }

        account.IsActive = false;
        var open = await db.Set<UserSession>().Where(s => s.UserId == account.Id && s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in open)
        {
            session.RevokedAt = now;
            session.RevokedReason = "erasure_request";
        }

        return [.. open.Select(s => s.Id)];
    }

    /// <summary>What must be kept, and until when: medical records, bills, the audit trail and the message log.</summary>
    private async Task<List<ErasureHoldItem>> HoldsAsync(Guid patientId, CancellationToken ct)
    {
        var holds = new List<ErasureHoldItem>();
        var outpatientYears = await retention.ValueAsync(RetentionSchedule.Get(RetentionSchedule.OutpatientRecords), ct) ?? RecordYears.Outpatient;
        var inpatientYears = await retention.ValueAsync(RetentionSchedule.Get(RetentionSchedule.InpatientRecords), ct) ?? RecordYears.Inpatient;
        var charts = db.Set<MedicalRecord>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Where(m => m.PatientId == patientId);
        var visits = db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Where(v => v.PatientId == patientId);

        if (await visits.AnyAsync(v => v.IsMlc, ct) || await charts.AnyAsync(m => m.IsMlc, ct))
        {
            holds.Add(new("records", "Medical records", "The patient has a medico-legal case, whose records are kept permanently.", null, Permanently: true));
        }
        else
        {
            var lastVisit = await visits.MaxAsync(v => (DateTimeOffset?)v.VisitedAt, ct);
            var lastDischarge = await charts.Where(m => m.AdmissionId != null).MaxAsync(m => m.DischargedAt, ct);
            var chartUntil = await charts.MaxAsync(m => m.RetainUntil, ct);
            var until = Latest(
                lastVisit is { } visited ? HospitalDate(visited).AddYears(outpatientYears) : null,
                lastDischarge is { } discharged ? HospitalDate(discharged).AddYears(inpatientYears) : null,
                chartUntil);
            if (until is not null)
            {
                holds.Add(new("records", "Medical records", string.Create(CultureInfo.InvariantCulture,
                    $"Hospitals must keep medical records for at least {outpatientYears} years after an out-patient visit and {inpatientYears} years after a hospital stay."),
                    until, Permanently: false));
            }
        }

        var financeYears = await retention.ValueAsync(RetentionSchedule.Get(RetentionSchedule.Finance), ct) ?? 8;
        var lastBill = await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(b => b.PatientId == patientId).MaxAsync(b => (DateTimeOffset?)b.CreatedAt, ct);
        if (lastBill is { } billed)
        {
            holds.Add(new("finance", "Bills and payments", string.Create(CultureInfo.InvariantCulture,
                $"Tax law requires financial records to be kept for {financeYears} years."), HospitalDate(billed).AddYears(financeYears), Permanently: false));
        }

        var auditYears = await retention.ValueAsync(RetentionSchedule.Get(RetentionSchedule.Audit), ct) ?? 10;
        var lastAccess = await db.Set<AuditEvent>().AsNoTracking().Where(a => a.PatientId == patientId).MaxAsync(a => (DateTimeOffset?)a.OccurredAt, ct);
        if (lastAccess is { } accessed)
        {
            holds.Add(new("audit", "Record access log", string.Create(CultureInfo.InvariantCulture,
                $"The tamper-evident log of who opened or changed the record is kept for {auditYears} years and never edited."),
                HospitalDate(accessed).AddYears(auditYears), Permanently: false));
        }

        var messageDays = await retention.ValueAsync(RetentionSchedule.Get(RetentionSchedule.MessageLog), ct) ?? 365;
        var lastMessage = await db.Set<NotificationLog>().IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.PatientId == patientId).MaxAsync(l => (DateTimeOffset?)l.CreatedAt, ct);
        if (lastMessage is { } messaged)
        {
            holds.Add(new("messages", "Message log", string.Create(CultureInfo.InvariantCulture,
                $"The log of messages sent is kept for {messageDays} days, then deleted automatically."),
                HospitalDate(messaged).AddDays(messageDays), Permanently: false));
        }

        return holds;
    }

    /// <summary>The line recorded on the request (and shown to the principal) for what was erased and what is kept.</summary>
    internal static string Describe(IReadOnlyList<string> erased, IReadOnlyList<ErasureHoldItem> holds)
    {
        var done = erased.Select(i => i switch
        {
            Messages => "optional messages stopped",
            Feedback => "your name and number removed from feedback, complaints and enquiries",
            NomineeItem => "nominee removed",
            PortalAccount => "portal account closed",
            _ => i,
        });
        var kept = holds.Select(h => h.Permanently
            ? $"{h.Title.ToLowerInvariant()} permanently"
            : $"{h.Title.ToLowerInvariant()} until {h.Until!.Value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}");
        var text = $"Erased: {string.Join("; ", done)}.";
        return holds.Count == 0 ? text : $"{text} Kept as the law requires: {string.Join("; ", kept)}.";
    }

    private static DateOnly? Latest(params DateOnly?[] dates) => dates.Max();

    private static DateOnly HospitalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(TimeSpan.FromMinutes(330)).DateTime);

    private static class RecordYears
    {
        public const int Outpatient = 3;
        public const int Inpatient = 5;
    }
}
