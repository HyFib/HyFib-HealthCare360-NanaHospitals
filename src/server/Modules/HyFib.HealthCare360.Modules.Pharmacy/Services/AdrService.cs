using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Adr;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// Suspected adverse drug reactions, reported on the PvPI form by whoever saw them and sent by pharmacovigilance to the
/// ADR monitoring centre with its causality assessment. The form names the patient by initials only.
/// </summary>
internal sealed class AdrService(HospitalDbContext db, INumberSeries numbers, ICurrentUser currentUser, TimeProvider time, IIncidentRegister incidents)
{
    public Task<AdrReport?> FindAsync(Guid id, CancellationToken ct) => db.Set<AdrReport>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<Result<AdrReportDto>> SaveAsync(Guid? id, SaveAdrReportRequest request, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        var today = HospitalClock.Today(time);
        if (request.LearnedOn > today || request.LearnedOn < request.ReactionStartedOn)
        {
            return Error.Field("learnedOn", "The hospital learned of the reaction on or after it started, and not in the future.");
        }

        AdrReport? report;
        if (id is { } existing)
        {
            report = await db.Set<AdrReport>().FirstOrDefaultAsync(r => r.Id == existing, ct);
            if (report is null)
            {
                return Error.NotFound("ADR report", existing);
            }

            if (report.PatientId != request.PatientId)
            {
                return Error.Field("patientId", "A report stays with the patient it was made for.");
            }

            db.SetExpectedVersion(report, request.Version);
        }
        else
        {
            if (currentUser.FacilityId is not { } facility)
            {
                return Error.Rule("facility.required", "Choose the facility first.");
            }

            report = new AdrReport
            {
                FacilityId = facility,
                PatientId = request.PatientId,
                Number = await numbers.NextAsync(PharmacyNumberSeries.AdrReport, facility, ct),
                ReportedBy = currentUser.UserId,
                ReporterName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
            };
            db.Add(report);
        }

        var recorded = report.Record(
            new AdrDetails(
                request.Reaction, request.ReactionStartedOn, request.ReactionStoppedOn, request.Seriousness, request.Outcome, request.WeightKg,
                request.RelevantTests, request.MedicalHistory, request.Drugs.Select(Clean).ToList()),
            today);
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        report.LearnedOn = request.LearnedOn;
        report.IncidentNumber = string.IsNullOrWhiteSpace(request.IncidentNumber) ? null : request.IncidentNumber.Trim().ToUpperInvariant();
        report.ReporterDesignation = string.IsNullOrWhiteSpace(request.ReporterDesignation) ? report.ReporterDesignation : request.ReporterDesignation.Trim();
        if (id is not null)
        {
            await db.SaveChangesAsync(ct);
            return Dto(report, patient, today);
        }

        // A new report raises an incident in the Quality register in the same save (NABH: ADRs are reported incidents), unless
        // it was reported there already and its number given.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (report.IncidentNumber is null)
        {
            var raised = await incidents.ReportAsync(new ReportedSafetyEvent(
                report.FacilityId, report.PatientId, SafetyEventKind.AdverseDrugReaction, AdrHarm.Of(report.Seriousness),
                HospitalClock.StartOfDayUtc(report.ReactionStartedOn), "Not recorded on the ADR form",
                $"Suspected adverse drug reaction ({report.Number}): {report.Reaction}. Suspected: {string.Join(", ", report.Drugs.Where(d => d.Suspected).Select(d => d.Name))}.",
                null, "Pharmacy", nameof(AdrReport), report.Id, report.Number), ct);
            if (raised.IsFailure)
            {
                return raised.Error!;
            }

            report.IncidentNumber = raised.Value.IncidentNumber;
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return Dto(report, patient, today);
    }

    public async Task<Result<AdrReportDto>> SubmitAsync(Guid id, SubmitAdrReportRequest request, CancellationToken ct)
    {
        var report = await db.Set<AdrReport>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null)
        {
            return Error.NotFound("ADR report", id);
        }

        db.SetExpectedVersion(report, request.Version);
        var today = HospitalClock.Today(time);
        var submitted = report.Submit(request.Causality, currentUser.DisplayName ?? currentUser.UserName ?? "Pharmacovigilance", request.SubmittedOn,
            request.Reference, today);
        if (submitted.IsFailure)
        {
            return submitted.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(report, ct);
    }

    /// <summary>The register: every report, or (<paramref name="mine"/>) the ones the user made.</summary>
    public async Task<IReadOnlyList<AdrReportListItem>> ListAsync(bool mine, AdrReportStatus? status, CancellationToken ct)
    {
        var query = db.Set<AdrReport>().AsNoTracking();
        if (mine)
        {
            var me = currentUser.UserId;
            query = query.Where(r => r.ReportedBy == me);
        }

        if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        var reports = await query.OrderByDescending(r => r.LearnedOn).ThenByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct);
        var ids = reports.Select(r => r.PatientId).Distinct().ToList();
        var patients = await db.Set<Patient>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var today = HospitalClock.Today(time);
        return reports.Select(r =>
        {
            var p = patients.GetValueOrDefault(r.PatientId);
            return new AdrReportListItem(
                r.Id, r.Number, p is null ? string.Empty : PatientNames.Initials(p.FullName), p?.Uhid ?? string.Empty, r.Reaction,
                string.Join(", ", r.Drugs.Where(d => d.Suspected).Select(d => d.Name)), r.IsSerious, r.Status, r.LearnedOn, r.DueBy, r.IsOverdue(today),
                r.ReporterName, r.Causality);
        }).ToList();
    }

