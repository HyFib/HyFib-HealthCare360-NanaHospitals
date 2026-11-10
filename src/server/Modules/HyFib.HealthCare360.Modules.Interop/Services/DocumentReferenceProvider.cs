using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Identity;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// DocumentReference: clinical documents attached to the patient record (Patients), signed discharge summaries and signed
/// clinical notes (Clinical).
/// </summary>
internal sealed class DocumentReferenceProvider(HospitalDbContext db, ReferenceDirectoryLoader references) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.DocumentReference;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (FhirIds.TryParse(id, FhirIds.PatientDocument, out var documentId))
        {
            var document = await db.Set<PatientDocument>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(d => d.Id == documentId && d.Type != PatientDocumentType.IdProof && d.Type != PatientDocumentType.InsuranceCard)
                .Select(d => new { State = new RowState(d.PatientId, d.IsDeleted), d.FileId }).FirstOrDefaultAsync(ct);
            var state = document is not null && (await AtReadersFacilityAsync([document.FileId], ct)).Count == 1 ? document.State : null;
            if (ResourceLookup.Unavailable(state) is { } unavailable)
            {
                return unavailable;
            }

            var documents = await ReadersDocumentsAsync(d => d.Id == documentId, ct);
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

        if (FhirIds.TryParse(id, FhirIds.ClinicalNote, out var noteId))
        {
            var state = await db.Set<ClinicalNote>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(n => n.Id == noteId).Select(n => new RowState(n.PatientId, n.IsDeleted)).FirstOrDefaultAsync(ct);
            if (ResourceLookup.Unavailable(state) is { } unavailable)
            {
                return unavailable;
            }

            // A draft is answered as no note at all.
            var notes = await SignedNotesAsync(n => n.Id == noteId, ct);
            return notes.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found((await MapNotesAsync(notes, ct))[0], state!.PatientId);
        }

        return ResourceLookup.Missing;
    }

    public async Task<SearchCandidates> ListForPatientAsync(Guid patientId, SearchRequest request, PatientAccess access, CancellationToken ct)
    {
        var documents = await ReadersDocumentsAsync(d => d.PatientId == patientId, ct);
        var summaries = await SignedSummaries(s => s.PatientId == patientId).ToListAsync(ct);
        var notes = await SignedNotesAsync(n => n.PatientId == patientId, ct);
        return new SearchCandidates([.. documents.Select(DocumentReferenceMapper.FromPatientDocument), .. await MapSummariesAsync(summaries, ct), .. await MapNotesAsync(notes, ct)]);
    }

    /// <summary>
    /// Signed clinical notes, addenda included; a draft is never shared. The note's encounter, stay and accounts are
    /// checked and named by <see cref="SignedNotesAsync"/>.
    /// </summary>
    internal IQueryable<ClinicalNoteRow> SignedNotes(Expression<Func<ClinicalNote, bool>> filter) =>
        Rows(db.Set<ClinicalNote>().AsNoTracking().Where(filter));

    /// <summary>
    /// The signed notes, each with its encounter and stay kept only when they are the patient's own (in whichever
    /// facility), so a note filed against someone else's never points another provider at it, and its author and signer
    /// named from their accounts, a removed account too (a nurse is not a practitioner), the signer's practitioner looked
    /// up. Those are read apart: a query that ignores a filter ignores it for every table it reads, so ignoring the
    /// facility filter for the encounter in the notes' query would read other facilities' notes, and ignoring the
    /// soft-delete filter for the accounts would read removed notes.
    /// </summary>
    internal async Task<List<ClinicalNoteRow>> SignedNotesAsync(Expression<Func<ClinicalNote, bool>> filter, CancellationToken ct) =>
        await CheckedAsync(await SignedNotes(filter).ToListAsync(ct), ct);

    /// <summary>
    /// The signed addenda to any of <paramref name="noteIds"/>, checked and named as <see cref="SignedNotesAsync"/> does,
    /// from every facility: an addendum is part of the note it adds to wherever it was written, as the record staff see
    /// lists the patient's notes from every facility and adds to them. Only the notes are read from every facility; their
    /// encounters, stays and accounts are read apart, as always.
    /// </summary>
    internal async Task<List<ClinicalNoteRow>> SignedAddendaAsync(Guid[] noteIds, CancellationToken ct) =>
        await CheckedAsync(
            await Rows(db.Set<ClinicalNote>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(n => n.AmendsNoteId != null && noteIds.Contains(n.AmendsNoteId.Value))).ToListAsync(ct),
            ct);

    private static IQueryable<ClinicalNoteRow> Rows(IQueryable<ClinicalNote> notes) =>
        notes.Where(n => n.IsSigned && n.SignedAt != null)
            .Select(n => new ClinicalNoteRow
            {
                Id = n.Id,
                PatientId = n.PatientId,
                FacilityId = n.FacilityId,
                EncounterId = n.EncounterId,
                AdmissionId = n.AdmissionId,
                Type = n.Type,
                Title = n.Title,
                Body = n.Body,
                AuthorPractitionerId = n.AuthorPractitionerId,
                AuthorUserId = n.AuthorUserId,
                SignedByUserId = n.SignedByUserId,
                AmendsNoteId = n.AmendsNoteId,
                SignedAt = n.SignedAt!.Value,
                CreatedAt = n.CreatedAt,
                UpdatedAt = n.UpdatedAt,
            });

    /// <summary>The notes with their encounters, stays and accounts checked and named (<see cref="SignedNotesAsync"/>).</summary>
    private async Task<List<ClinicalNoteRow>> CheckedAsync(List<ClinicalNoteRow> notes, CancellationToken ct)
    {
        if (notes.Count == 0)
        {
            return notes;
        }

        Guid[] encounterIds = [.. notes.Select(n => n.EncounterId).OfType<Guid>().Distinct()];
        Guid[] admissionIds = [.. notes.Select(n => n.AdmissionId).OfType<Guid>().Distinct()];
        Guid[] userIds = [.. notes.SelectMany(n => new[] { n.AuthorUserId, n.SignedByUserId }).OfType<Guid>().Distinct()];
        var encounters = encounterIds.Length == 0
            ? []
            : await db.Set<Encounter>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(e => encounterIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.PatientId, ct);
        var admissions = admissionIds.Length == 0
            ? []
            : await db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(a => admissionIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.PatientId, ct);
        var users = userIds.Length == 0
            ? []
            : await db.Set<User>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => new NoteAccount(u.DisplayName, u.PractitionerId), ct);
        return
        [
            .. notes.Select(n => n with
            {
                EncounterId = n.EncounterId is { } encounter && encounters.GetValueOrDefault(encounter) == n.PatientId ? encounter : null,
                AdmissionId = n.AdmissionId is { } admission && admissions.GetValueOrDefault(admission) == n.PatientId ? admission : null,
                AuthorUserName = Account(users, n.AuthorUserId)?.Name,
                SignedByUserName = Account(users, n.SignedByUserId)?.Name,
                SignedByPractitionerId = Account(users, n.SignedByUserId)?.PractitionerId,
            }),
        ];
    }

    private static NoteAccount? Account(Dictionary<Guid, NoteAccount> users, Guid? userId) =>
        userId is { } id ? users.GetValueOrDefault(id) : null;

    /// <summary>
    /// Clinical documents (identity and insurance documents stay); the stored file's content type and size are added by
    /// <see cref="PatientDocumentsAsync"/>.
    /// </summary>
    internal IQueryable<PatientDocumentRow> PatientDocuments(Expression<Func<PatientDocument, bool>> filter) =>
        db.Set<PatientDocument>().AsNoTracking().Where(filter)
            .Where(d => d.Type != PatientDocumentType.IdProof && d.Type != PatientDocumentType.InsuranceCard)
            .Select(d => new PatientDocumentRow
            {
                Id = d.Id,
                PatientId = d.PatientId,
                Type = d.Type,
                Title = d.Title,
                DocumentDate = d.DocumentDate,
                Source = d.Source,
                FileId = d.FileId,
                CreatedAt = d.CreatedAt,
                UpdatedAt = d.UpdatedAt,
            });

    /// <summary>
    /// The documents with their stored file's content type and size, a file since removed included. The files are read
    /// apart: a query that ignores a filter ignores it for every table it reads, so ignoring the soft-delete filter for
    /// the files in the documents' query would read removed documents too.
    /// </summary>
    internal async Task<List<PatientDocumentRow>> PatientDocumentsAsync(Expression<Func<PatientDocument, bool>> filter, CancellationToken ct)
    {
        var documents = await PatientDocuments(filter).ToListAsync(ct);
        Guid[] fileIds = [.. documents.Select(d => d.FileId).Distinct()];
        if (fileIds.Length == 0)
        {
            return documents;
        }

        var files = await db.Set<StoredFile>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(f => fileIds.Contains(f.Id)).Select(f => new { f.Id, f.ContentType, f.SizeBytes }).ToDictionaryAsync(f => f.Id, ct);
        return [.. documents.Select(d => files.TryGetValue(d.FileId, out var file) ? d with { ContentType = file.ContentType, SizeBytes = file.SizeBytes } : d)];
    }

    /// <summary>
    /// The documents uploaded at the reader's facility (<see cref="ReaderFacility"/>): a document is the facility's whose
    /// staff stored its file. Discharge summaries and clinical notes are their facility's by the platform's filter.
    /// </summary>
    private async Task<List<PatientDocumentRow>> ReadersDocumentsAsync(Expression<Func<PatientDocument, bool>> filter, CancellationToken ct)
    {
        var documents = await PatientDocumentsAsync(filter, ct);
        var here = await AtReadersFacilityAsync([.. documents.Select(d => d.FileId)], ct);
        return [.. documents.Where(d => here.Contains(d.FileId))];
    }

    /// <summary>
    /// Which of these stored files were uploaded at the reader's facility, read in a query of its own: a file is read past
    /// its soft-delete filter (as <see cref="PatientDocumentsAsync"/> reads it), which must not lift the documents' filters.
    /// </summary>
    private async Task<HashSet<Guid>> AtReadersFacilityAsync(Guid[] fileIds, CancellationToken ct)
    {
        if (fileIds.Length == 0)
        {
            return [];
        }

        var facility = ReaderFacility.Of(db);
        var here = await db.Set<StoredFile>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(f => fileIds.Contains(f.Id) && f.FacilityId == facility).Select(f => f.Id).ToListAsync(ct);
        return [.. here];
    }

    /// <summary>Signed or amended discharge summaries; drafts are not shared.</summary>
    internal IQueryable<DischargeSummaryRow> SignedSummaries(Expression<Func<DischargeSummary, bool>> filter) =>
        SummaryRows(db.Set<DischargeSummary>().AsNoTracking().Where(filter).Where(s => s.Status != DischargeSummaryStatus.Draft));

    /// <summary>Discharge summaries as rows, whatever their status (the ABDM export tells a draft from a missing summary).</summary>
    internal static IQueryable<DischargeSummaryRow> SummaryRows(IQueryable<DischargeSummary> summaries) =>
        summaries.Select(s => new DischargeSummaryRow
        {
            Id = s.Id,
            PatientId = s.PatientId,
            FacilityId = s.FacilityId,
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

    private async Task<List<R4.Resource>> MapNotesAsync(IReadOnlyList<ClinicalNoteRow> notes, CancellationToken ct)
    {
        if (notes.Count == 0)
        {
            return [];
        }

        var ids = new ReferenceIds();
        foreach (var note in notes)
        {
            ids.Practitioner(note.AuthorPractitionerId, note.SignedByPractitionerId).Facility(note.FacilityId).Admission(note.AdmissionId);
        }

        var refs = await references.LoadAsync(ids, ct);
        return [.. notes.Select(n => (R4.Resource)DocumentReferenceMapper.FromClinicalNote(n, refs))];
    }

    /// <summary>An account as a note names it.</summary>
    private sealed record NoteAccount(string? Name, Guid? PractitionerId);
}
