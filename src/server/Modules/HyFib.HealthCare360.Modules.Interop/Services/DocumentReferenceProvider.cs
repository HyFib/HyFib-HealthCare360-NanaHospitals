using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Files;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>DocumentReference: clinical documents attached to the patient record (Patients) and signed discharge summaries (Clinical).</summary>
internal sealed class DocumentReferenceProvider(HospitalDbContext db, ReferenceDirectoryLoader references) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.DocumentReference;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (FhirIds.TryParse(id, FhirIds.PatientDocument, out var documentId))
        {
            var state = await db.Set<PatientDocument>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(d => d.Id == documentId && d.Type != PatientDocumentType.IdProof && d.Type != PatientDocumentType.InsuranceCard)
                .Select(d => new RowState(d.PatientId, d.IsDeleted)).FirstOrDefaultAsync(ct);
            if (ResourceLookup.Unavailable(state) is { } unavailable)
            {
                return unavailable;
            }

            var documents = await PatientDocuments(d => d.Id == documentId).ToListAsync(ct);
            return documents.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found(DocumentReferenceMapper.FromPatientDocument(documents[0]), state!.PatientId);
        }

        if (FhirIds.TryParse(id, FhirIds.DischargeSummary, out var summaryId))
        {
            var state = await db.Set<DischargeSummary>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(s => s.Id == summaryId).Select(s => new RowState(s.PatientId, s.IsDeleted)).FirstOrDefaultAsync(ct);
            if (ResourceLookup.Unavailable(state) is { } unavailable)
            {
                return unavailable;
            }

            var summaries = await SignedSummaries(s => s.Id == summaryId).ToListAsync(ct);
            return summaries.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found((await MapSummariesAsync(summaries, ct))[0], state!.PatientId);
        }

        return ResourceLookup.Missing;
    }

    public async Task<IReadOnlyList<R4.Resource>> ListForPatientAsync(Guid patientId, CancellationToken ct)
    {
        var documents = await PatientDocuments(d => d.PatientId == patientId).ToListAsync(ct);
        var summaries = await SignedSummaries(s => s.PatientId == patientId).ToListAsync(ct);
        return [.. documents.Select(DocumentReferenceMapper.FromPatientDocument), .. await MapSummariesAsync(summaries, ct)];
    }

    /// <summary>Clinical documents with their stored file's content type and size (identity and insurance documents stay).</summary>
    internal IQueryable<PatientDocumentRow> PatientDocuments(Expression<Func<PatientDocument, bool>> filter) =>
        from d in db.Set<PatientDocument>().AsNoTracking().Where(filter)
        where d.Type != PatientDocumentType.IdProof && d.Type != PatientDocumentType.InsuranceCard
        join f in db.Set<StoredFile>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on d.FileId equals f.Id into files
        from f in files.DefaultIfEmpty()
        select new PatientDocumentRow
        {
            Id = d.Id,
            PatientId = d.PatientId,
            Type = d.Type,
            Title = d.Title,
            DocumentDate = d.DocumentDate,
            Source = d.Source,
            ContentType = f.ContentType,
            SizeBytes = (long?)f.SizeBytes,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
        };

    /// <summary>Signed or amended discharge summaries; drafts are not shared.</summary>
    internal IQueryable<DischargeSummaryRow> SignedSummaries(Expression<Func<DischargeSummary, bool>> filter) =>
        db.Set<DischargeSummary>().AsNoTracking().Where(filter).Where(s => s.Status != DischargeSummaryStatus.Draft).Select(s => new DischargeSummaryRow
        {
            Id = s.Id,
            PatientId = s.PatientId,
            AdmissionId = s.AdmissionId,
            EncounterId = s.EncounterId,
            PractitionerId = s.PractitionerId,
            SignedByPractitionerId = s.SignedByPractitionerId,
            AdmittedAt = s.AdmittedAt,
            DischargedAt = s.DischargedAt,
            Diagnoses = s.Diagnoses,
            DiagnosisCodes = s.DiagnosisCodes,
            PresentingComplaints = s.PresentingComplaints,
            CourseInHospital = s.CourseInHospital,
            Procedures = s.Procedures,
            InvestigationsSummary = s.InvestigationsSummary,
            ConditionAtDischarge = s.ConditionAtDischarge,
            Medications = s.Medications,
            Advice = s.Advice,
            FollowUpDate = s.FollowUpDate,
            FollowUpInstructions = s.FollowUpInstructions,
            Status = s.Status,
            SignedAt = s.SignedAt,
            AmendedAt = s.AmendedAt,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
        });

    private async Task<List<R4.Resource>> MapSummariesAsync(IReadOnlyList<DischargeSummaryRow> summaries, CancellationToken ct)
    {
        if (summaries.Count == 0)
        {
            return [];
        }

        var ids = new ReferenceIds();
        foreach (var summary in summaries)
        {
            ids.Practitioner(summary.PractitionerId, summary.SignedByPractitionerId).Admission(summary.AdmissionId);
        }

        var refs = await references.LoadAsync(ids, ct);
        return [.. summaries.Select(s => (R4.Resource)DocumentReferenceMapper.FromDischargeSummary(s, refs))];
    }
}