    /// <summary>What the pharmacy gave the patient in the last 90 days, newest first, to fill in the drugs.</summary>
    public async Task<IReadOnlyList<DispensedMedicine>> MedicinesAsync(Guid patientId, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-90);
        var sold = await (
                from i in db.Set<PharmacySaleItem>().AsNoTracking()
                join s in db.Set<PharmacySale>() on i.PharmacySaleId equals s.Id
                where s.PatientId == patientId && s.Status != SaleStatus.Cancelled && s.SoldAt >= since
                select new { i.ItemId, i.ItemName, i.BatchNumber, i.ExpiryDate, At = s.SoldAt })
            .ToListAsync(ct);
        var issued = await (
                from l in db.Set<IpIssueLine>().AsNoTracking()
                join i in db.Set<IpIssue>() on l.IpIssueId equals i.Id
                where i.PatientId == patientId && i.IssuedAt >= since
                select new { l.ItemId, l.ItemName, l.BatchNumber, l.ExpiryDate, At = i.IssuedAt })
            .ToListAsync(ct);
        var all = sold.Concat(issued).ToList();
        var itemIds = all.Select(a => a.ItemId).Distinct().ToList();
        var items = await db.Set<Item>().AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        return all.GroupBy(a => new { a.ItemId, a.BatchNumber })
            .Select(g => g.OrderByDescending(a => a.At).First())
            .OrderByDescending(a => a.At)
            .Take(50)
            .Select(a => new DispensedMedicine(
                a.ItemName, items.GetValueOrDefault(a.ItemId)?.GenericName, items.GetValueOrDefault(a.ItemId)?.Manufacturer, a.BatchNumber, a.ExpiryDate, a.At))
            .ToList();
    }

    public async Task<AdrReportDto> DtoAsync(AdrReport report, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == report.PatientId, ct);
        return Dto(report, patient, HospitalClock.Today(time));
    }

    private static AdrDrug Clean(AdrDrug d) => d with
    {
        Name = d.Name.Trim(),
        Manufacturer = Trim(d.Manufacturer),
        BatchNumber = Trim(d.BatchNumber),
        Dose = Trim(d.Dose),
        Route = Trim(d.Route),
        Frequency = Trim(d.Frequency),
        Indication = Trim(d.Indication),
    };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AdrReportDto Dto(AdrReport r, Patient p, DateOnly today)
    {
        var age = p.DateOfBirth is { } dob ? r.ReactionStartedOn.Year - dob.Year - (r.ReactionStartedOn < dob.AddYears(r.ReactionStartedOn.Year - dob.Year) ? 1 : 0) : (int?)null;
        return new AdrReportDto(
            r.Id, r.Number, r.PatientId, PatientNames.Initials(p.FullName), p.Uhid, p.Gender.ToString(), age, r.IncidentNumber, r.Status, r.LearnedOn,
            r.DueBy, r.IsOverdue(today), r.Reaction, r.ReactionStartedOn, r.ReactionStoppedOn, r.Seriousness, r.Outcome, r.WeightKg, r.RelevantTests,
            r.MedicalHistory, r.Drugs, r.ReporterName, r.ReporterDesignation, r.Causality, r.AssessedByName, r.SubmittedOn, r.SubmissionReference,
            r.Version);
    }
}

/// <summary>The harm grade (NABH incident reporting) an ADR is reported with, from the PvPI seriousness ticked.</summary>
internal static class AdrHarm
{
    public static IncidentHarm Of(AdrSeriousness seriousness) =>
        seriousness.HasFlag(AdrSeriousness.Death) ? IncidentHarm.Death
        : (seriousness & (AdrSeriousness.LifeThreatening | AdrSeriousness.Disability | AdrSeriousness.CongenitalAnomaly)) != 0 ? IncidentHarm.Severe
        : seriousness != AdrSeriousness.None ? IncidentHarm.Moderate
        : IncidentHarm.Mild;
}
