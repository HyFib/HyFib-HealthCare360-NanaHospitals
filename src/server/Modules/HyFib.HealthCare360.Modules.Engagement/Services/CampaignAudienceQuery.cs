using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Builds the audience of a marketing campaign (CRM-03). The DPDP Act 2023 allows marketing only with consent, so the
/// consent filter is not optional and not part of the segment: a patient qualifies only when they hold a granted,
/// unrevoked <see cref="ConsentType.MarketingCommunication"/> consent that no later refusal supersedes. Deceased,
/// inactive and merged records and patients without a mobile number are never included. A WhatsApp campaign also needs
/// the patient's number to have opted in to WhatsApp, with no STOP in force (<see cref="ReachableOnWhatsApp"/>). Written against plain
/// <see cref="IQueryable{T}"/> (no provider-specific functions) so the same rules are unit-tested in memory and
/// translated to SQL in production.
/// </summary>
public static class CampaignAudienceQuery
{
    /// <summary>IST has no daylight saving; visit dates in segments are local calendar dates.</summary>
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    public static IQueryable<Patient> Build(
        IQueryable<Patient> patients,
        IQueryable<PatientConsent> consents,
        IQueryable<Visit> visits,
        CampaignSegment segment,
        Guid facilityId,
        DateOnly today)
    {
        var audience = patients
            .Where(p => p.IsActive && !p.IsDeceased && p.MergedIntoPatientId == null && p.Phone != null)
            .Where(HasActiveMarketingConsent(consents))
            .Where(p => p.RegisteredFacilityId == facilityId || visits.Any(v => v.PatientId == p.Id && v.FacilityId == facilityId));

        if (segment.MinAge is { } minAge)
        {
            var bornOnOrBefore = today.AddYears(-minAge);
            audience = audience.Where(p => p.DateOfBirth != null && p.DateOfBirth <= bornOnOrBefore);
        }

        if (segment.MaxAge is { } maxAge)
        {
            var bornAfter = today.AddYears(-(maxAge + 1));
            audience = audience.Where(p => p.DateOfBirth != null && p.DateOfBirth > bornAfter);
        }

        if (segment.Gender is { } gender)
        {
            audience = audience.Where(p => p.Gender == gender);
        }

        if (!string.IsNullOrWhiteSpace(segment.City))
        {
            var city = segment.City.Trim().ToLowerInvariant();
            audience = audience.Where(p => p.Address.City != null && p.Address.City.ToLower() == city);
        }

        var facilityVisits = visits.Where(v => v.FacilityId == facilityId && v.Status != VisitStatus.Cancelled);
        if (segment.LastVisitFrom is not null || segment.LastVisitTo is not null)
        {
            var from = segment.LastVisitFrom is { } f ? StartOfDay(f) : DateTimeOffset.MinValue;
            var to = segment.LastVisitTo is { } t ? StartOfDay(t.AddDays(1)) : DateTimeOffset.MaxValue;
            audience = audience.Where(p => facilityVisits.Where(v => v.PatientId == p.Id).Max(v => (DateTimeOffset?)v.VisitedAt) >= from
                                           && facilityVisits.Where(v => v.PatientId == p.Id).Max(v => (DateTimeOffset?)v.VisitedAt) < to);
        }

        if (segment.DepartmentIds.Count > 0)
        {
            var departments = segment.DepartmentIds.ToList();
            audience = audience.Where(p => facilityVisits.Any(v => v.PatientId == p.Id && v.DepartmentId != null && departments.Contains(v.DepartmentId.Value)));
        }

        return audience;
    }

    /// <summary>
    /// Patients whose mobile number may get campaigns on WhatsApp (INT-WA): it opted in (desk, portal or START) and has no
    /// STOP in force. The number is the patient's registered one, in E.164 as the opt-in records it.
    /// </summary>
    public static IQueryable<Patient> ReachableOnWhatsApp(IQueryable<Patient> patients, IQueryable<WhatsAppOptIn> optIns, IQueryable<WhatsAppOptOut> optOuts) =>
        patients.Where(p => optIns.Any(o => o.Phone == p.Phone) && !optOuts.Any(o => o.Phone == p.Phone && o.OptedOut));

    /// <summary>Granted, not revoked, and not superseded by a later recorded refusal.</summary>
    public static Expression<Func<Patient, bool>> HasActiveMarketingConsent(IQueryable<PatientConsent> consents) =>
        p => consents.Any(c => c.PatientId == p.Id && c.Type == ConsentType.MarketingCommunication && c.Granted && c.RevokedAt == null
                               && !consents.Any(d => d.PatientId == p.Id && d.Type == ConsentType.MarketingCommunication && !d.Granted
                                                     && d.CapturedAt > c.CapturedAt));

    private static DateTimeOffset StartOfDay(DateOnly date) => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), Ist).ToUniversalTime();
}
