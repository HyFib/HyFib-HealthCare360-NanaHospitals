namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>A document of a patient's record that the medical records department can put in a copy it issues (MRD-04).</summary>
/// <param name="Kind">Which <see cref="IRecordDocumentSource"/> prints it, for example "diagnostics.lab-report".</param>
/// <param name="Id">The document's id in its module; a radiology or cardiology report goes by its study's id, as everywhere there.</param>
/// <param name="Title">What it is, as the list and the hash report name it: "Discharge summary", "Lab report LAB2600012".</param>
/// <param name="Date">When it was signed or released.</param>
/// <param name="VisitId">The visit it belongs to, when it has one, so that a copy of one episode can be picked.</param>
/// <param name="AdmissionId">The admission it belongs to, when it has one.</param>
public sealed record RecordDocumentEntry(string Kind, Guid Id, string Title, DateTimeOffset Date, Guid? VisitId, Guid? AdmissionId);

/// <summary>
/// A module's printed documents of a patient's record, for a copy the medical records department issues. Each module
/// registers one source for the kinds it prints. A document is built as the hospital prints it, in English, and only when
/// it is the patient's and may be given out: signed, released, not cancelled.
/// </summary>
public interface IRecordDocumentSource
{
    IReadOnlyCollection<string> Kinds { get; }

    /// <summary>The patient's documents of this source's kinds that may be given out.</summary>
    Task<IReadOnlyList<RecordDocumentEntry>> ListAsync(Guid patientId, CancellationToken cancellationToken);

    /// <returns>The document as printed, or null when it is not the patient's or may not be given out.</returns>
    Task<PrintableDocument?> BuildAsync(string kind, Guid id, Guid patientId, CancellationToken cancellationToken);
}
