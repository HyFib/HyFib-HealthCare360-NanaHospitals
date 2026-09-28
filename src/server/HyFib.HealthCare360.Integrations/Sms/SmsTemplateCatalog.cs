using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Integrations.Sms;

/// <summary>Template bodies registered under a TRAI DLT template id (used to recover MSG91 Flow variables).</summary>
internal interface ISmsTemplateCatalog
{
    /// <summary>Active SMS template bodies with this DLT id, facility-specific ones first.</summary>
    Task<IReadOnlyList<string>> BodiesForDltTemplateAsync(string dltTemplateId, CancellationToken cancellationToken);
}

internal sealed class SmsTemplateCatalog(HospitalDbContext db) : ISmsTemplateCatalog
{
    public async Task<IReadOnlyList<string>> BodiesForDltTemplateAsync(string dltTemplateId, CancellationToken cancellationToken) =>
        await db.Set<NotificationTemplate>().AsNoTracking()
            .Where(t => t.DltTemplateId == dltTemplateId && t.Channel == NotificationChannel.Sms && t.IsActive)
            .OrderByDescending(t => t.FacilityId != null)
            .Select(t => t.Body)
            .Take(10)
            .ToListAsync(cancellationToken);
}
