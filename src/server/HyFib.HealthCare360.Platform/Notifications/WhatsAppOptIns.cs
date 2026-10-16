using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Notifications;

/// <summary>Where a WhatsApp opt-in was given.</summary>
public enum WhatsAppOptInSource
{
    /// <summary>Recorded by staff at registration or from the patient's consents, on what the patient said.</summary>
    RegistrationDesk = 0,

    /// <summary>The patient switched WhatsApp messages on in the patient portal, signed in with this number.</summary>
    Portal,

    /// <summary>The number replied START to the hospital's WhatsApp number.</summary>
    WhatsAppStart,
}

/// <summary>
/// A mobile number's agreement to get WhatsApp messages from the hospital (INT-WA opt-in tracking): who gave it, where
/// and when. Rows are only ever added, one for each time the agreement is given, so the history stays complete. The
/// number's latest STOP (or the portal's "off") is in <see cref="WhatsAppOptOut"/> and overrides every opt-in until the
/// number opts in again in a way that lifts it (<see cref="WhatsAppConsent"/>).
/// </summary>
public sealed class WhatsAppOptIn : AuditableEntity
{
    /// <summary>The number, in E.164 (<c>Phone.Normalize</c>).</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>The patient it was recorded for, when known (family members can share a number).</summary>
    public Guid? PatientId { get; set; }

    public WhatsAppOptInSource Source { get; set; }

    public DateTimeOffset OptedInAt { get; set; }

    /// <summary>The member of staff who recorded it at the desk, or the portal account; null for a START reply.</summary>
    public Guid? CapturedByUserId { get; set; }

    public Guid? FacilityId { get; set; }

    /// <summary>The WhatsApp message id (wamid) of a START reply.</summary>
    public string? ProviderMessageId { get; set; }
}

internal sealed class WhatsAppOptInConfiguration : IEntityTypeConfiguration<WhatsAppOptIn>
{
    public void Configure(EntityTypeBuilder<WhatsAppOptIn> b)
    {
        b.ToTable("whatsapp_opt_ins", "platform");
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.ProviderMessageId).HasMaxLength(128);

        // Every WhatsApp reminder and campaign message looks its number up.
        b.HasIndex(x => new { x.Phone, x.OptedInAt });
        b.HasIndex(x => x.PatientId);
    }
}

/// <summary>A number's WhatsApp choices as staff and the portal show them.</summary>
/// <param name="OptedIn">The number has opted in at least once (it still needs <paramref name="OptedOut"/> false to get messages).</param>
/// <param name="OptedOut">A STOP reply, or the portal's "off", is in force.</param>
public sealed record WhatsAppConsentStatus(
    bool OptedIn,
    DateTimeOffset? OptedInAt,
    WhatsAppOptInSource? Source,
    Guid? CapturedByUserId,
    bool OptedOut,
    DateTimeOffset? OptedOutAt)
{
    public static readonly WhatsAppConsentStatus None = new(false, null, null, null, false, null);

    /// <summary>Reminders and campaigns may go to the number on WhatsApp (see <see cref="WhatsAppOptOutPolicy.NeedsOptIn"/>).</summary>
    public bool Reachable => OptedIn && !OptedOut;
}

/// <summary>
/// Records and reads a number's WhatsApp opt-in and opt-out. The rules:
/// <list type="bullet">
/// <item>An opt-in from the desk, the portal or a START reply is always recorded, with its source and who captured it.</item>
/// <item>Only the number's holder can lift a STOP: a START reply, or the portal signed in with that number. A desk
/// opt-in is recorded but leaves the STOP in force, because families share numbers and the person at the desk may not
/// be the one who replied STOP.</item>
/// <item>The portal's "off" is recorded as an opt-out, exactly as a STOP would be.</item>
/// </list>
/// Adds to the caller's unit of work; the caller saves.
/// </summary>
public sealed class WhatsAppConsent(HospitalDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<WhatsAppConsentStatus> StatusAsync(string? phone, CancellationToken cancellationToken)
    {
        if (Phone.Normalize(phone) is not { } number)
        {
            return WhatsAppConsentStatus.None;
        }

        var latest = await db.Set<WhatsAppOptIn>().AsNoTracking()
            .Where(o => o.Phone == number)
            .OrderByDescending(o => o.OptedInAt)
            .Select(o => new { o.OptedInAt, o.Source, o.CapturedByUserId })
            .FirstOrDefaultAsync(cancellationToken);
        var optOut = await db.Set<WhatsAppOptOut>().AsNoTracking()
            .Where(WhatsAppOptOutPolicy.InForceFor(number))
            .Select(o => new { o.ChangedAt })
            .FirstOrDefaultAsync(cancellationToken);
        return new WhatsAppConsentStatus(latest is not null, latest?.OptedInAt, latest?.Source, latest?.CapturedByUserId, optOut is not null, optOut?.ChangedAt);
    }

    /// <summary>
    /// Records an opt-in given at the desk or in the portal. Returns false when there is no valid mobile number. A portal
    /// opt-in from the number's own account also lifts a STOP (<paramref name="byNumberHolder"/>).
    /// </summary>
    public async Task<bool> OptInAsync(
        string? phone, Guid? patientId, WhatsAppOptInSource source, Guid? facilityId, bool byNumberHolder, CancellationToken cancellationToken)
    {
        if (Phone.Normalize(phone) is not { } number)
        {
            return false;
        }

        var now = time.GetUtcNow();
        db.Add(new WhatsAppOptIn
        {
            Phone = number,
            PatientId = patientId,
            Source = source,
            OptedInAt = now,
            CapturedByUserId = currentUser.UserId,
            FacilityId = facilityId,
        });

        if (byNumberHolder && source != WhatsAppOptInSource.RegistrationDesk)
        {
            (await OptOutRowAsync(number, cancellationToken)).Apply(optedOut: false, now, null, facilityId);
        }

        return true;
    }

    /// <summary>The number's holder switched WhatsApp off in the portal: recorded as an opt-out, as a STOP reply would be.</summary>
    public async Task<bool> OptOutAsync(string? phone, Guid? facilityId, CancellationToken cancellationToken)
    {
        if (Phone.Normalize(phone) is not { } number)
        {
            return false;
        }

        (await OptOutRowAsync(number, cancellationToken)).Apply(optedOut: true, time.GetUtcNow(), null, facilityId);
        return true;
    }

    private async Task<WhatsAppOptOut> OptOutRowAsync(string number, CancellationToken cancellationToken)
    {
        // Local first: a row added earlier in the same save is not in the database yet.
        var row = db.Set<WhatsAppOptOut>().Local.FirstOrDefault(o => o.Phone == number)
                  ?? await db.Set<WhatsAppOptOut>().FirstOrDefaultAsync(o => o.Phone == number, cancellationToken);
        if (row is null)
        {
            row = new WhatsAppOptOut { Phone = number };
            db.Add(row);
        }

        return row;
    }
}
