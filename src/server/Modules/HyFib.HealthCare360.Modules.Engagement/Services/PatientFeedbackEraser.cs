using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Removes a patient's identity from the feedback, complaints and enquiries they gave, when they ask for their data to
/// be erased (DPDP Act 2023 s12(3)). The ratings and the complaints stay in the hospital's quality records (NABH), but
/// nothing in them points to the patient any more: the patient, visit and admission links, contact details and feedback
/// comments go. Called by the Privacy module; the caller saves.
/// </summary>
public sealed class PatientFeedbackEraser(HospitalDbContext db)
{
    public const string ErasedName = "Erased on request";

    /// <summary>How many feedback responses, complaints and enquiries point to the patient.</summary>
    public async Task<int> CountAsync(Guid patientId, CancellationToken ct) =>
        await db.Set<FeedbackResponse>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).CountAsync(f => f.PatientId == patientId, ct)
        + await db.Set<Complaint>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).CountAsync(c => c.PatientId == patientId, ct)
        + await db.Set<Lead>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).CountAsync(l => l.ConvertedPatientId == patientId, ct);

    public async Task<int> EraseAsync(Guid patientId, CancellationToken ct)
    {
        var feedback = await db.Set<FeedbackResponse>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(f => f.PatientId == patientId)
            .ToListAsync(ct);
        foreach (var response in feedback)
        {
            response.PatientId = null;
            response.VisitId = null;
            response.AdmissionId = null;
            response.ContactPhone = null;
            response.Comment = null;
        }

        var feedbackIds = feedback.Select(f => f.Id).ToList();
        var complaints = await db.Set<Complaint>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(c => c.PatientId == patientId || (c.FeedbackResponseId != null && feedbackIds.Contains(c.FeedbackResponseId.Value)))
            .ToListAsync(ct);
        foreach (var complaint in complaints)
        {
            complaint.PatientId = null;
            complaint.ContactName = null;
            complaint.ContactPhone = null;
        }

        var leads = await db.Set<Lead>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(l => l.ConvertedPatientId == patientId)
            .ToListAsync(ct);
        foreach (var lead in leads)
        {
            lead.Name = ErasedName;
            lead.Phone = null;
            lead.Email = null;
            lead.Gender = null;
            lead.AgeYears = null;
            lead.City = null;
            lead.Notes = null;
            lead.ConvertedPatientId = null;
        }

        return feedback.Count + complaints.Count + leads.Count;
    }
}
