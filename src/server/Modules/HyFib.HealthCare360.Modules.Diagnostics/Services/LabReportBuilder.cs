using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Builds the printable lab report (LIS-07). It has the facility letterhead, patient header, results grouped by
/// section with flags and reference intervals, the signatories (with qualification and registration number), the
/// amendment history and the QR verification code. Staff copies show work in progress; portal copies contain released
/// tests only.
/// </summary>
internal sealed class LabReportBuilder(HospitalDbContext db, DiagnosticsLookups lookups, TimeProvider time)
{
    public const string VerificationPathPrefix = "/api/v1/lab/reports/verify/";

    public async Task<LabReportView?> BuildAsync(Guid reportId, bool releasedOnly, CancellationToken ct)
    {
        var report = await db.Set<LabReport>().AsNoTracking().Include(r => r.Amendments).FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null)
        {
            return null;
        }

        var order = await db.Set<LabOrder>().AsNoTracking().Include(o => o.Items).FirstAsync(o => o.Id == report.LabOrderId, ct);
        var items = order.Items
            .Where(i => releasedOnly ? i.Status == LabOrderItemStatus.Released : i.IsActive)
            .ToList();
        var itemIds = items.Select(i => i.Id).ToList();
        var results = await db.Set<LabResult>().AsNoTracking()
            .Include(r => r.Susceptibilities)
            .Where(r => itemIds.Contains(r.LabOrderItemId) && (!releasedOnly || r.Status == LabResultStatus.Verified))
            .ToListAsync(ct);
        var tubes = await db.Set<LabSample>().AsNoTracking().Where(s => s.LabOrderId == order.Id).ToDictionaryAsync(s => s.Id, ct);
        var testIds = items.Select(i => i.LabTestId).Distinct().ToList();
        var tests = await db.Set<LabTest>().AsNoTracking().Where(t => testIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => new { t.Method, t.Interpretation, t.SpecimenType }, ct);
        var referralIds = items.Select(i => i.ReferralLabId).OfType<Guid>().Distinct().ToList();
        var referralLabs = await db.Set<ReferralLab>().AsNoTracking().Where(l => referralIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        var patient = await lookups.PatientAsync(order.PatientId, ct);
        var facility = await lookups.FacilityAsync(order.FacilityId, ct);
        var referredBy = order.ReferringDoctorName
                         ?? (order.OrderingPractitionerId is { } pid ? (await lookups.PractitionerNamesAsync([pid], ct)).GetValueOrDefault(pid) : null);
        var signatories = await lookups.SignatoriesAsync(results.Where(r => !r.AutoValidated).Select(r => r.VerifiedBy), ct);
        var amendedBy = await lookups.UserNamesAsync(report.Amendments.Select(a => a.AmendedBy), ct);
        var today = DiagnosticsTime.Today(time);

        var sections = items
            .GroupBy(i => i.Section)
            .OrderBy(g => g.Key)
            .Select(g => new LabReportSection(g.Key, g.OrderBy(i => i.TestName).Select(item =>
            {
                var test = tests.GetValueOrDefault(item.LabTestId);
                var tube = item.SampleId is { } sid ? tubes.GetValueOrDefault(sid) : null;
                return new LabReportTest(
                    item.Id,
                    item.TestCode,
                    item.TestName,
                    item.Status,
                    tube?.SpecimenType ?? test?.SpecimenType ?? SpecimenType.Other,
                    test?.Method,
                    test?.Interpretation,
                    item.ReferralLabId is { } lab ? referralLabs.GetValueOrDefault(lab) : null,
                    tube?.CollectedAt,
                    tube?.ReceivedAt,
                    item.VerifiedAt,
                    item.ReleasedAt,
                    results.Where(r => r.LabOrderItemId == item.Id).OrderBy(r => r.DisplayOrder).Select(r => new LabReportResultLine(
                        r.Id, r.TestName, r.GroupName, r.ValueText, r.Unit, r.Flag, r.ReferenceText, r.Status, r.AutoValidated, r.Remarks,
                        r.Organism, r.ColonyCount,
                        r.Susceptibilities.OrderBy(s => s.DisplayOrder).Select(s => new SusceptibilityDto(s.Antibiotic, s.Interpretation, s.Mic)).ToList(),
                        r.PreviousValue, r.PreviousResultAt)).ToList());
            }).ToList()))
            .ToList();

        return new LabReportView(
            report.Id,
            order.Id,
            report.ReportNumber,
            report.Status,
            report.IsPartial,
            report.Revision,
            Letterhead(facility),
            new ReportPatient(order.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown,
                patient?.AgeOn(today), patient?.DateOfBirth),
            order.PatientClass,
            referredBy,
            releasedOnly ? null : order.ClinicalNotes,
            order.OrderedAt,
            report.FirstReleasedAt,
            report.ReleasedAt,
            sections,
            signatories.Select(s => new ReportSignatory(s.UserId, s.Name, s.Qualification, s.RegistrationNumber, s.SignatureFileId)).ToList(),
            results.Any(r => r.AutoValidated),
            report.Amendments.OrderBy(a => a.Revision).Select(a => new LabAmendmentView(
                a.Revision, a.Reason, a.Changes, a.AmendedAt, a.AmendedBy is { } by ? amendedBy.GetValueOrDefault(by) : null)).ToList(),
            report.VerificationCode,
            report.VerificationCode is { } code ? VerificationPathPrefix + code : null,
            await lookups.VerificationUrlAsync(order.FacilityId, DiagnosticKind.Laboratory, report.VerificationCode, ct));
    }

    internal static ReportLetterhead Letterhead(FacilitySnapshot? facility) =>
        facility is null
            ? new ReportLetterhead("Hospital", null, null, null, null, null, null)
            : new ReportLetterhead(
                facility.Name, NullIfEmpty(facility.Address.ToString()), facility.Phone, facility.Email, facility.NabhNumber, facility.LetterheadNote, facility.LogoFileId,
                facility.Id);

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
