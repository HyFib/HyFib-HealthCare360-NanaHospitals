using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>A patient's messaging choices and when each last changed.</summary>
public sealed record CommunicationPreferences(bool Marketing, bool Updates, DateTimeOffset? MarketingChangedAt, DateTimeOffset? UpdatesChangedAt);

/// <summary>
/// Messaging consent under the DPDP Act 2023, which requires that withdrawing consent be as easy as giving it. It
/// covers two consent types:
/// <list type="bullet">
/// <item><see cref="ConsentType.MarketingCommunication"/> for campaigns.</item>
/// <item><see cref="ConsentType.SmsWhatsAppUpdates"/> for service messages such as reminders and feedback links.</item>
/// </list>
/// The records are the Patients module's consents (EHR-06), so each patient's consent history stays in one place.
/// A withdrawal revokes every active grant and records the refusal with its source. An explicit opt-out is recorded
/// even for a patient who never opted in, so the request itself is documented. A STOP or START on WhatsApp comes from a
/// number, not a patient, so it records only what it changes (<see cref="WhatsAppReplyConsent"/>).
/// </summary>
public sealed class CommunicationConsentService(HospitalDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    private const int MaxReasonLength = 300;
    private const int MaxNotesLength = 1000;

    public async Task<bool> HasActiveConsentAsync(Guid patientId, CancellationToken ct) =>
        await db.Set<Patient>().Where(p => p.Id == patientId).Where(CampaignAudienceQuery.HasActiveMarketingConsent(db.Set<PatientConsent>())).AnyAsync(ct);

    /// <summary>Is the patient's latest SMS/WhatsApp-updates consent still in force (service messages)?</summary>
    public async Task<bool> HasTransactionalConsentAsync(Guid patientId, CancellationToken ct)
    {
        var latest = await db.Set<PatientConsent>()
            .Where(c => c.PatientId == patientId && c.Type == ConsentType.SmsWhatsAppUpdates)
            .OrderByDescending(c => c.CapturedAt)
            .Select(c => new { c.Granted, c.RevokedAt })
            .FirstOrDefaultAsync(ct);
        return latest is { Granted: true, RevokedAt: null };
    }

    public async Task<CommunicationPreferences> PreferencesAsync(Guid patientId, CancellationToken ct)
    {
        var history = await db.Set<PatientConsent>().AsNoTracking()
            .Where(c => c.PatientId == patientId && (c.Type == ConsentType.MarketingCommunication || c.Type == ConsentType.SmsWhatsAppUpdates))
            .Select(c => new { c.Type, c.CapturedAt, c.RevokedAt })
            .ToListAsync(ct);

        DateTimeOffset? LastChange(ConsentType type) =>
            history.Where(c => c.Type == type)
                .Select(c => c.RevokedAt is { } revoked && revoked > c.CapturedAt ? revoked : c.CapturedAt)
                .Select(at => (DateTimeOffset?)at)
                .Max();

        return new CommunicationPreferences(
            await HasActiveConsentAsync(patientId, ct),
            await HasTransactionalConsentAsync(patientId, ct),
            LastChange(ConsentType.MarketingCommunication),
            LastChange(ConsentType.SmsWhatsAppUpdates));
    }

    /// <summary>Explicit marketing opt-out (link, staff or portal); always documented.</summary>
    public async Task<Result> OptOutAsync(Guid patientId, string reason, CancellationToken ct)
    {
        if (!await db.Set<Patient>().AnyAsync(p => p.Id == patientId, ct))
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        await WithdrawAsync(patientId, ConsentType.MarketingCommunication, $"Opted out: {reason}", Trim(reason, MaxReasonLength), ct);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>The patient's own choices from the portal; only actual changes are recorded.</summary>
    public async Task<Result> SetPreferencesAsync(Guid patientId, bool marketing, bool updates, string via, CancellationToken ct)
    {
        if (!await db.Set<Patient>().AnyAsync(p => p.Id == patientId, ct))
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (marketing != await HasActiveConsentAsync(patientId, ct))
        {
            await ChangeAsync(patientId, ConsentType.MarketingCommunication, marketing, via, ct);
        }

        if (updates != await HasTransactionalConsentAsync(patientId, ct))
        {
            await ChangeAsync(patientId, ConsentType.SmsWhatsAppUpdates, updates, via, ct);
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// A STOP (<paramref name="optedOut"/>) or START sent on WhatsApp from <paramref name="phone"/>, applied to the
    /// marketing consent of every patient registered with that number (see <see cref="WhatsAppReplyConsent"/>). Adds to
    /// the caller's unit of work; the caller saves. Returns the number of patients whose consent changed.
    /// </summary>
    public async Task<int> ApplyWhatsAppReplyAsync(string phone, bool optedOut, DateTimeOffset repliedAt, CancellationToken ct)
    {
        var patientIds = await db.Set<Patient>().Where(WhatsAppReplyConsent.RegisteredWith(phone)).Select(p => p.Id).ToListAsync(ct);
        if (patientIds.Count == 0)
        {
            return 0;
        }

        var history = await db.Set<PatientConsent>()
            .Where(c => patientIds.Contains(c.PatientId) && c.Type == ConsentType.MarketingCommunication)
            .ToListAsync(ct);
        var changed = 0;
        foreach (var patientId in patientIds)
        {
            var record = WhatsAppReplyConsent.Apply(patientId, history.Where(c => c.PatientId == patientId).ToList(), optedOut, repliedAt, currentUser.UserId);
            if (record is not null)
            {
                db.Add(record);
                changed++;
            }
        }

        return changed;
    }

    private Task ChangeAsync(Guid patientId, ConsentType type, bool granted, string via, CancellationToken ct)
    {
        if (!granted)
        {
            return WithdrawAsync(patientId, type, $"Withdrawn via {via}", $"Withdrawn via {via}", ct);
        }

        Record(patientId, type, granted: true, $"Given via {via}");
        return Task.CompletedTask;
    }

    private async Task WithdrawAsync(Guid patientId, ConsentType type, string notes, string revokedReason, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var active = await db.Set<PatientConsent>()
            .Where(c => c.PatientId == patientId && c.Type == type && c.Granted && c.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var consent in active)
        {
            consent.RevokedAt = now;
            consent.RevokedReason = Trim(revokedReason, MaxReasonLength);
        }

        Record(patientId, type, granted: false, notes);
    }

    private void Record(Guid patientId, ConsentType type, bool granted, string notes) =>
        db.Add(new PatientConsent
        {
            PatientId = patientId,
            Type = type,
            Granted = granted,
            CapturedAt = time.GetUtcNow(),
            CapturedByUserId = currentUser.UserId,
            Notes = Trim(notes, MaxNotesLength),
        });

    private static string Trim(string value, int max)
    {
        var text = value.Trim();
        return text.Length <= max ? text : text[..max];
    }
}
