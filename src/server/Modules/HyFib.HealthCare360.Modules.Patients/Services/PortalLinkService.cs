using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Links to the patient portal sent to the patient's registered number (PP-01). Staff send one from the patient's record
/// by SMS, WhatsApp or both; the patient opens it and asks for a sign-in code, which goes to the same number through the
/// portal's own sign-in (<see cref="AuthService.RequestOtpAsync"/>, with its limit per number), and signs in with that
/// code as on the sign-in page. So the link proves nothing by itself: whoever holds it can only send a code to the
/// number the hospital has for the patient. Only the hash of its token is kept.
/// </summary>
public sealed class PortalLinkService(
    HospitalDbContext db,
    PatientAccessGuard guard,
    INotificationService notifications,
    WhatsAppConsent whatsApp,
    IFeatureFlags features,
    ISettingsService settings,
    IPermissionService permissions,
    AuthService auth,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string Template = "portal.link";

    /// <summary>How many of the patient's latest links the record shows.</summary>
    public const int Shown = 5;

    /// <summary>How long the message may wait in the outbox (an internet outage): a link that arrives a day late helps nobody.</summary>
    private static readonly TimeSpan MessageLifetime = TimeSpan.FromDays(1);

    private const string WhatsAppSkipped = "This number replied STOP on WhatsApp, so the link went by SMS only.";

    // ------------------------------------------------------------------ staff

    /// <summary>
    /// Sends a new link to the patient's registered number, replacing any link sent earlier that still works. Refused,
    /// each with a code of its own, while the portal has no address, for a record no longer in use, without a mobile
    /// number, in a protected category, on WhatsApp alone to a number that replied STOP, and after
    /// <see cref="PortalLink.MaxPerWindow"/> links in 24 hours.
    /// </summary>
    public async Task<Result<PortalLinkSent>> SendAsync(Guid patientId, SendPortalLinkRequest request, CancellationToken ct)
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

        var facilityId = currentUser.FacilityId ?? patient.RegisteredFacilityId;
        var baseUrl = await BaseUrlAsync(facilityId, ct);
        if (baseUrl is null)
        {
            return PortalLinkRules.NoAddress;
        }

        if (PortalLinkRules.WhyNotSend(patient) is { } refusal)
        {
            return refusal;
        }

        var phone = patient.Phone!;
        var channels = request.Channels.Aggregate(PortalLinkChannels.None, (all, c) => all | Of(c));
        string? notice = null;
        if (channels.HasFlag(PortalLinkChannels.WhatsApp) && (await whatsApp.StatusAsync(phone, ct)).OptedOut)
        {
            if (!channels.HasFlag(PortalLinkChannels.Sms))
            {
                return PortalLinkRules.WhatsAppStopped;
            }

            channels = PortalLinkChannels.Sms;
            notice = WhatsAppSkipped;
        }

        // The patient's sends take turns until the new link is saved: sends at the same moment, from two desks, would
        // otherwise each count the same links and all pass the limit.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"portal.link:" + patientId.ToString("N")}, 0))", ct);

        // The patient's links from every facility: the portal is the hospital group's, so is the limit, and a new link
        // replaces the one another branch sent.
        var now = time.GetUtcNow();
        var windowStart = PortalLinkRules.WindowStart(now);
        var links = db.Set<PortalLink>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(l => l.PatientId == patientId);
        if (PortalLinkRules.IsThrottled(await links.CountAsync(l => l.CreatedAt > windowStart, ct)))
        {
            return PortalLinkRules.Throttled;
        }

        foreach (var earlier in await links.Where(l => l.RevokedAt == null && l.ExpiresAt > now).ToListAsync(ct))
        {
            earlier.Revoke(PortalLink.ReplacedReason, now);
        }

        var token = SecretTokens.NewOpaqueToken(16);
        var link = new PortalLink
        {
            FacilityId = facilityId,
            PatientId = patientId,
            Phone = phone,
            TokenHash = SecretTokens.Hash(token),
            Channels = channels,
            ExpiresAt = now + PortalLink.Validity,
        };
        db.Add(link);

        notifications.Queue(
            Template,
            new NotificationRecipient(phone, null, patientId, patient.FullName, PatientLanguages.OrDefault(PatientLanguages.FromPreference(patient.PreferredLanguage))),
            new Dictionary<string, string> { ["link"] = PortalAddress.Page(baseUrl, "link/" + token) },
            ChannelList(channels),
            MessageLifetime,
            facilityId);

        // On the patient's record, so its history says who sent a link, when and how (the link's own row is in the trail too).
        audit.Add(new AuditEntry(AuditCategories.Data, "portal.link.sent",
            $"Patient portal link sent by {Describe(channels)} to {SharedKernel.Phone.Mask(phone)}; it works for {PortalLink.Validity.Days} days")
        {
            PatientId = patientId,
            EntityType = nameof(Patient),
            EntityId = patientId.ToString(),
            Details = new { linkId = link.Id, channels = ChannelList(channels), expiresAt = link.ExpiresAt },
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new PortalLinkSent(link.Id, ChannelList(channels), link.ExpiresAt, SharedKernel.Phone.Mask(phone), notice);
    }

    /// <summary>
    /// The patient's latest links, with who sent them, and the number a new one would go to. Staff who may send one are
    /// told why none can be sent now; others are not told (it would say that the record is in a protected category).
    /// </summary>
    public async Task<Result<PatientPortalLinksDto>> ListAsync(Guid patientId, CancellationToken ct)
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

        var links = await db.Set<PortalLink>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(l => l.PatientId == patientId)
            .OrderByDescending(l => l.CreatedAt)
            .Take(Shown)
            .ToListAsync(ct);
        var senders = links.Select(l => l.CreatedBy).OfType<Guid>().Distinct().ToList();
        var names = await db.Set<User>().IgnoreQueryFilters().AsNoTracking().Where(u => senders.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        PortalLinkRefusal? cannotSend = null;
        if (currentUser.UserId is { } userId && await permissions.HasPermissionAsync(userId, PatientsPermissions.Register, ct))
        {
            var refusal = await BaseUrlAsync(currentUser.FacilityId ?? patient.RegisteredFacilityId, ct) is null
                ? PortalLinkRules.NoAddress
                : PortalLinkRules.WhyNotSend(patient);
            cannotSend = refusal is null ? null : new PortalLinkRefusal(refusal.Code, refusal.Message);
        }

        var whatsAppStopped = patient.Phone is not null && (await whatsApp.StatusAsync(patient.Phone, ct)).OptedOut;
        await audit.PatientRecordAccessedAsync(patientId, "Viewed the patient portal links sent to the patient", ct);

        var now = time.GetUtcNow();
        return new PatientPortalLinksDto(
            patient.Phone is null ? null : SharedKernel.Phone.Mask(patient.Phone),
            cannotSend,
            whatsAppStopped,
            links.Select(l => new PortalLinkDto(
                    l.Id, l.CreatedAt, l.CreatedBy is { } by ? names.GetValueOrDefault(by) : null, ChannelList(l.Channels), SharedKernel.Phone.Mask(l.Phone),
                    l.ExpiresAt, l.OpenedAt, l.StateAt(now), l.RevokedAt, l.RevokedReason))
                .ToList());
    }

    /// <summary>
    /// Stops the patient's links that still work, when their registered number changes: they went to the old number. Adds
    /// to the caller's unit of work. (Opening a link checks the number too; this keeps the record's list true.)
    /// </summary>
    internal static async Task RevokeLiveLinksAsync(HospitalDbContext db, Guid patientId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var live = await db.Set<PortalLink>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(l => l.PatientId == patientId && l.RevokedAt == null && l.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var link in live)
        {
            link.Revoke(reason, now);
        }
    }

    // ------------------------------------------------------------------ public (the link's page)

    /// <summary>What the link's page shows, or null for anything that is not a link that works now. Changes nothing.</summary>
    public async Task<PublicPortalLinkDto?> OpenAsync(string token, CancellationToken ct)
    {
        var found = await UsableAsync(token, ct);
        if (found is null)
        {
            return null;
        }

        var hospital = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == found.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        return new PublicPortalLinkDto(hospital ?? "Hospital", SharedKernel.Phone.Mask(found.Phone), found.ExpiresAt);
    }

    /// <summary>
    /// Sends a sign-in code to the number the link went to, as the portal's sign-in does (its limit of codes per number
    /// applies), and marks the link opened the first time. Null for anything that is not a link that works now.
    /// </summary>
    public async Task<Result<OtpChallengeResponse>?> RequestCodeAsync(string token, CancellationToken ct)
    {
        var found = await UsableAsync(token, ct);
        if (found is null)
        {
            return null;
        }

        var sent = await auth.RequestOtpAsync(new OtpRequest(found.Phone), ct);
        if (sent.IsFailure || found.OpenedAt is not null)
        {
            return sent;
        }

        var link = await db.Set<PortalLink>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstAsync(l => l.Id == found.Id, ct);
        link.MarkOpened(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>The number a link went to, for the anti-automation check to count a failed request against it.</summary>
    public async Task<string?> PhoneOfAsync(string token, CancellationToken ct)
    {
        if (!PortalLink.IsWellFormedToken(token))
        {
            return null;
        }

        var hash = SecretTokens.Hash(token);
        return await db.Set<PortalLink>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(l => l.TokenHash == hash).Select(l => l.Phone).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The link of <paramref name="token"/> when it works now: live, for a record it may still be used for that still has
    /// its number, and while the portal is on for the facility that sent it. Anything else reads the same: null.
    /// </summary>
    private async Task<PortalLink?> UsableAsync(string token, CancellationToken ct)
    {
        if (!PortalLink.IsWellFormedToken(token))
        {
            return null;
        }

        var hash = SecretTokens.Hash(token);
        var link = await db.Set<PortalLink>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstOrDefaultAsync(l => l.TokenHash == hash, ct);
        var now = time.GetUtcNow();
        if (link is null || !link.IsLive(now))
        {
            return null;
        }

        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == link.PatientId, ct);
        return patient is not null && link.Opens(patient, now) && await features.IsEnabledAsync(FeatureKeys.PatientPortal, link.FacilityId, ct)
            ? link
            : null;
    }

    // ------------------------------------------------------------------ shared

    /// <summary>The portal's public address for the facility while the portal is on there; null otherwise.</summary>
    private async Task<string?> BaseUrlAsync(Guid facilityId, CancellationToken ct) =>
        await features.IsEnabledAsync(FeatureKeys.PatientPortal, facilityId, ct) ? await PortalAddress.BaseUrlAsync(settings, facilityId, ct) : null;

    private static PortalLinkChannels Of(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Sms => PortalLinkChannels.Sms,
        NotificationChannel.WhatsApp => PortalLinkChannels.WhatsApp,
        _ => PortalLinkChannels.None,
    };

    private static List<NotificationChannel> ChannelList(PortalLinkChannels channels)
    {
        var list = new List<NotificationChannel>(2);
        if (channels.HasFlag(PortalLinkChannels.Sms))
        {
            list.Add(NotificationChannel.Sms);
        }

        if (channels.HasFlag(PortalLinkChannels.WhatsApp))
        {
            list.Add(NotificationChannel.WhatsApp);
        }

        return list;
    }

    private static string Describe(PortalLinkChannels channels) => channels switch
    {
        PortalLinkChannels.Sms => "SMS",
        PortalLinkChannels.WhatsApp => "WhatsApp",
        _ => "SMS and WhatsApp",
    };
}
