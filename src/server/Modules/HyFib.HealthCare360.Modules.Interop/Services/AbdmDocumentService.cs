using System.Globalization;
using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
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
/// ABDM health records (EHR-08): a signed discharge summary as an NRCeS document bundle
/// (<see cref="DischargeSummaryRecordMapper"/>). A record leaves the hospital under the FHIR API's sharing rules
/// (<see cref="PatientSharingService"/>): the patient exists and was not deleted or merged, holds an active record-sharing
/// consent, and, for a restricted record, the caller may open it. Staff export the summaries of the facility they are
/// signed in to, as the FHIR API shares a facility's own records. Every export is audited, and so is every refusal for
/// consent or restriction (<see cref="AbdmAuditTrail"/>).
/// </summary>
internal sealed class AbdmDocumentService(
    HospitalDbContext db,
    PatientSharingService sharing,
    PatientAccessGuard guard,
    PatientRecords patients,
    ReferenceDirectoryLoader references,
    AbdmAuditTrail audit,
    TimeProvider time)
{
    public async Task<Result<AbdmDocument>> DischargeSummaryAsync(Guid id, CancellationToken ct)
    {
        var record = AbdmRecord.DischargeSummary(id);
        var summary = await Summaries(s => s.Id == id).FirstOrDefaultAsync(ct);
        if (summary is null)
        {
            var elsewhere = await db.Set<DischargeSummary>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(s => s.Id == id, ct);
            return elsewhere
                ? Error.Rule("abdm.other_facility", "This discharge summary was written at another facility. Sign in to that facility to export it.")
                : Error.NotFound(nameof(DischargeSummary), id);
        }

        if (await RefuseAsync(record, summary.PatientId, ct) is { } refused)
        {
            return refused;
        }

        if (summary.Status == DischargeSummaryStatus.Draft)
        {
            return Error.Rule("abdm.not_signed", "Only a signed discharge summary can be shared as an ABDM health record. Sign it first.");
        }

        var source = await LoadAsync(summary, ct);
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
    private async Task<Result<DischargeSummarySource>> LoadAsync(DischargeSummaryRow summary, CancellationToken ct)
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

    /// <summary>"discharge-summary-NH-000123-2026-09-25-abdm.json": the UHID and the discharge date (else the signing or admission date).</summary>
    internal static string FileName(DischargeSummaryRow summary, string uhid) =>
        (summary.DischargedAt ?? summary.SignedAt ?? summary.AdmittedAt) is { } date
            ? string.Create(CultureInfo.InvariantCulture, $"discharge-summary-{uhid}-{FhirTime.ToSeconds(date):yyyy-MM-dd}-abdm.json")
            : $"discharge-summary-{uhid}-abdm.json";
}
