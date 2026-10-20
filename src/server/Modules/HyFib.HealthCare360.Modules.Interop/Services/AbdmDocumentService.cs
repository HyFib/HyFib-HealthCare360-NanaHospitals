using System.Globalization;
using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>An ABDM health record ready to download: the FHIR document and the file name to save it under.</summary>
internal sealed record AbdmDocument(R4.Bundle Bundle, string FileName);

/// <summary>A coded diagnosis a doctor recorded in one of a stay's encounters.</summary>
internal sealed record StayDiagnosisRow
{
    public required string Code { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public DiagnosisStatus Status { get; init; }
    public DateOnly? OnsetDate { get; init; }
    public DateOnly? ResolvedOn { get; init; }
}

/// <summary>
/// ABDM health records (EHR-08): a signed discharge summary (<see cref="DischargeSummaryRecordMapper"/>) or a signed
/// prescription (<see cref="PrescriptionRecordMapper"/>) as an NRCeS document bundle. A record leaves the hospital under
/// the FHIR API's sharing rules (<see cref="PatientSharingService"/>): the patient exists and was not deleted or merged,
/// holds an active record-sharing consent, and, for a restricted record, the caller may open it. Staff export the records
/// of the facility they are signed in to, as the FHIR API shares a facility's own records. Every export is audited, and
/// so is every refusal for consent or restriction (<see cref="AbdmAuditTrail"/>).
/// </summary>
internal sealed class AbdmDocumentService(
    HospitalDbContext db,
    PatientSharingService sharing,
    PatientAccessGuard guard,
    PatientRecords patients,
    ReferenceDirectoryLoader references,
    PrescriptionFinalCopies finalCopies,
    AbdmAuditTrail audit,
    TimeProvider time)
{
    public async Task<Result<AbdmDocument>> DischargeSummaryAsync(Guid id, CancellationToken ct)
    {
        var record = AbdmRecord.DischargeSummary(id);
        var summary = await Summaries(s => s.Id == id).FirstOrDefaultAsync(ct);
        if (summary is null)
        {
            return await MissingAsync<DischargeSummary>(id, "discharge summary", ct);
        }

        if (await RefuseAsync(record, summary.PatientId, ct) is { } refused)
        {
            return refused;
        }

        if (summary.Status == DischargeSummaryStatus.Draft)
        {
            return Error.Rule("abdm.not_signed", "Only a signed discharge summary can be shared as an ABDM health record. Sign it first.");
        }

        var source = await SummarySourceAsync(summary, ct);
        if (source.IsFailure)
        {
            return source.Error!;
        }

        var now = time.GetUtcNow();
        var documentId = Guid.CreateVersion7(now);
        var bundle = DischargeSummaryRecordMapper.Map(source.Value, documentId, now);
        await audit.ExportedAsync(record, summary.PatientId, AbdmProfiles.DischargeSummaryRecord, DocumentBundleBuilder.FullUrl(documentId), bundle.Entry.Count, ct);
        return new AbdmDocument(bundle, FileName(summary, source.Value.Patient.Uhid));
    }

    public async Task<Result<AbdmDocument>> PrescriptionAsync(Guid id, CancellationToken ct)
    {
        var prescription = await Prescriptions(p => p.Id == id).FirstOrDefaultAsync(ct);
        if (prescription is null)
        {
            return await MissingAsync<Prescription>(id, "prescription", ct);
        }

        var record = AbdmRecord.Prescription(id, prescription.PrescriptionNumber);
        if (await RefuseAsync(record, prescription.PatientId, ct) is { } refused)
        {
            return refused;
        }

        switch (prescription.Status)
        {
            case PrescriptionStatus.Draft:
                return Error.Rule("abdm.not_signed", "Only a signed prescription can be shared as an ABDM health record. Sign it first.");
            case PrescriptionStatus.Cancelled:
                return Error.Rule("abdm.cancelled", "This prescription was cancelled, so it is not shared as a health record.");
        }

        // The health record is one more copy of the prescription the hospital issues: where the hospital requires the
        // doctor's DSC, no copy goes out without a valid one (the printout's and the PDF's rule, with their codes).
        var issued = await db.Set<Prescription>().AsNoTracking().Include(p => p.Items).FirstAsync(p => p.Id == id, ct);
        if (PrescriptionFinalCopies.Problem(await finalCopies.CheckAsync(issued, ct)) is { } notIssued)
        {
            return notIssued;
        }

        var source = await PrescriptionSourceAsync(prescription, ct);
        if (source.IsFailure)
        {
            return source.Error!;
        }

        var now = time.GetUtcNow();
        var documentId = Guid.CreateVersion7(now);
        var bundle = PrescriptionRecordMapper.Map(source.Value, documentId, now);
        await audit.ExportedAsync(record, prescription.PatientId, AbdmProfiles.PrescriptionRecord, DocumentBundleBuilder.FullUrl(documentId), bundle.Entry.Count, ct);
        return new AbdmDocument(bundle, FileName(prescription));
    }

    /// <summary>
    /// A record the user's facility does not hold: 422 when another facility holds it (so the user knows where to sign
    /// in), 404 when there is no such record.
    /// </summary>
    private async Task<Error> MissingAsync<T>(Guid id, string name, CancellationToken ct) where T : Entity =>
        await db.Set<T>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(x => x.Id == id, ct)
            ? Error.Rule("abdm.other_facility", $"This {name} was written at another facility. Sign in to that facility to export it.")
            : Error.NotFound(typeof(T).Name, id);

    /// <summary>
    /// The FHIR API's sharing rules for the patient, worded for staff: null when the record may be shared. Refusals for
    /// consent and restriction are audited as security events.
    /// </summary>
    private async Task<Error?> RefuseAsync(AbdmRecord record, Guid patientId, CancellationToken ct)
    {
        var access = await sharing.EvaluateAsync(patientId, ct);
        switch (access.Decision)
        {
            case SharingDecision.Allowed:
                return null;
            case SharingDecision.NoConsent:
                await audit.RefusedAsync(record, patientId, FhirOutcomes.ConsentRequired, ct);
                return Error.Forbidden("abdm.consent_required",
                    "The patient has not agreed to share their records with other providers. Record their data-sharing consent first.");
            case SharingDecision.Restricted:
                // The restricted-record guard's own answer, so the screen can offer emergency access (break-the-glass).
                await audit.RefusedAsync(record, patientId, FhirOutcomes.RestrictedRecord, ct);
                return (await guard.EnsureCanOpenAsync(patientId, ct)).Error ?? Error.Forbidden("patient.restricted", "This is a restricted record.");
            case SharingDecision.Deleted:
                return Error.Rule("abdm.patient_deleted", "This patient's record was deleted, so it cannot be shared.");
            case SharingDecision.Merged:
                return Error.Rule("abdm.patient_merged", "This patient's record was merged into another one. Share it from the record that was kept.");
            default:
                return Error.NotFound(nameof(Patient), patientId);
        }
    }

    /// <summary>The records the summary names: the patient, its facility, its doctor, the stay and the codes' titles.</summary>
    private async Task<Result<DischargeSummarySource>> SummarySourceAsync(DischargeSummaryRow summary, CancellationToken ct)
    {
        if (await patients.FindRowAsync(summary.PatientId, ct) is not { } patient)
        {
            return Error.NotFound(nameof(Patient), summary.PatientId);
        }

        if (await Facilities(f => f.Id == summary.FacilityId).FirstOrDefaultAsync(ct) is not { } facility)
        {
            return Error.NotFound(nameof(Facility), summary.FacilityId);
        }

        var authorId = summary.SignedByPractitionerId ?? summary.PractitionerId;
        var author = authorId is { } practitionerId ? await Practitioners(p => p.Id == practitionerId).FirstOrDefaultAsync(ct) : null;
        var stay = await StayAsync(summary.AdmissionId, ct);
        var ids = new ReferenceIds()
            .Practitioner(summary.PractitionerId, summary.SignedByPractitionerId, stay?.PractitionerId, stay?.Admission?.AdmittingPractitionerId,
                stay?.Admission?.ConsultantPractitionerId)
            .Department(stay?.DepartmentId, stay?.Admission?.DepartmentId)
            .Facility(summary.FacilityId, stay?.FacilityId, patient.RegisteredFacilityId)
            .Admission(summary.AdmissionId);

        return new DischargeSummarySource
        {
            Summary = summary,
            Patient = patient,
            Facility = facility,
            Author = author,
            Stay = stay,
            CodeTitles = await CodeTitlesAsync(summary.DiagnosisCodes, ct),
            RecordedDiagnoses = await RecordedDiagnosesAsync(summary.AdmissionId, summary.DiagnosisCodes, ct),
            References = await references.LoadAsync(ids, ct),
        };
    }

    /// <summary>The records the prescription names: the patient, its facility, its doctor, its visit and its lines.</summary>
    private async Task<Result<PrescriptionSource>> PrescriptionSourceAsync(PrescriptionRow prescription, CancellationToken ct)
    {
        if (await patients.FindRowAsync(prescription.PatientId, ct) is not { } patient)
        {
            return Error.NotFound(nameof(Patient), prescription.PatientId);
        }

        if (await Facilities(f => f.Id == prescription.FacilityId).FirstOrDefaultAsync(ct) is not { } facility)
        {
            return Error.NotFound(nameof(Facility), prescription.FacilityId);
        }

        var prescriber = await Practitioners(p => p.Id == prescription.PractitionerId).FirstOrDefaultAsync(ct);
        var visit = await PrescriptionVisitAsync(prescription, ct);
        var lines = await MedicationRequestProvider.LineRows(
                db.Set<PrescriptionItem>().AsNoTracking().Where(i => i.PrescriptionId == prescription.Id),
                db.Set<Prescription>().Where(p => p.Id == prescription.Id))
            .ToListAsync(ct);
        var ids = new ReferenceIds()
            .Practitioner(prescription.PractitionerId, visit?.PractitionerId, visit?.Admission?.AdmittingPractitionerId,
                visit?.Admission?.ConsultantPractitionerId)
            .Department(visit?.DepartmentId, visit?.Admission?.DepartmentId)
            .Facility(prescription.FacilityId, visit?.FacilityId, patient.RegisteredFacilityId)
            .Admission(prescription.AdmissionId, visit?.Admission?.Id);

        return new PrescriptionSource
        {
            Prescription = prescription,
            Lines = lines,
            Patient = patient,
            Facility = facility,
            Prescriber = prescriber,
            Visit = visit,
            References = await references.LoadAsync(ids, ct),
        };
    }

    /// <summary>
    /// The visit the prescription was written in: its own, else its encounter's, else its admission's in-patient visit;
    /// null when none is known or it can no longer be read.
    /// </summary>
    private async Task<VisitRow?> PrescriptionVisitAsync(PrescriptionRow prescription, CancellationToken ct)
    {
        var visitId = prescription.VisitId;
        var admissionId = prescription.AdmissionId;
        if (visitId is null && prescription.EncounterId is { } encounterId)
        {
            var encounter = await db.Set<Encounter>().AsNoTracking().Where(e => e.Id == encounterId)
                .Select(e => new { e.VisitId, e.AdmissionId })
                .FirstOrDefaultAsync(ct);
            visitId = encounter?.VisitId;
            admissionId ??= encounter?.AdmissionId;
        }

        if (visitId is null && admissionId is { } admission)
        {
            visitId = await db.Set<Admission>().AsNoTracking().Where(a => a.Id == admission).Select(a => (Guid?)a.VisitId).FirstOrDefaultAsync(ct);
        }

        if (visitId is not { } id
            || await EncounterProvider.VisitRows(db.Set<Visit>().AsNoTracking().Where(v => v.Id == id)).FirstOrDefaultAsync(ct) is not { } visit)
        {
            return null;
        }

        // An in-patient visit carries its admission (the latest, as the FHIR API shows it).
        var admitted = await EncounterProvider.AdmissionRows(db.Set<Admission>().AsNoTracking().Where(a => a.VisitId == id).OrderByDescending(a => a.AdmittedAt))
            .FirstOrDefaultAsync(ct);
        return admitted is null ? visit : visit with { Admission = admitted.Admission };
    }

    /// <summary>What doctors recorded about the summary's codes in the stay's encounters: the latest record of each code.</summary>
    private async Task<Dictionary<string, RecordedDiagnosis>> RecordedDiagnosesAsync(Guid admissionId, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var wanted = codes.Select(c => c.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var rows = await StayDiagnoses(admissionId, wanted).ToListAsync(ct);
        return rows.GroupBy(r => r.Code.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.RecordedAt).First())
            .ToDictionary(r => r.Code.Trim().ToUpperInvariant(), r => new RecordedDiagnosis(r.Status, r.OnsetDate, r.ResolvedOn), StringComparer.Ordinal);
    }

    /// <summary>The coded, non-differential diagnoses made in the admission's encounters, with these codes.</summary>
    internal IQueryable<StayDiagnosisRow> StayDiagnoses(Guid admissionId, List<string> codes) =>
        from d in db.Set<Diagnosis>().AsNoTracking()
        join e in db.Set<Encounter>().AsNoTracking() on d.EncounterId equals (Guid?)e.Id
        where e.AdmissionId == admissionId && d.IcdCode != null && codes.Contains(d.IcdCode) && d.Type != DiagnosisType.Differential
        select new StayDiagnosisRow
        {
            Code = d.IcdCode!,
            RecordedAt = d.RecordedAt,
            Status = d.Status,
            OnsetDate = d.OnsetDate,
            ResolvedOn = d.ResolvedOn,
        };

    /// <summary>Prescriptions of the user's facility, whatever their status.</summary>
    internal IQueryable<PrescriptionRow> Prescriptions(Expression<Func<Prescription, bool>> filter) =>
        db.Set<Prescription>().AsNoTracking().Where(filter).Select(p => new PrescriptionRow
        {
            Id = p.Id,
            PatientId = p.PatientId,
            FacilityId = p.FacilityId,
            PrescriptionNumber = p.PrescriptionNumber,
            EncounterId = p.EncounterId,
            VisitId = p.VisitId,
            AdmissionId = p.AdmissionId,
            PractitionerId = p.PractitionerId,
            IsInpatient = p.IsInpatient,
            Status = p.Status,
            PrescribedAt = p.PrescribedAt,
            SignedAt = p.SignedAt,
            Advice = p.Advice,
            RefillOfPrescriptionId = p.RefillOfPrescriptionId,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        });

    /// <summary>Discharge summaries of the user's facility, whatever their status.</summary>
    internal IQueryable<DischargeSummaryRow> Summaries(Expression<Func<DischargeSummary, bool>> filter) =>
        DocumentReferenceProvider.SummaryRows(db.Set<DischargeSummary>().AsNoTracking().Where(filter));

    /// <summary>The admission's in-patient visit, carrying this admission.</summary>
    private async Task<VisitRow?> StayAsync(Guid admissionId, CancellationToken ct)
    {
        var admission = await EncounterProvider.AdmissionRows(db.Set<Admission>().AsNoTracking().Where(a => a.Id == admissionId)).FirstOrDefaultAsync(ct);
        if (admission is null)
        {
            return null;
        }

        var visit = await EncounterProvider.VisitRows(db.Set<Visit>().AsNoTracking().Where(v => v.Id == admission.VisitId)).FirstOrDefaultAsync(ct);
        return visit is null ? null : visit with { Admission = admission.Admission };
    }

    /// <summary>The catalogue title of each code: its ICD-10 or ICD-11 entry, by the code's form.</summary>
    private async Task<Dictionary<string, string>> CodeTitlesAsync(IReadOnlyList<string> codes, CancellationToken ct)
    {
        var wanted = codes.Select(c => c.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var rows = await db.Set<IcdCode>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => wanted.Contains(c.Code))
            .Select(c => new { c.Code, c.Title, c.Release })
            .ToListAsync(ct);
        return rows.Where(r => r.Release == CodeReleases.OfDiagnosisCode(r.Code))
            .GroupBy(r => r.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
    }

    /// <summary>Practitioners (deleted ones too: a signed summary keeps its doctor).</summary>
    internal IQueryable<PractitionerRow> Practitioners(Expression<Func<Practitioner, bool>> filter) =>
        db.Set<Practitioner>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking().Where(filter).Select(p => new PractitionerRow
        {
            Id = p.Id,
            Code = p.Code,
            Title = p.Title,
            FirstName = p.FirstName,
            LastName = p.LastName,
            DisplayName = p.DisplayName,
            Gender = p.Gender,
            Qualification = p.Qualification,
            RegistrationCouncil = p.RegistrationCouncil,
            RegistrationNumber = p.RegistrationNumber,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        });

    /// <summary>Facilities (deleted ones too).</summary>
    internal IQueryable<FacilityRow> Facilities(Expression<Func<Facility, bool>> filter) =>
        db.Set<Facility>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking().Where(filter).Select(f => new FacilityRow
        {
            Id = f.Id,
            Code = f.Code,
            Name = f.Name,
            HfrId = f.HfrId,
            Address = f.Address,
            Phone = f.Phone,
            Email = f.Email,
            CreatedAt = f.CreatedAt,
            UpdatedAt = f.UpdatedAt,
        });

    /// <summary>"prescription-NHRX260000123-abdm.json": the prescription number, as the prescription's PDF is named.</summary>
    internal static string FileName(PrescriptionRow prescription) => $"prescription-{prescription.PrescriptionNumber}-abdm.json";

    /// <summary>"discharge-summary-NH-000123-2026-09-25-abdm.json": the UHID and the discharge date (else the signing or admission date).</summary>
    internal static string FileName(DischargeSummaryRow summary, string uhid) =>
        (summary.DischargedAt ?? summary.SignedAt ?? summary.AdmittedAt) is { } date
            ? string.Create(CultureInfo.InvariantCulture, $"discharge-summary-{uhid}-{FhirTime.ToSeconds(date):yyyy-MM-dd}-abdm.json")
            : $"discharge-summary-{uhid}-abdm.json";
}
