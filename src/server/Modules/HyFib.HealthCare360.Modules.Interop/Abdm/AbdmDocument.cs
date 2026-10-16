using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>The four ABDM health record types HC360 exports (the IG's Composition profiles).</summary>
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
}

/// <summary>
/// A section of a document: the IG's section code, its title, the narrative written in it (one paragraph per line) and
/// the resources it lists. A section has narrative, entries or both.
/// </summary>
internal sealed record AbdmSection(AbdmCode Code, string Title, IReadOnlyList<string> Text, IReadOnlyList<R4.Resource> Entries)
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

    /// <summary>The HC360 record the document is made from (discharge summary, lab report, encounter, prescription).</summary>
    public required Guid SourceId { get; init; }

    /// <summary>The Composition profile (DischargeSummaryRecord, …).</summary>
    public required string Profile { get; init; }

    public required AbdmCode DocumentType { get; init; }
    public required string Title { get; init; }
    public required R4.CompositionStatus Status { get; init; }

    /// <summary>When the record was completed: signed, released or amended.</summary>
    public required DateTimeOffset Date { get; init; }

    /// <summary>The latest change to anything in the document; it versions the document.</summary>
    public required DateTimeOffset LastUpdated { get; init; }

    public required R4.Patient Patient { get; init; }
    public R4.Encounter? Encounter { get; init; }

    /// <summary>Who wrote it: the doctor, or for a laboratory report the laboratory.</summary>
    public required R4.Resource Author { get; init; }

    /// <summary>The doctor who signed it (legal attester), with the time; none for a laboratory report.</summary>
    public R4.Practitioner? Attester { get; init; }

    public DateTimeOffset? AttestedAt { get; init; }

    /// <summary>The facility that keeps the record.</summary>
    public required R4.Organization Custodian { get; init; }

    public required IReadOnlyList<AbdmSection> Sections { get; init; }

    /// <summary>Resources referenced from the entries rather than listed in a section (a report's observations, other doctors).</summary>
    public IReadOnlyList<R4.Resource> Supporting { get; init; } = [];

    /// <summary>The document's version: the latest change, in Unix milliseconds.</summary>
    public string VersionId => LastUpdated.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
}
