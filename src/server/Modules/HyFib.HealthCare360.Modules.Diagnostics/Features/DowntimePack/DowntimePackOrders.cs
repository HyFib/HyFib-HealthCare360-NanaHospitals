using System.Globalization;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.DowntimePack;

/// <summary>
/// Diagnostics' part of the pre-downtime pack: the lab tests and imaging studies ordered and not yet reported, for every
/// in-patient and for anyone ordered in the last 24 hours, so the lab and radiology know what is still owed on paper and
/// the wards know what to chase by phone.
/// </summary>
internal sealed class DowntimePackOrders(HospitalDbContext db) : IDowntimePackSource
{
    private static readonly LabOrderItemStatus[] LabPending =
        [LabOrderItemStatus.Ordered, LabOrderItemStatus.Collected, LabOrderItemStatus.InLab, LabOrderItemStatus.ResultsEntered];

    private static readonly ImagingStudyStatus[] ImagingPending =
        [ImagingStudyStatus.Ordered, ImagingStudyStatus.Scheduled, ImagingStudyStatus.Arrived, ImagingStudyStatus.InProgress, ImagingStudyStatus.Acquired, ImagingStudyStatus.Reported];

    public async Task<IReadOnlyList<DowntimePackSection>> BuildAsync(DowntimePackContext context, CancellationToken ct)
    {
        var since = context.Now.AddHours(-24);
        var lab = await (
                from i in db.Set<LabOrderItem>().AsNoTracking().IgnoreQueryFilters()
                join o in db.Set<LabOrder>().IgnoreQueryFilters() on i.LabOrderId equals o.Id
                join p in db.Set<Patient>().IgnoreQueryFilters() on o.PatientId equals p.Id
                where o.FacilityId == context.FacilityId && !i.IsDeleted && LabPending.Contains(i.Status)
                      && (o.AdmissionId != null || o.OrderedAt >= since)
                orderby i.Priority descending, o.OrderedAt
                select new { p.FullName, p.Uhid, p.IsVip, o.AccessionNumber, i.TestName, i.Priority, o.OrderedAt, i.Status, InPatient = o.AdmissionId != null })
            .Take(2000)
            .ToListAsync(ct);
        var imaging = await (
                from s in db.Set<ImagingStudy>().AsNoTracking().IgnoreQueryFilters()
                join p in db.Set<Patient>().IgnoreQueryFilters() on s.PatientId equals p.Id
                where s.FacilityId == context.FacilityId && !s.IsDeleted && ImagingPending.Contains(s.Status)
                      && (s.AdmissionId != null || s.OrderedAt >= since)
                orderby s.Priority descending, s.OrderedAt
                select new { p.FullName, p.Uhid, p.IsVip, s.AccessionNumber, s.StudyName, s.Modality, s.Priority, s.OrderedAt, s.ScheduledAt, s.Status, InPatient = s.AdmissionId != null })
            .Take(2000)
            .ToListAsync(ct);

        string[] columns = ["Patient (UHID)", "IP/OP", "Accession", "Test", "Priority", "Ordered", "Status"];
        var labTable = new DowntimePackTable("Laboratory", $"{lab.Count} tests", null, columns,
            [.. lab.Select(r => (IReadOnlyList<string>)[context.Patient(r.FullName, r.Uhid, r.IsVip), r.InPatient ? "IP" : "OP", r.AccessionNumber, r.TestName,
                r.Priority.ToString(), Local(r.OrderedAt), Words(r.Status.ToString())])]);
        var imagingTable = new DowntimePackTable("Imaging", $"{imaging.Count} studies", null, columns,
            [.. imaging.Select(r => (IReadOnlyList<string>)[context.Patient(r.FullName, r.Uhid, r.IsVip), r.InPatient ? "IP" : "OP", r.AccessionNumber,
                $"{r.Modality} {r.StudyName}", r.Priority.ToString(), Local(r.OrderedAt),
                Words(r.Status.ToString()) + (r.ScheduledAt is { } at ? $", booked {Local(at)}" : "")])]);
        return
        [
            new DowntimePackSection("orders", "Pending lab and imaging orders",
                $"Ordered and not yet reported at {Local(context.Now)}: every in-patient's, and any ordered in the last 24 hours. Results during the downtime are phoned (critical values first) and filed on paper.",
                false, [labTable, imagingTable]),
        ];
    }

    private static string Local(DateTimeOffset at) => NumberFormat.ToHospitalTime(at).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    private static string Words(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
}
