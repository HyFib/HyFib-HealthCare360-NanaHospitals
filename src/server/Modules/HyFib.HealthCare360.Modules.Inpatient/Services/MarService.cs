using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Medication chart and administration record (PH-03 MAR feed). Signed in-patient prescriptions become chart lines;
/// when the IP pharmacy dispenses them, the next 24 hours of doses are scheduled on the ward's medication rounds; nurses
/// record each dose once. Methods used by domain-event handlers only stage changes (the raising module commits them).
/// </summary>
public sealed class MarService(HospitalDbContext db, ISettingsService settings, ICurrentUser currentUser, TimeProvider time)
{
    /// <summary>Facility setting: minutes after the scheduled time before a due dose shows as overdue (default 30).</summary>
    public const string GraceSetting = "inpatient.marGraceMinutes";

    public static readonly TimeSpan Horizon = TimeSpan.FromHours(24);

    private const string AsDirected = "As directed";

    public async Task<TimeSpan> GraceAsync(Guid? facilityId, CancellationToken ct)
    {
        var minutes = await settings.GetAsync<int?>(GraceSetting, facilityId, ct);
        return minutes is >= 5 and <= 240 ? TimeSpan.FromMinutes(minutes.Value) : MarRules.DefaultGrace;
    }

    /// <summary>Stages Due rows for the order's doses in the next 24 hours, skipping doses already on the chart.</summary>
    public async Task<int> ScheduleAsync(MedicationOrder order, DateTimeOffset from, string? batchNumber, Guid? dispenseId, CancellationToken ct)
    {
        if (order.Status != MedicationOrderStatus.Active)
        {
            return 0;
        }

        var until = from + Horizon;
        var doses = DoseSchedule.Between(order.Frequency, from, until, order.StartAt, order.EndAt);
        if (doses.Count == 0)
        {
            return 0;
        }

        var staged = db.ChangeTracker.Entries<MedicationAdministration>()
            .Where(e => e.Entity.MedicationOrderId == order.Id)
            .Select(e => e.Entity.ScheduledAt)
            .ToList();

        // A STAT order is a single dose: never chart it twice, whatever the dispense time.
        if (DoseSchedule.IsImmediate(order.Frequency)
            && (staged.Count > 0 || await db.Set<MedicationAdministration>().AnyAsync(m => m.MedicationOrderId == order.Id, ct)))
        {
            return 0;
        }

        var existing = (await db.Set<MedicationAdministration>().AsNoTracking()
                .Where(m => m.MedicationOrderId == order.Id && m.ScheduledAt >= from && m.ScheduledAt < until)
                .Select(m => m.ScheduledAt)
                .ToListAsync(ct))
            .Concat(staged)
            .ToHashSet();

        var added = 0;
        foreach (var at in doses.Where(d => !existing.Contains(d)))
        {
            db.Add(NewDose(order, at, isAdHoc: false, batchNumber, dispenseId));
            added++;
        }

        return added;
    }

