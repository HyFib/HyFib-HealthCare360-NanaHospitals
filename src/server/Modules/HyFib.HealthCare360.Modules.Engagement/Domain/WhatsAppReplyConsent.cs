using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Domain;

/// <summary>
/// What a STOP or START sent on WhatsApp from a patient's mobile number changes in the patient's marketing consent
/// (DPDP Act 2023: withdrawing consent must be as easy as giving it). Only real changes are recorded, dated when the
/// reply was sent:
/// <list type="bullet">
/// <item>STOP withdraws an active marketing consent: every grant is revoked and the refusal is recorded.</item>
/// <item>START gives marketing consent back only when the patient's latest marketing record is the refusal a STOP
/// recorded. It never creates consent the patient had not given, and never overrides a choice made another way
/// (portal, staff, the opt-out link in a campaign message).</item>
/// </list>
/// A choice recorded after the reply was sent wins over it, so late or repeated webhooks change nothing.
/// </summary>
public static class WhatsAppReplyConsent
{
    public const string StopNotes = "Withdrawn via WhatsApp STOP reply";
    public const string StartNotes = "Given again via WhatsApp START reply";

    /// <summary>The patients a reply from <paramref name="phone"/> (E.164) applies to: everyone registered with it, as families share numbers.</summary>
    public static Expression<Func<Patient, bool>> RegisteredWith(string phone) => p => p.Phone == phone && p.MergedIntoPatientId == null;

    /// <summary>
    /// Applies a STOP (<paramref name="optedOut"/>) or START to one patient's marketing-consent history, revoking grants
    /// in place, and returns the consent record to add, or null when nothing changes.
    /// </summary>
    public static PatientConsent? Apply(
        Guid patientId, IReadOnlyCollection<PatientConsent> marketingHistory, bool optedOut, DateTimeOffset repliedAt, Guid? capturedByUserId)
    {
        if (marketingHistory.Any(c => c.CapturedAt > repliedAt))
        {
            return null;
        }

        if (optedOut)
        {
            if (!marketingHistory.Any(c => IsActive(c, marketingHistory)))
            {
                return null;
            }

            foreach (var grant in marketingHistory.Where(c => c.Granted && c.RevokedAt is null))
            {
                grant.Revoke(StopNotes, capturedByUserId, repliedAt);
            }

            return Record(patientId, granted: false, repliedAt, StopNotes, capturedByUserId);
        }

        var latest = marketingHistory.MaxBy(c => c.CapturedAt);
        return latest is { Granted: false, CapturedByUserId: null, Notes: StopNotes }
            ? Record(patientId, granted: true, repliedAt, StartNotes, capturedByUserId)
            : null;
    }

    /// <summary>Granted, not revoked, and not superseded by a later refusal (as for the campaign audience).</summary>
    private static bool IsActive(PatientConsent consent, IEnumerable<PatientConsent> history) =>
        consent is { Granted: true, RevokedAt: null } && !history.Any(d => !d.Granted && d.CapturedAt > consent.CapturedAt);

    private static PatientConsent Record(Guid patientId, bool granted, DateTimeOffset at, string notes, Guid? capturedByUserId) => new()
    {
        PatientId = patientId,
        Type = ConsentType.MarketingCommunication,
        Granted = granted,
        CapturedAt = at,
        CapturedByUserId = capturedByUserId,
        Notes = notes,
    };
}
