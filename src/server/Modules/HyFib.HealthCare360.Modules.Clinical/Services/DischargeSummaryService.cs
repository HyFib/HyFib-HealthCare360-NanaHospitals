using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Discharge summaries keyed by admission (EHR-01, PP-02): draft, sign, amend with reason, prefill from the structured
/// record, and print with letterhead. Signing closes the MRD "missing discharge summary" deficiency of the chart.
/// </summary>
public sealed class DischargeSummaryService(HospitalDbContext db, ClinicalAccess access, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<Result<DischargeSummaryDto>> SaveDraftAsync(Guid admissionId, SaveDischargeSummaryRequest request, CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(request.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var codes = await CheckCodesAsync(request.DiagnosisCodes, ct);
        if (codes.IsFailure)
        {
            return codes.Error!;
        }

        var summary = await Summaries().Include(s => s.Amendments).FirstOrDefaultAsync(s => s.AdmissionId == admissionId, ct);
        if (summary is null)
        {
            if (currentUser.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.");
            }

            summary = new DischargeSummary { FacilityId = facilityId, AdmissionId = admissionId, PatientId = request.PatientId };
            db.Add(summary);
        }
        else
        {
            if (summary.PatientId != request.PatientId)
            {
                return Error.Rule("discharge_summary.patient_mismatch", "This admission belongs to a different patient.");
            }

            if (request.Version is not { } version)
            {
                return Error.Field("version", "Send the version of the draft you edited.");
            }

            db.SetExpectedVersion(summary, version);
        }

        var updated = summary.Update(request.ToContent());
        if (updated.IsFailure)
        {
            return updated.Error!;
        }

        summary.EncounterId = request.EncounterId ?? summary.EncounterId;
        summary.PractitionerId = request.PractitionerId ?? summary.PractitionerId ?? currentUser.PractitionerId;
        summary.AdmittedAt = request.AdmittedAt ?? summary.AdmittedAt;
        summary.DischargedAt = request.DischargedAt ?? summary.DischargedAt;
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(summary, ct);
    }

    public Task<Result<DischargeSummaryDto>> SignAsync(Guid id, uint version, CancellationToken ct) =>
        ChangeAsync(id, version, async summary =>
        {
            var signed = summary.Sign(currentUser.UserId, currentUser.PractitionerId, time.GetUtcNow());
            if (signed.IsSuccess)
            {
                await ResolveChartDeficiencyAsync(summary.AdmissionId, ct);
            }

            return signed;
        }, ct);

    public async Task<Result<DischargeSummaryDto>> AmendAsync(Guid id, AmendDischargeSummaryRequest request, CancellationToken ct)
    {
        var codes = await CheckCodesAsync(request.DiagnosisCodes, ct);
        if (codes.IsFailure)
        {
            return codes.Error!;
        }

        return await ChangeAsync(id, request.Version,
            summary => Task.FromResult(summary.Amend(request.ToContent(), request.Reason, currentUser.UserId, time.GetUtcNow())), ct);
    }

    /// <summary>Suggested content from diagnoses, orders, prescriptions and the latest encounter of the admission.</summary>
    public async Task<DischargeSummaryPrefill> PrefillAsync(Guid admissionId, CancellationToken ct)
    {
        var encounters = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.AdmissionId == admissionId)
            .OrderBy(e => e.StartedAt)
            .ToListAsync(ct);
        var encounterIds = encounters.Select(e => (Guid?)e.Id).ToList();
        var diagnoses = await db.Set<Diagnosis>().AsNoTracking()
            .Where(d => encounterIds.Contains(d.EncounterId) && d.Type != DiagnosisType.Differential)
            .OrderByDescending(d => d.IsPrimary).ThenByDescending(d => d.Type == DiagnosisType.Final)
            .ToListAsync(ct);
        var orderLines = await (
                from o in db.Set<ClinicalOrder>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                from i in o.Items
                where o.AdmissionId == admissionId && i.Status != OrderItemStatus.Cancelled
                select new { i.ServiceName, i.Category, i.Status })
            .ToListAsync(ct);
        var medicines = await (
                from p in db.Set<Prescription>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                from i in p.Items
                where p.AdmissionId == admissionId && p.Status == PrescriptionStatus.Signed
                orderby p.SignedAt descending, i.Sequence
                select new { i.DrugName, i.Strength, i.Dose, i.Frequency, i.CustomFrequency, i.DurationDays })
            .ToListAsync(ct);
        var last = encounters.LastOrDefault();

        var procedures = orderLines.Where(l => l.Category is ServiceCategory.Procedure or ServiceCategory.Surgery).Select(l => l.ServiceName).Distinct().ToList();
        var investigations = orderLines.Where(l => l.Category is ServiceCategory.Laboratory or ServiceCategory.Radiology or ServiceCategory.Cardiology)
            .Select(l => l.Status == OrderItemStatus.Completed ? l.ServiceName : $"{l.ServiceName} (pending)").Distinct().ToList();
        var medicineLines = medicines
            .Select(m => $"{m.DrugName} {m.Strength} — {m.Dose} {(m.Frequency == DoseFrequency.Custom ? m.CustomFrequency : m.Frequency.ToString())} x {m.DurationDays} days".Replace("  ", " ", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        return new DischargeSummaryPrefill(
            diagnoses.Count == 0 ? null : string.Join(Environment.NewLine, diagnoses.Select(d => d.IcdCode is null ? d.Title : $"{d.Title} ({d.IcdCode})").Distinct()),
            diagnoses.Where(d => d.IcdCode is not null).Select(d => d.IcdCode!).Distinct().ToList(),
            encounters.FirstOrDefault()?.ChiefComplaint,
            procedures.Count == 0 ? null : string.Join(Environment.NewLine, procedures),
            investigations.Count == 0 ? null : string.Join(Environment.NewLine, investigations),
            medicineLines.Count == 0 ? null : string.Join(Environment.NewLine, medicineLines),
            last?.FollowUpDate,
            last?.FollowUpAdvice);
    }

    public async Task<Result<DischargeSummaryPrint>> PrintAsync(Guid id, CancellationToken ct)
    {
        var summary = await Summaries().AsNoTracking().Include(s => s.Amendments).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (summary is null)
        {
            return Error.NotFound(nameof(DischargeSummary), id);
        }

        var patients = await PatientQueries.SummariesAsync(db, [summary.PatientId], time.Today(), ct);
        var consultantId = summary.SignedByPractitionerId ?? summary.PractitionerId;
        return new DischargeSummaryPrint(
            await ToDtoAsync(summary, ct),
            patients[summary.PatientId],
            await PrintQueries.LetterheadAsync(db, summary.FacilityId, ct),
            consultantId is { } c ? await PrintQueries.PrescriberAsync(db, c, ct) : null);
    }

    public IQueryable<DischargeSummary> Summaries() => db.Set<DischargeSummary>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);

    public async Task<DischargeSummaryDto> ToDtoAsync(DischargeSummary s, CancellationToken ct)
    {
        var names = await PrintQueries.PractitionerNamesAsync(db, [s.PractitionerId], ct);
        return new DischargeSummaryDto(s.Id, s.AdmissionId, s.PatientId, s.EncounterId, s.PractitionerId,
            s.PractitionerId is { } p ? names.GetValueOrDefault(p) : null, s.AdmittedAt, s.DischargedAt, s.Diagnoses, s.DiagnosisCodes,
            s.PresentingComplaints, s.CourseInHospital, s.Procedures, s.InvestigationsSummary, s.ConditionAtDischarge, s.Medications, s.Advice,
            s.FollowUpDate, s.FollowUpInstructions, s.Status, s.SignedAt, s.SignedByPractitionerId, s.AmendmentReason, s.AmendedAt, s.Amendments.Count,
            s.Version);
    }

    private async Task<Result<DischargeSummaryDto>> ChangeAsync(Guid id, uint version, Func<DischargeSummary, Task<Result>> change, CancellationToken ct)
    {
        var summary = await Summaries().Include(s => s.Amendments).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (summary is null)
        {
            return Error.NotFound(nameof(DischargeSummary), id);
        }

        var allowed = await access.EnsureCanOpenAsync(summary.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        db.SetExpectedVersion(summary, version);
        var result = await change(summary);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(summary, ct);
    }

    /// <summary>Discharge summaries must use valid catalogue codes so MRD coding and ABDM exports stay consistent.</summary>
    private async Task<Result> CheckCodesAsync(IReadOnlyList<string>? codes, CancellationToken ct)
    {
        var wanted = (codes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return Result.Success();
        }

        var known = await db.Set<IcdCode>().Where(c => wanted.Contains(c.Code) && c.IsActive).Select(c => c.Code).ToListAsync(ct);
        var unknown = wanted.Except(known, StringComparer.Ordinal).ToList();
        return unknown.Count == 0 ? Result.Success() : Error.Field("diagnosisCodes", $"Unknown ICD code(s): {string.Join(", ", unknown)}.");
    }

    private async Task ResolveChartDeficiencyAsync(Guid admissionId, CancellationToken ct)
    {
        var chart = await db.Set<MedicalRecord>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(m => m.Deficiencies)
            .FirstOrDefaultAsync(m => m.AdmissionId == admissionId, ct);
        if (chart is null)
        {
            return;
        }

        foreach (var deficiency in chart.Deficiencies.Where(d => d.IsOpen && d.Type == DeficiencyType.MissingDischargeSummary).ToList())
        {
            chart.ResolveDeficiency(deficiency.Id, currentUser.UserId, "Discharge summary signed.", time.GetUtcNow());
        }
    }
}