    public async Task<Result<MedicationOrderDto>> AddOrderAsync(AdmissionRef admission, AddMedicationOrderRequest request, CancellationToken ct)
    {
        // A late entry after the discharge records what was done; nothing is started for a patient who has gone home.
        if (admission.DischargedAt is not null)
        {
            return Error.Rule("medication.discharged",
                "The patient has been discharged, so no medicine is started now. To record a dose given before the discharge, add it as given.");
        }

        var now = time.GetUtcNow();
        var start = request.StartAt ?? now;
        var order = new MedicationOrder
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            DrugItemId = request.DrugItemId,
            DrugName = request.DrugName.Trim(),
            GenericName = Clean(request.GenericName),
            Strength = Clean(request.Strength),
            Dose = request.Dose.Trim(),
            Route = Clean(request.Route),
            Frequency = request.Frequency.Trim(),
            DurationDays = request.DurationDays,
            StartAt = start,
            EndAt = request.DurationDays is > 0 ? start.AddDays(request.DurationDays.Value) : null,
            Instructions = Clean(request.Instructions),
            IsControlledDrug = request.IsControlledDrug,
            Source = MedicationOrderSource.Manual,
            Status = MedicationOrderStatus.Active,
            OrderedByPractitionerId = request.OrderedByPractitionerId ?? currentUser.PractitionerId,
        };
        db.Add(order);
        await ScheduleAsync(order, start > now ? start : now, null, null, ct);
        await db.SaveChangesAsync(ct);
        return ToDto(order);
    }

    /// <summary>Stops the order; doses not yet due are cancelled, overdue ones stay for the nurse to document.</summary>
    public async Task<Result<MedicationOrderDto>> StopOrderAsync(Guid orderId, string reason, CancellationToken ct)
    {
        var order = await db.Set<MedicationOrder>().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("Medication order", orderId);
        }

        var now = time.GetUtcNow();
        var stopped = order.Stop(currentUser.UserId, reason, now);
        if (stopped.IsFailure)
        {
            return stopped.Error!;
        }

        var future = await db.Set<MedicationAdministration>()
            .Where(m => m.MedicationOrderId == order.Id && m.Status == MarStatus.Due && m.ScheduledAt >= now)
            .ToListAsync(ct);
        future.ForEach(m => m.CancelIfDue("Medication order stopped"));
        await db.SaveChangesAsync(ct);
        return ToDto(order);
    }

    /// <summary>Extends the chart by another 24 hours of doses (for orders not re-dispensed daily, such as ward stock).</summary>
    public async Task<Result<ScheduleResult>> ExtendScheduleAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Set<MedicationOrder>().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("Medication order", orderId);
        }

        if (order.Status != MedicationOrderStatus.Active)
        {
            return Error.Rule("medication.not_active", "This medication order is no longer active.");
        }

        if (order.IsAsNeeded || DoseSchedule.TimesOfDay(order.Frequency).Count == 0)
        {
            return Error.Rule("medication.not_schedulable", "This frequency has no fixed rounds; record doses as they are given.");
        }

        var added = await ScheduleAsync(order, time.GetUtcNow(), null, null, ct);
        await db.SaveChangesAsync(ct);
        return new ScheduleResult(order.Id, added);
    }

    public async Task<Result<MarEntryDto>> RecordAsync(Guid entryId, RecordAdministrationRequest request, CancellationToken ct)
    {
        var entry = await db.Set<MedicationAdministration>().FirstOrDefaultAsync(m => m.Id == entryId, ct);
        if (entry is null)
        {
            return Error.NotFound("Medication dose", entryId);
        }

        db.SetExpectedVersion(entry, request.Version);
        var requiresWitness = entry.MedicationOrderId is { } orderId
                              && await db.Set<MedicationOrder>().Where(o => o.Id == orderId).Select(o => o.IsControlledDrug).FirstOrDefaultAsync(ct);
        var now = time.GetUtcNow();
        var recorded = entry.Record(
            new MarOutcome(request.Outcome, request.AdministeredAt, request.Reason, request.BatchNumber, request.WitnessName, request.Notes),
            currentUser.UserId, currentUser.DisplayName, requiresWitness, now);
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(entry, now, await GraceAsync(entry.FacilityId, ct));
    }

    public async Task<Result<MarEntryDto>> AddAdHocAsync(AdmissionRef admission, AddAdHocDoseRequest request, CancellationToken ct)
    {
        MedicationOrder? order = null;
        if (request.MedicationOrderId is { } orderId)
        {
            order = await db.Set<MedicationOrder>().FirstOrDefaultAsync(o => o.Id == orderId && o.AdmissionId == admission.Id, ct);
            if (order is null)
            {
                return Error.Field("medicationOrderId", "This medication is not on the patient's chart.");
            }
        }

        var now = time.GetUtcNow();
        if (admission.DischargedAt is { } dischargedAt && (!request.GivenNow || (request.ScheduledAt ?? now) > dischargedAt))
        {
            return Error.Rule("medication.discharged",
                "The patient has been discharged: record only a dose given before the discharge, with the time it was given.");
        }

        var entry = new MedicationAdministration
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            MedicationOrderId = order?.Id,
            DrugName = order?.DrugName ?? request.DrugName!.Trim(),
            Dose = request.Dose.Trim(),
            Route = Clean(request.Route) ?? order?.Route,
            ScheduledAt = request.ScheduledAt ?? now,
            Status = MarStatus.Due,
            IsAdHoc = true,
            Notes = Clean(request.Notes),
        };
        db.Add(entry);

        if (request.GivenNow)
        {
            var recorded = entry.Record(
                new MarOutcome(MarStatus.Given, entry.ScheduledAt, request.Reason, request.BatchNumber, request.WitnessName, request.Notes),
                currentUser.UserId, currentUser.DisplayName, order?.IsControlledDrug ?? false, now);
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }
        }

        await db.SaveChangesAsync(ct);
        return ToDto(entry, now, await GraceAsync(admission.FacilityId, ct));
    }

    // ------------------------------------------------------------------ integration events (stage only, never save)

    /// <summary>Charts the lines of a signed in-patient prescription; doses are scheduled when pharmacy dispenses them.</summary>
    public async Task ChartPrescriptionAsync(PrescriptionSigned signed, CancellationToken ct)
    {
        if (!signed.IsInpatient || signed.AdmissionId is not { } admissionId)
        {
            return;
        }

        var admission = await ActiveAdmissionAsync(admissionId, ct);
        if (admission is null)
        {
            return;
        }

        var lineIds = signed.Lines.Select(l => l.LineId).ToList();
        var charted = await db.Set<MedicationOrder>()
            .Where(o => o.PrescriptionLineId != null && lineIds.Contains(o.PrescriptionLineId.Value))
            .Select(o => o.PrescriptionLineId!.Value)
            .ToListAsync(ct);

        foreach (var line in signed.Lines.Where(l => !charted.Contains(l.LineId)))
        {
            db.Add(new MedicationOrder
            {
                FacilityId = admission.Value.FacilityId,
                AdmissionId = admissionId,
                PatientId = admission.Value.PatientId,
                PrescriptionId = signed.PrescriptionId,
                PrescriptionLineId = line.LineId,
                DrugItemId = line.DrugItemId,
                DrugName = Fit(line.DrugName, 200) ?? "Medicine",
                GenericName = Fit(line.GenericName, 200),
                Strength = Fit(line.Strength, 64),
                Dose = Fit(line.Dose, 64) ?? AsDirected,
                Route = Fit(line.Route, 32),
                Frequency = Fit(line.Frequency, 32) ?? AsDirected,
                DurationDays = line.DurationDays > 0 ? line.DurationDays : null,
                StartAt = signed.SignedAt,
                EndAt = line.DurationDays > 0 ? signed.SignedAt.AddDays(line.DurationDays) : null,
                Instructions = Fit(line.Instructions, 500),
                IsControlledDrug = line.IsControlledDrug,
                Source = MedicationOrderSource.Prescription,
                Status = MedicationOrderStatus.Active,
                OrderedByPractitionerId = signed.PractitionerId,
            });
        }
    }

    public async Task StopPrescriptionAsync(PrescriptionCancelled cancelled, CancellationToken ct)
    {
        var orders = await db.Set<MedicationOrder>()
            .Where(o => o.PrescriptionId == cancelled.PrescriptionId && o.Status == MedicationOrderStatus.Active)
            .ToListAsync(ct);
        if (orders.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        var reason = Fit($"Prescription cancelled: {cancelled.Reason}", 300)!;
        orders.ForEach(o => o.Stop(null, reason, now));
        var ids = orders.Select(o => o.Id).ToList();
        var future = await db.Set<MedicationAdministration>()
            .Where(m => m.MedicationOrderId != null && ids.Contains(m.MedicationOrderId.Value) && m.Status == MarStatus.Due && m.ScheduledAt >= now)
            .ToListAsync(ct);
        future.ForEach(m => m.CancelIfDue("Prescription cancelled"));
    }

    /// <summary>
    /// A completed discharge ends the stay's medicines: its active chart lines stop and the doses scheduled from the
    /// discharge on are cancelled, so the MAR, a legal record, does not show a dose as due for a patient who has gone.
    /// Doses that fell due before the discharge stay for the nurse to document, as when a line is stopped.
    /// </summary>
    public async Task StopForDischargeAsync(PatientDischarged discharged, CancellationToken ct)
    {
        const string reason = "Patient discharged";
        var orders = await db.Set<MedicationOrder>()
            .Where(o => o.AdmissionId == discharged.AdmissionId && o.Status == MedicationOrderStatus.Active)
            .ToListAsync(ct);
        orders.ForEach(o => o.Stop(null, reason, discharged.DischargedAt));
        var future = await db.Set<MedicationAdministration>()
            .Where(m => m.AdmissionId == discharged.AdmissionId && m.Status == MarStatus.Due && m.ScheduledAt >= discharged.DischargedAt)
            .ToListAsync(ct);
        future.ForEach(m => m.CancelIfDue(reason));
    }

    /// <summary>
    /// Schedules the next 24 hours of doses for each dispensed line. Lines without a charted prescription line are
    /// charted as a single dose to confirm with the doctor (never a guessed schedule); for patients already in discharge
    /// such lines are treated as take-home medicines and not put on the ward MAR.
    /// </summary>
    public async Task ChartDispenseAsync(MedicationDispensed dispensed, CancellationToken ct)
    {
        if (dispensed.AdmissionId is not { } admissionId)
        {
            return;
        }

        var admission = await ActiveAdmissionAsync(admissionId, ct);
        if (admission is null)
        {
            return;
        }

        var lineIds = dispensed.Lines.Where(l => l.PrescriptionLineId != null).Select(l => l.PrescriptionLineId!.Value).ToList();
        var orders = await db.Set<MedicationOrder>()
            .Where(o => o.AdmissionId == admissionId && o.PrescriptionLineId != null && lineIds.Contains(o.PrescriptionLineId.Value))
            .ToListAsync(ct);

        foreach (var line in dispensed.Lines)
        {
            var order = line.PrescriptionLineId is { } lineId ? orders.FirstOrDefault(o => o.PrescriptionLineId == lineId) : null;
            if (order is not null)
            {
                order.LastDispensedAt = dispensed.DispensedAt;
                await ScheduleAsync(order, dispensed.DispensedAt, Fit(line.BatchNumber, 64), dispensed.DispenseId, ct);
                continue;
            }

            if (admission.Value.Status != AdmissionStatus.Admitted)
            {
                continue;
            }

            var pharmacyOrder = new MedicationOrder
            {
                FacilityId = admission.Value.FacilityId,
                AdmissionId = admissionId,
                PatientId = admission.Value.PatientId,
                DrugItemId = line.ItemId,
                DrugName = Fit(line.ItemName, 200) ?? "Medicine",
                Dose = AsDirected,
                Frequency = AsDirected,
                StartAt = dispensed.DispensedAt,
                Instructions = "Issued by pharmacy without a linked prescription line: confirm dose and frequency with the doctor.",
                Source = MedicationOrderSource.Pharmacy,
                Status = MedicationOrderStatus.Active,
                LastDispensedAt = dispensed.DispensedAt,
            };
            db.Add(pharmacyOrder);
            var dose = NewDose(pharmacyOrder, dispensed.DispensedAt, isAdHoc: true, Fit(line.BatchNumber, 64), dispensed.DispenseId);
            dose.Notes = "Confirm dose and frequency with the prescribing doctor before giving.";
            db.Add(dose);
        }
    }

    public async Task<IReadOnlyList<WardMarItem>> WardDueAsync(Guid wardId, int withinMinutes, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var until = now.AddMinutes(withinMinutes);
        var rows = await (
                from m in db.Set<MedicationAdministration>().AsNoTracking()
                join a in db.Set<Admission>() on m.AdmissionId equals a.Id
                join p in db.Set<Patient>() on m.PatientId equals p.Id
                join b in db.Set<Bed>() on a.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                where a.WardId == wardId && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated)
                      && m.Status == MarStatus.Due && m.ScheduledAt < until
                orderby m.ScheduledAt
                select new { m.Id, m.AdmissionId, m.PatientId, p.Uhid, p.FullName, BedLabel = b != null ? b.Label : null, m.DrugName, m.Dose, m.Route, m.ScheduledAt, m.Version })
            .Take(500)
            .ToListAsync(ct);
        var grace = await GraceAsync(currentUser.FacilityId, ct);
        return rows.Select(r => new WardMarItem(r.Id, r.AdmissionId, r.PatientId, r.Uhid, r.FullName, r.BedLabel, r.DrugName, r.Dose, r.Route, r.ScheduledAt,
                MarRules.IsOverdue(MarStatus.Due, r.ScheduledAt, now, grace), (int)Math.Max(0, (now - r.ScheduledAt).TotalMinutes), r.Version))
            .ToList();
    }

    internal static MedicationOrderDto ToDto(MedicationOrder o) => new(
        o.Id, o.PrescriptionId, o.DrugItemId, o.DrugName, o.GenericName, o.Strength, o.Dose, o.Route, o.Frequency, o.DurationDays, o.StartAt, o.EndAt,
        o.Instructions, o.IsControlledDrug, o.IsAsNeeded, o.Source, o.Status, o.LastDispensedAt, o.StoppedAt, o.StopReason, DoseSchedule.TimesOfDay(o.Frequency));

    internal static MarEntryDto ToDto(MedicationAdministration m, DateTimeOffset now, TimeSpan grace) => new(
        m.Id, m.MedicationOrderId, m.DrugName, m.Dose, m.Route, m.ScheduledAt, m.Status, m.IsOverdue(now, grace), m.IsAdHoc, m.AdministeredAt,
        m.RecordedByName, m.Reason, m.BatchNumber, m.WitnessName, m.Notes, m.Version);

    private static MedicationAdministration NewDose(MedicationOrder order, DateTimeOffset at, bool isAdHoc, string? batchNumber, Guid? dispenseId) => new()
    {
        FacilityId = order.FacilityId,
        AdmissionId = order.AdmissionId,
        PatientId = order.PatientId,
        MedicationOrderId = order.Id,
        DispenseId = dispenseId,
        DrugName = order.DrugName,
        Dose = order.Dose,
        Route = order.Route,
        ScheduledAt = at,
        Status = MarStatus.Due,
        IsAdHoc = isAdHoc,
        BatchNumber = batchNumber,
    };

    private async Task<(Guid FacilityId, Guid PatientId, AdmissionStatus Status)?> ActiveAdmissionAsync(Guid admissionId, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().AsNoTracking()
            .Where(a => a.Id == admissionId && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated))
            .Select(a => new { a.FacilityId, a.PatientId, a.Status })
            .FirstOrDefaultAsync(ct);
        return admission is null ? null : (admission.FacilityId, admission.PatientId, admission.Status);
    }

    /// <summary>Other modules' text may exceed this module's column sizes; truncating keeps their transaction from failing.</summary>
    private static string? Fit(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
