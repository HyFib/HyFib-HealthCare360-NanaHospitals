using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Idsp;
using HyFib.HealthCare360.Modules.Clinical.Features.Idsp;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The IDSP register of the facility: each week's P and L forms with their cases, reporting them to IHIP (a nil report
/// too), and the cases added by hand or discarded.
/// </summary>
internal sealed class IdspService(
    HospitalDbContext db,
    PatientAccessGuard guard,
    PatientContactAccess contacts,
    IOutbox outbox,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>How many ended weeks the overview lists before the current one.</summary>
    public const int OverviewWeeks = 8;

    private const string Restricted = "Restricted";

    public async Task<IdspOverview> OverviewAsync(CancellationToken ct)
    {
        var today = time.Today();
        var current = IdspWeek.Of(today);
        var weeks = new List<IdspWeek> { current };
        for (var i = 0; i < OverviewWeeks; i++)
        {
            weeks.Add(weeks[^1].Previous);
        }

        var keys = weeks.Select(w => w.Key).ToList();
        var cases = await db.Set<NotifiableCase>().AsNoTracking()
            .Where(c => c.Status == NotifiableCaseStatus.Open
                        && ((c.PresumedWeek != null && keys.Contains(c.PresumedWeek.Value)) || (c.ConfirmedWeek != null && keys.Contains(c.ConfirmedWeek.Value))))
            .ToListAsync(ct);
        var reports = await ReportsAsync(keys, ct);
        var summaries = weeks.Select(w =>
        {
            var p = cases.Count(c => c.IsOn(IdspForm.Presumptive, w));
            var l = cases.Count(c => c.IsOn(IdspForm.Laboratory, w));
            var pReport = reports.GetValueOrDefault((w.Key, IdspForm.Presumptive));
            var lReport = reports.GetValueOrDefault((w.Key, IdspForm.Laboratory));
            var notReported = Math.Max(0, p - (pReport?.CaseCount ?? 0)) + Math.Max(0, l - (lReport?.CaseCount ?? 0));
            return new IdspWeekSummary(
                w.Year, w.Week, w.Monday, w.Sunday, w.DueOn, p, l, pReport is not null, lReport is not null, notReported,
                Overdue(w, pReport, today) || Overdue(w, lReport, today));
        }).ToList();

        var since = time.GetUtcNow().AddDays(-14);
        var immediateCodes = IdspDiseases.All.Where(d => d.Immediate).Select(d => d.Code).ToList();
        var immediate = await db.Set<NotifiableCase>().AsNoTracking()
            .Where(c => c.Status == NotifiableCaseStatus.Open && immediateCodes.Contains(c.DiseaseCode) && c.AlertedAt != null && c.AlertedAt >= since)
            .OrderByDescending(c => c.AlertedAt)
            .ToListAsync(ct);
        return new IdspOverview(summaries, await RowsAsync(immediate, ct));
    }

    public async Task<IdspWeekDto> WeekAsync(IdspWeek week, CancellationToken ct)
    {
        var today = time.Today();
        var cases = await db.Set<NotifiableCase>().AsNoTracking()
            .Where(c => c.PresumedWeek == week.Key || c.ConfirmedWeek == week.Key)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
        var rows = (await RowsAsync(cases, ct)).ToDictionary(r => r.Id);
        var reports = await ReportsAsync([week.Key], ct);

        IdspFormWeek Form(IdspForm form)
        {
            var on = cases.Where(c => c.IsOn(form, week)).Select(c => rows[c.Id]).ToList();
            var report = reports.GetValueOrDefault((week.Key, form));
            return new IdspFormWeek(
                form, on, report is null ? null : new IdspFormReport(report.ReportedOn, report.Reference, report.CaseCount, report.ReportedAt, report.Version),
                Math.Max(0, on.Count - (report?.CaseCount ?? 0)), Overdue(week, report, today));
        }

        var discarded = cases.Where(c => c.Status == NotifiableCaseStatus.Discarded).Select(c => rows[c.Id]).ToList();
        return new IdspWeekDto(
            week.Year, week.Week, week.Monday, week.Sunday, week.DueOn, today > week.Sunday, Form(IdspForm.Presumptive), Form(IdspForm.Laboratory), discarded);
    }

    public async Task<IdspCaseRow?> CaseAsync(Guid id, CancellationToken ct)
    {
        var @case = await db.Set<NotifiableCase>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return @case is null ? null : (await RowsAsync([@case], ct))[0];
    }

    /// <summary>
    /// Records the week's form as sent to IHIP on <paramref name="request"/>.ReportedOn, with the cases it then holds.
    /// Sending it again after late cases updates the count; the week must be over.
    /// </summary>
    public async Task<Result<IdspWeekDto>> ReportAsync(IdspWeek week, IdspForm form, ReportIdspWeekRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var today = time.Today();
        if (today <= week.Sunday)
        {
            return Error.Rule("idsp.week_not_over", $"Week {week} ends on {week.Sunday:dd-MM-yyyy}. Report it once it is over.");
        }

        if (request.ReportedOn > today || request.ReportedOn <= week.Sunday)
        {
            return Error.Field("reportedOn", $"Give the date it was sent: after {week.Sunday:dd-MM-yyyy}, and not later than today.");
        }

        var count = await db.Set<NotifiableCase>()
            .CountAsync(c => c.Status == NotifiableCaseStatus.Open && (form == IdspForm.Presumptive ? c.PresumedWeek == week.Key : c.ConfirmedWeek == week.Key), ct);
        var report = await db.Set<IdspWeeklyReport>().FirstOrDefaultAsync(r => r.Year == week.Year && r.Week == week.Week && r.Form == form, ct);
        if (report is null)
        {
            if (request.Version is not null)
            {
                return Error.Conflict("idsp.report_missing", "This report was removed meanwhile. Reload the week.");
            }

            report = new IdspWeeklyReport { FacilityId = facility, Year = week.Year, Week = week.Week, Form = form };
            db.Add(report);
        }
        else
        {
            db.SetExpectedVersion(report, request.Version);
        }

        report.ReportedOn = request.ReportedOn;
        report.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        report.CaseCount = count;
        report.ReportedBy = currentUser.UserId;
        report.ReportedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await WeekAsync(week, ct);
    }

    public async Task<Result<IdspCaseRow>> DiscardAsync(Guid id, DiscardIdspCaseRequest request, CancellationToken ct)
    {
        var @case = await db.Set<NotifiableCase>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (@case is null)
        {
            return Error.NotFound("Case", id);
        }

        db.SetExpectedVersion(@case, request.Version);
        var discarded = @case.Discard(request.Reason, currentUser.UserId, time.GetUtcNow());
        if (discarded.IsFailure)
        {
            return discarded.Error!;
        }

        await db.SaveChangesAsync(ct);
        return (await RowsAsync([@case], ct))[0];
    }

    public async Task<Result<IdspCaseRow>> AddAsync(AddIdspCaseRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        if (!await db.Set<Patient>().AnyAsync(p => p.Id == request.PatientId, ct))
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        var disease = IdspDiseases.Find(request.DiseaseCode)!;
        var now = time.GetUtcNow();
        var since = now.AddDays(-NotifiableCase.EpisodeDays);
        var open = await db.Set<NotifiableCase>()
            .Where(c => c.PatientId == request.PatientId && c.DiseaseCode == disease.Code && c.Status == NotifiableCaseStatus.Open)
            .ToListAsync(ct);
        if (open.Any(c => c.LastEventAt >= since))
        {
            return Error.Conflict("idsp.case_exists", $"This patient already has an open case of {disease.Name} in the last {NotifiableCase.EpisodeDays} days.");
        }

        var @case = new NotifiableCase
        {
            FacilityId = facility,
            PatientId = request.PatientId,
            DiseaseCode = disease.Code,
            Source = NotifiableCaseSource.Manual,
            OnsetDate = request.OnsetDate,
        };
        if (string.IsNullOrWhiteSpace(request.TestResult))
        {
            @case.Presume(null, null, now);
        }
        else
        {
            @case.Confirm(null, request.TestName!.Trim(), request.TestResult.Trim(), now);
        }

        db.Add(@case);
        if (disease.Immediate)
        {
            @case.MarkAlerted(now);
            outbox.Enqueue(NotifiableDiseases.AlertType, NotifiableDiseases.AlertFor(@case, disease, @case.ConfirmedAt is null ? "suspected" : "confirmed"),
                facility, expiresIn: TimeSpan.FromDays(2));
        }

        await db.SaveChangesAsync(ct);
        return (await RowsAsync([@case], ct))[0];
    }

    private static bool Overdue(IdspWeek week, IdspWeeklyReport? report, DateOnly today) => report is null && today > week.DueOn;

    private async Task<Dictionary<(int Week, IdspForm Form), IdspWeeklyReport>> ReportsAsync(IReadOnlyCollection<int> keys, CancellationToken ct)
    {
        var years = keys.Select(k => k / 100).Distinct().ToList();
        var reports = await db.Set<IdspWeeklyReport>().AsNoTracking().Where(r => years.Contains(r.Year)).ToListAsync(ct);
        return reports.Where(r => keys.Contains((r.Year * 100) + r.Week)).ToDictionary(r => ((r.Year * 100) + r.Week, r.Form));
    }

    /// <summary>
    /// The rows with each patient's details as the line list needs them: contact details as PRV-03 lets the user see
    /// them, and a restricted (VIP) patient the user may not open reduced to initials (R-3).
    /// </summary>
    private async Task<List<IdspCaseRow>> RowsAsync(IReadOnlyCollection<NotifiableCase> cases, CancellationToken ct)
    {
        var ids = cases.Select(c => c.PatientId).Distinct().ToList();
        var patients = await db.Set<Patient>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var vip = patients.Values.Where(p => p.IsVip).Select(p => p.Id).ToList();
        var openable = vip.Count == 0 ? new HashSet<Guid>() : (await guard.OpenableAsync(vip, ct)).ToHashSet();
        var full = await contacts.FullAsync(ct);
        return cases.Select(c =>
        {
            var p = patients.GetValueOrDefault(c.PatientId);
            var hidden = p is { IsVip: true } && !openable.Contains(p.Id);
            var address = p is null || hidden ? null : PatientContacts.Address(p.Address, full);
            // A disease taken off the list later still shows, by its code.
            var disease = IdspDiseases.Find(c.DiseaseCode);
            return new IdspCaseRow(
                c.Id, c.DiseaseCode, disease?.Name ?? c.DiseaseCode, disease?.Immediate ?? false, c.Source, c.Status, c.PatientId,
                hidden ? PatientNames.Restricted(p!.FullName) : p?.FullName ?? string.Empty, hidden ? Restricted : p?.Uhid ?? string.Empty,
                p?.Gender.ToString(), p is null ? null : AgeAt(p, c.PresumedAt ?? c.ConfirmedAt ?? c.CreatedAt),
                address is null ? null : NullIfEmpty(address.ToString()), hidden ? null : p?.Address.District,
                hidden ? null : PatientContacts.Phone(p?.Phone, full), c.OnsetDate, c.PresumedAt, c.IcdCode, c.ConfirmedAt, c.TestName,
                hidden ? null : c.TestResult, c.AlertedAt, c.DiscardReason, c.Version);
        }).ToList();
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static int? AgeAt(Patient patient, DateTimeOffset at)
    {
        if (patient.DateOfBirth is not { } dob)
        {
            return null;
        }

        var on = VitalRegistration.LocalDate(at);
        var age = on.Year - dob.Year;
        return on < dob.AddYears(age) ? age - 1 : age;
    }
}
