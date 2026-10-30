using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Localization;

namespace HyFib.HealthCare360.Modules.Diagnostics.Documents;

/// <summary>
/// This module's documents for a copy of the record (MRD-04): the released lab, radiology and cardiology reports, each
/// printed as the patient's copy is, in English. A radiology or cardiology report goes by its study's id.
/// </summary>
internal sealed class DiagnosticsRecordDocuments(
    HospitalDbContext db,
    LabReportBuilder labReports,
    RadiologyReportService radiology,
    CardiologyService cardiology,
    ReportDocumentFrame frame) : IRecordDocumentSource
{
    public IReadOnlyCollection<string> Kinds { get; } =
        [LabReportDocumentSource.DocumentKind, RadiologyReportDocumentSource.DocumentKind, CardiologyReportDocumentSource.DocumentKind];

    public async Task<IReadOnlyList<RecordDocumentEntry>> ListAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var lab = await (
                from r in db.Set<LabReport>().AsNoTracking()
                join o in db.Set<LabOrder>() on r.LabOrderId equals o.Id
                where r.PatientId == patientId && (r.Status == LabReportStatus.Released || r.Status == LabReportStatus.Amended) && r.ReleasedAt != null
                select new RecordDocumentEntry(LabReportDocumentSource.DocumentKind, r.Id, "Lab report " + r.ReportNumber, r.ReleasedAt!.Value, o.VisitId, o.AdmissionId))
            .ToListAsync(cancellationToken);
        var imaging = await (
                from r in db.Set<RadiologyReport>().AsNoTracking()
                join s in db.Set<ImagingStudy>() on r.ImagingStudyId equals s.Id
                where r.PatientId == patientId && r.Status == DiagnosticReportStatus.Released && r.ReleasedAt != null
                select new RecordDocumentEntry(RadiologyReportDocumentSource.DocumentKind, s.Id, s.StudyName + " report " + s.AccessionNumber, r.ReleasedAt!.Value,
                    s.VisitId, s.AdmissionId))
            .ToListAsync(cancellationToken);
        var cardiac = await (
                from r in db.Set<CardiologyReport>().AsNoTracking()
                join s in db.Set<CardiologyStudy>() on r.CardiologyStudyId equals s.Id
                where r.PatientId == patientId && r.Status == DiagnosticReportStatus.Released && r.ReleasedAt != null
                select new RecordDocumentEntry(CardiologyReportDocumentSource.DocumentKind, s.Id, s.StudyName + " report " + s.AccessionNumber, r.ReleasedAt!.Value,
                    s.VisitId, s.AdmissionId))
            .ToListAsync(cancellationToken);
        return [.. lab, .. imaging, .. cardiac];
    }

    public async Task<PrintableDocument?> BuildAsync(string kind, Guid id, Guid patientId, CancellationToken cancellationToken)
    {
        var text = DocumentText.English;
        switch (kind)
        {
            case LabReportDocumentSource.DocumentKind:
                var lab = await labReports.BuildAsync(id, releasedOnly: true, cancellationToken);
                return lab is null || lab.Patient.Id != patientId || lab.Sections.Count == 0
                    ? null
                    : await LabReportDocument.BuildAsync(lab, releasedOnly: true, frame, text, cancellationToken);
            case RadiologyReportDocumentSource.DocumentKind:
                var imaging = await radiology.ViewAsync(id, releasedOnly: true, cancellationToken);
                return imaging is null || imaging.Patient.Id != patientId ? null : await RadiologyReportDocument.BuildAsync(imaging, frame, text, cancellationToken);
            case CardiologyReportDocumentSource.DocumentKind:
                var cardiac = await cardiology.ViewAsync(id, releasedOnly: true, cancellationToken);
                return cardiac is null || cardiac.Patient.Id != patientId ? null : await CardiologyReportDocument.BuildAsync(cardiac, frame, text, cancellationToken);
            default:
                return null;
        }
    }
}
