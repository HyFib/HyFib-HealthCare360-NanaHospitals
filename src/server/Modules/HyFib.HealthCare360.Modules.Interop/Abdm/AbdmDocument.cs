using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>The ABDM health record types HC360 exports (the IG's Composition profiles).</summary>
public enum AbdmRecordType
{
    /// <summary>A signed or amended discharge summary (DischargeSummaryRecord).</summary>
    DischargeSummary = 0,

    /// <summary>A released laboratory report (DiagnosticReportRecord).</summary>
    LabReport,

    /// <summary>A signed out-patient, emergency, telehealth or health-check consultation note (OPConsultRecord).</summary>
    OpConsultation,

    /// <summary>A signed e-prescription (PrescriptionRecord).</summary>
    Prescription,

    /// <summary>A released radiology report (DiagnosticReportRecord, its report a DiagnosticReportImaging).</summary>
    RadiologyReport,

    /// <summary>A released ECG, echo, TMT, Holter or ABPM report (DiagnosticReportRecord, its report a DiagnosticReportImaging).</summary>
    CardiologyReport,
}

/// <summary>
/// A section of a document: the IG's section code, its title, the free text written in it (one paragraph per line), the
/// resources it lists, and the heading its narrative puts over those resources after free text ("Diagnoses:"). A section
/// has free text, entries or both.
/// </summary>
internal sealed record AbdmSection(
    AbdmCode Code, string Title, IReadOnlyList<string> Text, IReadOnlyList<R4.Resource> Entries, string? EntriesHeading = null)
{
    public bool IsEmpty => Text.Count == 0 && Entries.Count == 0;
}

/// <summary>
/// Everything a document holds, before <see cref="AbdmDocumentBuilder"/> gives the resources their document ids and
/// resolves the references between them. References are written as in the FHIR read API (<c>Patient/{id}</c>,
/// <c>Encounter/visit-{id}</c>, <c>Practitioner/{id}</c>).
/// </summary>
internal sealed record AbdmDocument
{
    public required AbdmRecordType Type { get; init; }

    /// <summary>
    /// The HC360 record the document is made from (discharge summary, lab report, radiology report, encounter,
    /// prescription).
    /// </summary>
    public required Guid SourceId { get; init; }

    /// <summary>
    /// The name to save it under, as its PDF is named: <c>prescription-NHRX260000123-abdm.json</c>,
    /// <c>lab-report-LR2600042-abdm.json</c>, <c>radiology-report-NHRD260000042-abdm.json</c> (the accession number),
    /// <c>consultation-NH-000123-2026-09-25-abdm.json</c>, <c>discharge-summary-NH-000123-2026-09-25-abdm.json</c> (the
    /// UHID and the day in IST).
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>The Composition profile (DischargeSummaryRecord, …).</summary>
    public required string Profile { get; init; }

    public required AbdmCode DocumentType { get; init; }
    public required string Title { get; init; }
    public required R4.CompositionStatus Status { get; init; }

    /// <summary>When the record was completed: signed, released or amended.</summary>
    public required DateTimeOffset Date { get; init; }

    /// <summary>
    /// The record's own latest change (created, signed, amended, released). With the latest change to the resources in
    /// the document and a hash of its content it makes the document's version (<see cref="AbdmDocumentBuilder"/>).
    /// </summary>
    public required DateTimeOffset RecordUpdated { get; init; }

    public required R4.Patient Patient { get; init; }
    public R4.Encounter? Encounter { get; init; }

    /// <summary>
    /// Who wrote it: the doctor (for a radiology report the radiologist who signed it off), or for a laboratory report
    /// the laboratory.
    /// </summary>
    public required R4.Resource Author { get; init; }

    /// <summary>
    /// The doctor who amended it (for a radiology report, added the latest addendum after its release), when it was
    /// amended by a doctor: an author of the version too.
    /// </summary>
    public R4.Practitioner? AmendedBy { get; init; }

    /// <summary>
    /// The doctor who answers for this version (legal attester), with the time: the signer, or for an amended record the
    /// doctor who amended it; none for a laboratory report, or an amended record whose amender is not a doctor.
    /// </summary>
    public R4.Practitioner? Attester { get; init; }

    public DateTimeOffset? AttestedAt { get; init; }

    /// <summary>The facility that keeps the record.</summary>
    public required R4.Organization Custodian { get; init; }

    public required IReadOnlyList<AbdmSection> Sections { get; init; }

    /// <summary>
    /// Resources referenced from the entries rather than listed in a section (a report's observations, other doctors).
    /// Those nothing in the document refers to are left out of it.
    /// </summary>
    public IReadOnlyList<R4.Resource> Supporting { get; init; } = [];
}
