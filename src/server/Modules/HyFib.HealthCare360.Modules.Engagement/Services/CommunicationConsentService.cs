using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>A patient's messaging choices and when each last changed.</summary>
/// <param name="WhatsApp">The patient's number gets reminders and updates on WhatsApp: opted in, and no STOP in force.</param>
/// <param name="WhatsAppEditable">
/// The portal user may change <paramref name="WhatsApp"/>: they signed in with the patient's own number. WhatsApp choices
/// belong to the number, so a family member signed in with another number sees them but can't change them.
/// </param>
public sealed record CommunicationPreferences(
    bool Marketing,
    bool Updates,
    DateTimeOffset? MarketingChangedAt,
    DateTimeOffset? UpdatesChangedAt,
    bool WhatsApp = false,
    DateTimeOffset? WhatsAppChangedAt = null,
    bool WhatsAppEditable = false);

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
/// <para>
/// WhatsApp itself is a third choice, of the number rather than the patient (<see cref="WhatsAppConsent"/>): reminders
/// and campaigns go on WhatsApp only to a number that opted in, on top of the consent above.
/// </para>
/// </summary>
public sealed class CommunicationConsentService(HospitalDbContext db, ICurrentUser currentUser, WhatsAppConsent numbers, TimeProvider time)
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

    public Task<CommunicationPreferences> PreferencesAsync(Guid patientId, CancellationToken ct) => PreferencesAsync(patientId, null, ct);

    /// <param name="accountPhone">The number the portal user signed in with (E.164): WhatsApp is theirs to change only on it.</param>
    public async Task<CommunicationPreferences> PreferencesAsync(Guid patientId, string? accountPhone, CancellationToken ct)
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

        var phone = await PhoneAsync(patientId, ct);
        var number = await numbers.StatusAsync(phone, ct);
        DateTimeOffset? whatsAppChanged = (number.OptedInAt, number.OptedOutAt) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            (var a, var b) => a ?? b,
        };
        return new CommunicationPreferences(
            await HasActiveConsentAsync(patientId, ct),
            await HasTransactionalConsentAsync(patientId, ct),
            LastChange(ConsentType.MarketingCommunication),
            LastChange(ConsentType.SmsWhatsAppUpdates),
            number.Reachable,
            whatsAppChanged,
            phone is not null && phone == accountPhone);
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
    /// <param name="marketing">Marketing messages on or off; null leaves the choice as it is.</param>
    /// <param name="updates">Updates by SMS and WhatsApp on or off; null leaves the choice as it is.</param>
    /// <param name="whatsApp">WhatsApp on or off for the patient's number; null leaves it as it is.</param>
    /// <param name="accountPhone">The number the portal user signed in with: only its holder changes its WhatsApp choice.</param>
    public async Task<Result> SetPreferencesAsync(
        Guid patientId, bool? marketing, bool? updates, string via, CancellationToken ct, bool? whatsApp = null, string? accountPhone = null)
    {
        if (!await db.Set<Patient>().AnyAsync(p => p.Id == patientId, ct))
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (whatsApp is { } wanted)
        {
            var phone = await PhoneAsync(patientId, ct);
            if (wanted != (await numbers.StatusAsync(phone, ct)).Reachable)
            {
                if (phone is null || phone != accountPhone)
                {
                    return Error.Rule("portal.whatsapp_not_holder",
                        "WhatsApp messages can be switched on or off only by signing in with the patient's own mobile number.");
                }

                // Signed in with this number, so the person is its holder: switching on lifts a STOP it sent, as START would.
                _ = wanted
                    ? await numbers.OptInAsync(phone, patientId, WhatsAppOptInSource.Portal, null, byNumberHolder: true, ct)
                    : await numbers.OptOutAsync(phone, null, ct);
            }
        }

        if (marketing is { } wantsMarketing && wantsMarketing != await HasActiveConsentAsync(patientId, ct))
        {
            await ChangeAsync(patientId, ConsentType.MarketingCommunication, wantsMarketing, via, ct);
        }

        if (updates is { } wantsUpdates && wantsUpdates != await HasTransactionalConsentAsync(patientId, ct))
        {
            await ChangeAsync(patientId, ConsentType.SmsWhatsAppUpdates, wantsUpdates, via, ct);
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

    private Task<string?> PhoneAsync(Guid patientId, CancellationToken ct) =>
        db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.Phone).FirstOrDefaultAsync(ct);

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
            consent.Revoke(Trim(revokedReason, MaxReasonLength), currentUser.UserId, now);
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
