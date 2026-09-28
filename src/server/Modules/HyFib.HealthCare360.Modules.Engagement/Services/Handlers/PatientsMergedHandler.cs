using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Engagement.Services.Handlers;

/// <summary>
/// Re-points the CRM and feedback history of a merged (duplicate) patient to the surviving record. It runs in every
/// facility and includes soft-deleted rows, inside the merge transaction, so the 360° view and follow-up worklists
/// follow the patient. A patient receives each campaign once: where the survivor already has a row for a campaign,
/// the duplicate's row is kept as history.
/// </summary>
internal sealed class PatientsMergedHandler(HospitalDbContext db) : IDomainEventHandler<PatientsMerged>
{
    public async Task HandleAsync(PatientsMerged merged, CancellationToken cancellationToken)
    {
        var from = merged.SourcePatientId;
        var to = merged.TargetPatientId;
        await RepointAsync<FollowUpTask>(t => t.PatientId == from, t => t.PatientId = to, cancellationToken);
        await RepointAsync<FeedbackResponse>(r => r.PatientId == from, r => r.PatientId = to, cancellationToken);
        await RepointAsync<FeedbackInvitation>(i => i.PatientId == from, i => i.PatientId = to, cancellationToken);
        await RepointAsync<Complaint>(c => c.PatientId == from, c => c.PatientId = to, cancellationToken);
        await RepointAsync<PatientSpendEntry>(e => e.PatientId == from, e => e.PatientId = to, cancellationToken);
        await RepointAsync<Lead>(l => l.ConvertedPatientId == from, l => l.ConvertedPatientId = to, cancellationToken);

        var survivorCampaigns = db.Set<CampaignRecipient>().Where(r => r.PatientId == to).Select(r => r.CampaignId);
        await RepointAsync<CampaignRecipient>(r => r.PatientId == from && !survivorCampaigns.Contains(r.CampaignId), r => r.PatientId = to, cancellationToken);
    }

    private async Task RepointAsync<T>(Expression<Func<T, bool>> ofSource, Action<T> repoint, CancellationToken ct)
        where T : class
    {
        foreach (var row in await db.Set<T>().IgnoreQueryFilters().Where(ofSource).ToListAsync(ct))
        {
            repoint(row);
        }
    }
}
