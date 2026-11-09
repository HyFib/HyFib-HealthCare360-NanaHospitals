using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Medication chart and administration record (PH-03 MAR feed). Signed in-patient prescriptions become chart lines;
/// when the IP pharmacy dispenses them, the next 24 hours of doses are scheduled on the ward's medication rounds; nurses
/// record each dose once. Methods used by domain-event handlers only stage changes (the raising module commits them).
/// <para>
/// A ward order is signed at once only by the doctor it is written in the name of, signed in as themselves and holding
/// <c>clinical.prescriptions.sign</c>. Anyone else (a nurse, or a doctor writing another doctor's order) records that
/// doctor's verbal or telephone order, read back, and the doctor countersigns it from their own sign-in within the
/// hospital's time (<see cref="CountersignHoursSetting"/>); a high-alert, controlled or chemotherapy medicine gives no
/// dose until then unless it was taken as an emergency order. A nurse's stop waits for the doctor's countersignature with
/// the doses held. What is not countersigned in time is listed for the ward and the doctor, and both are told.
/// </para>
/// </summary>
public sealed class MarService(
    HospitalDbContext db,
    ISettingsService settings,
    ICurrentUser currentUser,
    IWitnessConfirmations witnesses,
    IPermissionService permissions,
    INotificationService notifications,
    IAuditLogger audit,
    TimeProvider time,
    IIncidentRegister incidents)
{
    /// <summary>
    /// What a second nurse witnesses on the MAR: a controlled drug or a high-alert medicine given. Whoever keeps the MAR
    /// (nurses, doctors) may witness, at the recording nurse's screen, with their own user name and password.
    /// </summary>
    public static readonly WitnessPurpose DoseWitness = new("mar.dose", InpatientPermissions.NursingMar, "a controlled drug or high-alert medicine given");

    /// <summary>Facility setting: minutes after the scheduled time before a due dose shows as overdue (default 30).</summary>
    public const string GraceSetting = "inpatient.marGraceMinutes";

    /// <summary>
    /// Facility setting: hours within which the doctor countersigns a verbal or telephone order, or a nurse's stop (1 to 72;
    /// 24 by default, and for any other value). Lengthening it waits for a second administrator (SettingGuard).
    /// </summary>
    public const string CountersignHoursSetting = "inpatient.verbalOrderCountersignHours";

    public const int DefaultCountersignHours = 24;

    public static readonly TimeSpan Horizon = TimeSpan.FromHours(24);

    /// <summary>Where the orders waiting for a countersignature are listed in the staff app (the notifications link there).</summary>
    public const string CountersignaturesLink = "/nursing/countersignatures";

    private const string AsDirected = "As directed";
    private const string Category = "nursing";

    private static readonly PractitionerKind[] Doctors = [.. PractitionerKinds.Doctors];

    public async Task<TimeSpan> GraceAsync(Guid? facilityId, CancellationToken ct)
    {
        var minutes = await settings.GetAsync<int?>(GraceSetting, facilityId, ct);
        return minutes is >= 5 and <= 240 ? TimeSpan.FromMinutes(minutes.Value) : MarRules.DefaultGrace;
    }

    /// <summary>How long the doctor has to countersign a verbal or telephone order, or a nurse's stop, in this facility.</summary>
    public async Task<TimeSpan> CountersignWindowAsync(Guid? facilityId, CancellationToken ct)
    {
        var hours = await settings.GetAsync<int?>(CountersignHoursSetting, facilityId, ct);
        return TimeSpan.FromHours(hours is >= 1 and <= 72 ? hours.Value : DefaultCountersignHours);
    }

    /// <summary>Stages Due rows for the order's doses in the next 24 hours, skipping doses already on the chart.</summary>
    public async Task<int> ScheduleAsync(MedicationOrder order, DateTimeOffset from, string? batchNumber, Guid? dispenseId, CancellationToken ct)
    {
        // Nothing is put on the round while the order's doses are held: a stop waiting for the doctor, or a high-alert verbal
        // order not yet countersigned. The doctor's countersignature schedules them.
        if (order.Status != MedicationOrderStatus.Active || order.DosesHeld)
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

        // The drug master says whether a medicine is controlled or high-alert; what the form sends can only add care. One
        // typed in without an item of the master is handled as high-alert: its doses are witnessed and, ordered verbally,
        // wait for the doctor, so typing a name instead of picking the drug clears nothing.
        (bool Controlled, bool HighAlert) drug = (false, true);
        if (request.DrugItemId is { } itemId && !(await DrugClassesAsync([itemId], ct)).TryGetValue(itemId, out drug))
        {
            return Error.Field("drugItemId", "This medicine is not in the drug master. Choose it again, or type it in.");
        }

        var doctorId = request.OrderedByPractitionerId ?? currentUser.PractitionerId;
        var doctor = doctorId is null
            ? null
            : await db.Set<Practitioner>().AsNoTracking()
                .Where(p => p.Id == doctorId && p.IsActive && Doctors.Contains(p.Kind))
                .Select(p => new { p.Id, p.DisplayName })
                .FirstOrDefaultAsync(ct);
        if (doctor is null)
        {
            return Error.Field("orderedByPractitionerId", "Choose the doctor who ordered it, from the practitioner list.");
        }

        // The doctor named, signed in as themselves and a prescriber, signs their own order. Anyone else writes the doctor's
        // verbal or telephone order: it says which, it was read back, and the doctor countersigns it.
        var verbal = !(currentUser.PractitionerId == doctor.Id && await PrescribesAsync(ct));
        if (verbal && request.Mode is not (MedicationOrderMode.Verbal or MedicationOrderMode.Telephone))
        {
            return Error.Rule("medication.verbal_order_required",
                $"You are writing {doctor.DisplayName}'s order: record it as a verbal or telephone order, read back to the doctor, for them to countersign.");
        }

        if (verbal && !request.ReadBack)
        {
            return Error.Rule("medication.read_back_required",
                $"Read the order back to {doctor.DisplayName} and record that they confirmed it before it goes on the chart.");
        }

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
            IsControlledDrug = request.IsControlledDrug || drug.Controlled,
            IsHighAlert = request.IsHighAlert || drug.HighAlert,
            Source = MedicationOrderSource.Manual,
            Status = MedicationOrderStatus.Active,
            OrderedByPractitionerId = doctor.Id,
            Mode = verbal ? request.Mode!.Value : MedicationOrderMode.Written,
            ReadBack = verbal,
            IsEmergency = verbal && request.Emergency,
            EnteredBy = currentUser.UserId,
            EnteredByName = Name(),
            CountersignDueAt = verbal ? now + await CountersignWindowAsync(admission.FacilityId, ct) : null,
        };
        db.Add(order);
        await ScheduleAsync(order, start > now ? start : now, null, null, ct);
        if (verbal)
        {
            audit.Add(OrderAudit(order, "medication.verbal_order",
                $"{Words(order)}: {(order.Mode == MedicationOrderMode.Telephone ? "telephone" : "verbal")} order of {doctor.DisplayName}, read back, written by {order.EnteredByName}"
                + (order.IsEmergency ? " as an emergency" : "") + (order.DosesHeld ? "; no dose until countersigned" : ""),
                AuditSeverity.Warning));
        }

        await db.SaveChangesAsync(ct);
        if (verbal)
        {
            await NotifyDoctorAsync(doctor.Id, "Verbal order to countersign",
                $"{Words(order)}, written in your name by {order.EnteredByName}. Countersign it by {Local(order.CountersignDueAt!.Value)}."
                + (order.DosesHeld ? " No dose is given until you do." : ""), ct);
        }

        return (await ToDtosAsync([order], ct))[0];
    }

    /// <summary>
    /// A prescriber stops the order at once: doses not yet due are cancelled, overdue ones stay for the nurse to document.
    /// Anyone else asks for the stop: the doses are held while it waits, and the doctor named (or the patient's consultant)
    /// confirms it or declines it.
    /// </summary>
    public async Task<Result<MedicationOrderDto>> StopOrderAsync(Guid orderId, string reason, CancellationToken ct)
    {
        var order = await db.Set<MedicationOrder>().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("Medication order", orderId);
        }

        var now = time.GetUtcNow();
        if (await PrescribesAsync(ct))
        {
            var stopped = order.Stop(currentUser.UserId, reason, now);
            if (stopped.IsFailure)
            {
                return stopped.Error!;
            }

            await CancelDueAsync(order.Id, now, "Medication order stopped", ct);
            await db.SaveChangesAsync(ct);
            return (await ToDtosAsync([order], ct))[0];
        }

        var countersigner = await StopCountersignerAsync(order, ct);
        var requested = order.RequestStop(currentUser.UserId ?? Guid.Empty, Name(), reason, now, now + await CountersignWindowAsync(order.FacilityId, ct));
        if (requested.IsFailure)
        {
            return requested.Error!;
        }

        audit.Add(OrderAudit(order, "medication.stop_requested",
            $"{Words(order)}: stop asked for by {order.StopRequestedByName} ({order.StopRequestReason}); doses held until the doctor countersigns", AuditSeverity.Warning));
        await db.SaveChangesAsync(ct);
        if (countersigner is { } doctorId)
        {
            await NotifyDoctorAsync(doctorId, "Stop of a medicine to countersign",
                $"{Words(order)}: {order.StopRequestedByName} asks to stop it ({order.StopRequestReason}). Its doses are held until you confirm or decline the stop.", ct);
        }

        return (await ToDtosAsync([order], ct))[0];
    }

    /// <summary>
    /// The doctor named countersigns the verbal or telephone order someone else wrote in their name: only from their own
    /// account (the practitioner linked to it), as a prescriber. Doses held until now are scheduled.
    /// </summary>
    public async Task<Result<MedicationOrderDto>> CountersignAsync(Guid orderId, uint version, CancellationToken ct)
    {
        var order = await db.Set<MedicationOrder>().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("Medication order", orderId);
        }

        var allowed = await EnsureCountersignerAsync(order.OrderedByPractitionerId, "the order", ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        db.SetExpectedVersion(order, version);
        var now = time.GetUtcNow();
        var signed = order.Countersign(currentUser.UserId!.Value, Name(), now);
        if (signed.IsFailure)
        {
            return signed.Error!;
        }

        var scheduled = await ScheduleAsync(order, order.StartAt > now ? order.StartAt : now, null, null, ct);
        audit.Add(OrderAudit(order, "medication.countersigned",
            $"{Words(order)}: countersigned by {order.CountersignedByName}"
            + (order.CountersignDueAt is { } due && now > due ? $", {Late(now - due)} after the time it was due by" : "")
            + (scheduled > 0 ? $"; {scheduled} doses scheduled" : ""),
            AuditSeverity.Info));
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([order], ct))[0];
    }

    /// <summary>The doctor confirms the stop a nurse asked for: the order stops, and doses from the time it was asked for are cancelled.</summary>
    public async Task<Result<MedicationOrderDto>> ConfirmStopAsync(Guid orderId, uint version, CancellationToken ct)
    {
        var order = await db.Set<MedicationOrder>().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("Medication order", orderId);
        }

        var allowed = await EnsureCountersignerAsync(await StopCountersignerAsync(order, ct), "the stop", ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        db.SetExpectedVersion(order, version);
        var now = time.GetUtcNow();
        var (askedAt, askedBy) = (order.StopRequestedAt, order.StopRequestedByName);
        var stopped = order.ConfirmStop(currentUser.UserId!.Value, now);
        if (stopped.IsFailure)
        {
            return stopped.Error!;
        }

        // The doses held since the stop was asked for were never given: they are cancelled with the stop. Doses due before it
        // stay for the nurse to document, as for any stop.
        await CancelDueAsync(order.Id, askedAt ?? now, "Medication order stopped", ct);
        audit.Add(OrderAudit(order, "medication.stop_countersigned", $"{Words(order)}: stop asked for by {askedBy} countersigned by {Name()}", AuditSeverity.Info));
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([order], ct))[0];
    }

    /// <summary>The doctor declines the stop a nurse asked for: the order goes on and its doses are given again; the nurse is told.</summary>
    public async Task<Result<MedicationOrderDto>> DeclineStopAsync(Guid orderId, string reason, uint version, CancellationToken ct)
    {
        var order = await db.Set<MedicationOrder>().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("Medication order", orderId);
        }

        var allowed = await EnsureCountersignerAsync(await StopCountersignerAsync(order, ct), "the stop", ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        db.SetExpectedVersion(order, version);
        var (askedBy, askedByName, askedReason) = (order.StopRequestedBy, order.StopRequestedByName, order.StopRequestReason);
        var declined = order.DeclineStop();
        if (declined.IsFailure)
        {
            return declined.Error!;
        }

        audit.Add(OrderAudit(order, "medication.stop_declined",
            $"{Words(order)}: stop asked for by {askedByName} ({askedReason}) declined by {Name()}: {reason.Trim()}", AuditSeverity.Warning));
        await db.SaveChangesAsync(ct);
        if (askedBy is { } nurse && nurse != Guid.Empty)
        {
            await notifications.NotifyUsersAsync([nurse], "Stop of a medicine declined", $"{Words(order)}: {Name()} declined the stop ({reason.Trim()}). Its doses go on.",
                CountersignaturesLink, Category, "warning", ct);
        }

        return (await ToDtosAsync([order], ct))[0];
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

        var held = order.CanGiveDose();
        if (held.IsFailure)
        {
            return held.Error!;
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
        var order = entry.MedicationOrderId is { } orderId ? await db.Set<MedicationOrder>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct) : null;
        if (entry.Status == MarStatus.Due && request.Outcome == MarStatus.Given && order?.CanGiveDose() is { IsFailure: true } held)
        {
            return held.Error!;
        }

        var witness = await WitnessAsync(request.WitnessConfirmationId, entry, ct);
        if (witness.IsFailure)
        {
            return witness.Error!;
        }

        var now = time.GetUtcNow();
        var recorded = entry.Record(
            new MarOutcome(request.Outcome, request.AdministeredAt, request.Reason, request.BatchNumber, witness.Value, request.Notes),
            currentUser.UserId, currentUser.DisplayName, MarRules.RequiresWitness(order), now);
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        if (entry.Status != MarStatus.Missed)
        {
            await db.SaveChangesAsync(ct);
            return ToDto(entry, now, await GraceAsync(entry.FacilityId, ct), order);
        }

        // A dose recorded as missed is an error of omission (NCC MERP): it goes to the incident register in the same save.
        // A refusal or a dose held on purpose is the patient's choice or a clinical decision, not an error, and is not reported.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        var raised = await incidents.ReportAsync(new ReportedSafetyEvent(
            entry.FacilityId, entry.PatientId, SafetyEventKind.MedicationError, IncidentHarm.NoHarm, entry.ScheduledAt,
            await WardNameAsync(entry.AdmissionId, ct),
            $"Dose missed (omission): {entry.DrugName} {entry.Dose}{(entry.Route is { } route ? " " + route : string.Empty)} due {entry.ScheduledAt:u}. Reason recorded: {entry.Reason}",
            null, "Inpatient", nameof(MedicationAdministration), entry.Id, $"{entry.DrugName} {entry.Dose}"), ct);
        if (raised.IsFailure)
        {
            return raised.Error!;
        }

        await transaction.CommitAsync(ct);
        return ToDto(entry, now, await GraceAsync(entry.FacilityId, ct), order);
    }

    private async Task<string> WardNameAsync(Guid admissionId, CancellationToken ct) =>
        await (from a in db.Set<Admission>()
               join w in db.Set<Ward>() on a.WardId equals w.Id
               where a.Id == admissionId
               select w.Name).FirstOrDefaultAsync(ct) ?? "Ward";

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

            // No extra dose of an order whose doses are held, given now or put on the chart for later.
            var held = order.CanGiveDose();
            if (held.IsFailure)
            {
                return held.Error!;
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
            var witness = await WitnessAsync(request.WitnessConfirmationId, entry, ct);
            if (witness.IsFailure)
            {
                return witness.Error!;
            }

            // A dose with no order behind it is always witnessed: nobody prescribed it, so the second nurse is the only check.
            var recorded = entry.Record(
                new MarOutcome(MarStatus.Given, entry.ScheduledAt, request.Reason, request.BatchNumber, witness.Value, request.Notes),
                currentUser.UserId, currentUser.DisplayName, MarRules.RequiresWitness(order), now);
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }
        }

        await db.SaveChangesAsync(ct);
        return ToDto(entry, now, await GraceAsync(admission.FacilityId, ct), order);
    }

    /// <summary>
    /// The second nurse who witnessed the dose, from their confirmation at this screen; none when none was sent. The
    /// confirmation is spent with the dose's save, so a dose not recorded leaves it for another try.
    /// </summary>
    private async Task<Result<MarWitness?>> WitnessAsync(Guid? confirmationId, MedicationAdministration entry, CancellationToken ct)
    {
        if (confirmationId is not { } id)
        {
            return Result<MarWitness?>.Ok(null);
        }

        var used = await witnesses.UseAsync(id, DoseWitness.Key, entry.PatientId, nameof(MedicationAdministration), entry.Id, ct);
        return used.IsFailure ? used.Error! : new MarWitness(used.Value.UserId, used.Value.Name);
    }

    /// <summary>
    /// How the drug master classes these items: a controlled drug (Schedule H1 or X, or NDPS), and a high-alert medicine.
    /// An order copies it when it is made, so a dose given is witnessed whatever the form said. Items not in the master are
    /// left out.
    /// </summary>
    private async Task<Dictionary<Guid, (bool Controlled, bool HighAlert)>> DrugClassesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        var items = await db.Set<Item>().AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Schedule, i.IsControlled, i.IsHighAlert })
            .ToListAsync(ct);
        return items.ToDictionary(i => i.Id, i => (Item.NeedsRegister(i.Schedule, i.IsControlled), i.IsHighAlert));
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

        var drugs = await DrugClassesAsync([.. signed.Lines.Where(l => l.DrugItemId is not null).Select(l => l.DrugItemId!.Value).Distinct()], ct);
        foreach (var line in signed.Lines.Where(l => !charted.Contains(l.LineId)))
        {
            var drug = line.DrugItemId is { } itemId ? drugs.GetValueOrDefault(itemId) : default;
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
                // The line is controlled as the master said at signing (Clinical reads it again then); high-alert is the master's here.
                IsControlledDrug = line.IsControlledDrug,
                IsHighAlert = drug.HighAlert,
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
    /// charted as a single dose to confirm with the doctor (never a guessed schedule), on an order that takes the drug's
    /// class from the master as one typed on the ward does; for patients already in discharge such lines are treated as
    /// take-home medicines and not put on the ward MAR.
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
        var drugs = await DrugClassesAsync(
            [.. dispensed.Lines.Where(l => orders.TrueForAll(o => o.PrescriptionLineId != l.PrescriptionLineId)).Select(l => l.ItemId).Distinct()], ct);

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

            // A controlled or high-alert medicine issued this way is witnessed when given, as the master says.
            var drug = drugs.GetValueOrDefault(line.ItemId);
            var pharmacyOrder = new MedicationOrder
            {
                FacilityId = admission.Value.FacilityId,
                AdmissionId = admissionId,
                PatientId = admission.Value.PatientId,
                DrugItemId = line.ItemId,
                DrugName = Fit(line.ItemName, 200) ?? "Medicine",
                IsControlledDrug = drug.Controlled,
                IsHighAlert = drug.HighAlert,
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
                join o in db.Set<MedicationOrder>() on m.MedicationOrderId equals (Guid?)o.Id into orders
                from o in orders.DefaultIfEmpty()
                where a.WardId == wardId && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated)
                      && m.Status == MarStatus.Due && m.ScheduledAt < until
                orderby m.ScheduledAt
                select new
                {
                    m.Id,
                    m.AdmissionId,
                    m.PatientId,
                    p.Uhid,
                    p.FullName,
                    BedLabel = b != null ? b.Label : null,
                    m.DrugName,
                    m.Dose,
                    m.Route,
                    m.ScheduledAt,
                    m.Version,
                    Order = o,
                })
            .Take(500)
            .ToListAsync(ct);
        var grace = await GraceAsync(currentUser.FacilityId, ct);
        return rows.Select(r => new WardMarItem(r.Id, r.AdmissionId, r.PatientId, r.Uhid, r.FullName, r.BedLabel, r.DrugName, r.Dose, r.Route, r.ScheduledAt,
                MarRules.IsOverdue(MarStatus.Due, r.ScheduledAt, now, grace), (int)Math.Max(0, (now - r.ScheduledAt).TotalMinutes), r.Version,
                MarRules.RequiresWitness(r.Order), HeldBecause(r.Order)))
            .ToList();
    }

    /// <summary>
    /// The verbal and telephone orders, and the nurses' stops, waiting for a doctor's countersignature in this facility,
    /// longest waiting first: for one ward (its nurses and nurse in charge), for the signed-in doctor (<paramref name="mine"/>:
    /// those they countersign), and only those past their time (<paramref name="overdueOnly"/>).
    /// </summary>
    public async Task<IReadOnlyList<AwaitingCountersignatureItem>> AwaitingCountersignatureAsync(Guid? wardId, bool mine, bool overdueOnly, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var me = currentUser.PractitionerId;
        if (mine && me is null)
        {
            return [];
        }

        var rows = from o in db.Set<MedicationOrder>().AsNoTracking()
                   join a in db.Set<Admission>() on o.AdmissionId equals a.Id
                   join p in db.Set<Patient>() on o.PatientId equals p.Id
                   join w in db.Set<Ward>() on a.WardId equals w.Id into wards
                   from w in wards.DefaultIfEmpty()
                   join b in db.Set<Bed>() on a.BedId equals b.Id into beds
                   from b in beds.DefaultIfEmpty()
                   where (o.Mode != MedicationOrderMode.Written && o.CountersignedAt == null)
                         || (o.StopRequestedAt != null && o.Status == MedicationOrderStatus.Active)
                   select new
                   {
                       Order = o,
                       AdmissionId = a.Id,
                       a.PatientId,
                       p.Uhid,
                       p.FullName,
                       a.WardId,
                       WardName = w != null ? w.Name : null,
                       BedLabel = b != null ? b.Label : null,
                       Countersigner = o.OrderedByPractitionerId ?? a.ConsultantPractitionerId,
                       Due = o.Mode != MedicationOrderMode.Written && o.CountersignedAt == null ? o.CountersignDueAt : o.StopCountersignDueAt,
                   };
        if (wardId is { } ward)
        {
            rows = rows.Where(r => r.WardId == ward);
        }

        if (mine)
        {
            rows = rows.Where(r => r.Countersigner == me);
        }

        if (overdueOnly)
        {
            rows = rows.Where(r => (r.Order.Mode != MedicationOrderMode.Written && r.Order.CountersignedAt == null && r.Order.CountersignDueAt < now)
                                   || (r.Order.StopRequestedAt != null && r.Order.Status == MedicationOrderStatus.Active && r.Order.StopCountersignDueAt < now));
        }

        var found = await rows.OrderBy(r => r.Due).Take(500).ToListAsync(ct);
        var orders = await ToDtosAsync([.. found.Select(r => r.Order)], ct);
        return [.. found.Select((r, i) => new AwaitingCountersignatureItem(orders[i], r.AdmissionId, r.PatientId, r.Uhid, r.FullName, r.WardId, r.WardName, r.BedLabel))];
    }

    /// <summary>
    /// Tells the doctor, and the facility's nurses in charge (<c>nursing.in_charge</c>), of each verbal or telephone order and
    /// each nurse's stop not countersigned in time, once each, audited at Warning. Run by <see cref="CountersignOverdueJob"/>.
    /// </summary>
    public async Task<int> NotifyOverdueCountersignaturesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = await (
                from o in db.Set<MedicationOrder>()
                join a in db.Set<Admission>() on o.AdmissionId equals a.Id
                join w in db.Set<Ward>() on a.WardId equals w.Id into wards
                from w in wards.DefaultIfEmpty()
                where (o.Mode != MedicationOrderMode.Written && o.CountersignedAt == null && o.CountersignDueAt < now && o.CountersignOverdueNotifiedAt == null)
                      || (o.StopRequestedAt != null && o.Status == MedicationOrderStatus.Active && o.StopCountersignDueAt < now && o.StopOverdueNotifiedAt == null)
                select new { Order = o, Countersigner = o.OrderedByPractitionerId ?? a.ConsultantPractitionerId, WardName = w != null ? w.Name : null })
            .Take(200)
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        var told = new List<(Guid FacilityId, Guid? Doctor, string Title, string Body)>();
        foreach (var row in due)
        {
            var order = row.Order;
            var where = row.WardName is null ? "" : $" on {row.WardName}";
            if (order.CountersignOverdue(now) && order.CountersignOverdueNotifiedAt is null)
            {
                order.CountersignOverdueNotifiedAt = now;
                var body = $"{Words(order)}{where}: {(order.Mode == MedicationOrderMode.Telephone ? "telephone" : "verbal")} order written by {order.EnteredByName} on {Local(order.CreatedAt)}, not countersigned by {Local(order.CountersignDueAt!.Value)}.";
                audit.Add(OrderAudit(order, "medication.countersign_overdue", body, AuditSeverity.Warning));
                told.Add((order.FacilityId, row.Countersigner, "Verbal order not countersigned in time", body));
            }

            if (order.StopCountersignOverdue(now) && order.StopOverdueNotifiedAt is null)
            {
                order.StopOverdueNotifiedAt = now;
                var body = $"{Words(order)}{where}: stop asked for by {order.StopRequestedByName} on {Local(order.StopRequestedAt!.Value)}, not countersigned by {Local(order.StopCountersignDueAt!.Value)}. Its doses are held.";
                audit.Add(OrderAudit(order, "medication.stop_countersign_overdue", body, AuditSeverity.Warning));
                told.Add((order.FacilityId, row.Countersigner, "Stop of a medicine not countersigned in time", body));
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var (facilityId, doctor, title, body) in told)
        {
            if (doctor is { } doctorId)
            {
                await NotifyDoctorAsync(doctorId, title, body, ct, "warning");
            }

            await notifications.NotifyPermissionHoldersAsync(InpatientPermissions.NursingInCharge, facilityId, title, body, CountersignaturesLink, Category, "warning", ct);
        }

        return told.Count;
    }

    /// <summary>The orders as the chart shows them: the doctor named, and whether the signed-in user countersigns what waits.</summary>
    public async Task<List<MedicationOrderDto>> ToDtosAsync(IReadOnlyList<MedicationOrder> orders, CancellationToken ct)
    {
        if (orders.Count == 0)
        {
            return [];
        }

        var admissionIds = orders.Select(o => o.AdmissionId).Distinct().ToList();
        var consultants = await db.Set<Admission>().AsNoTracking().Where(a => admissionIds.Contains(a.Id))
            .Select(a => new { a.Id, a.ConsultantPractitionerId })
            .ToDictionaryAsync(a => a.Id, a => a.ConsultantPractitionerId, ct);
        var doctorIds = orders.Select(o => o.OrderedByPractitionerId).OfType<Guid>().Distinct().ToList();
        var names = await db.Set<Practitioner>().AsNoTracking().Where(p => doctorIds.Contains(p.Id))
            .Select(p => new { p.Id, p.DisplayName })
            .ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var me = currentUser.PractitionerId;
        var prescribes = me is not null && orders.Any(o => o.AwaitingCountersignature || o.StopPending) && await PrescribesAsync(ct);
        var now = time.GetUtcNow();
        return
        [
            .. orders.Select(o =>
            {
                var countersigner = o.OrderedByPractitionerId ?? consultants.GetValueOrDefault(o.AdmissionId);
                var canCountersign = prescribes && countersigner == me && (o.AwaitingCountersignature || o.StopPending);
                return ToDto(o, o.OrderedByPractitionerId is { } id ? names.GetValueOrDefault(id) : null, canCountersign, now);
            }),
        ];
    }

    internal static MedicationOrderDto ToDto(MedicationOrder o, string? orderedByName, bool canCountersign, DateTimeOffset now) => new(
        o.Id, o.PrescriptionId, o.DrugItemId, o.DrugName, o.GenericName, o.Strength, o.Dose, o.Route, o.Frequency, o.DurationDays, o.StartAt, o.EndAt,
        o.Instructions, o.IsControlledDrug, o.IsAsNeeded, o.Source, o.Status, o.LastDispensedAt, o.StoppedAt, o.StopReason, DoseSchedule.TimesOfDay(o.Frequency),
        o.IsHighAlert, o.Mode, o.IsEmergency, o.OrderedByPractitionerId, orderedByName, o.EnteredByName, o.AwaitingCountersignature, o.CountersignDueAt,
        o.CountersignOverdue(now), o.CountersignedAt, o.CountersignedByName, o.StopPending, o.StopPending ? o.StopRequestedAt : null,
        o.StopPending ? o.StopRequestedByName : null, o.StopPending ? o.StopRequestReason : null, o.StopCountersignOverdue(now), o.DosesHeld, canCountersign,
        o.Version);

    internal static MarEntryDto ToDto(MedicationAdministration m, DateTimeOffset now, TimeSpan grace, MedicationOrder? order) => new(
        m.Id, m.MedicationOrderId, m.DrugName, m.Dose, m.Route, m.ScheduledAt, m.Status, m.IsOverdue(now, grace), m.IsAdHoc, m.AdministeredAt,
        m.RecordedByName, m.Reason, m.BatchNumber, m.WitnessName, m.Notes, m.Version, MarRules.RequiresWitness(order),
        m.Status == MarStatus.Due ? HeldBecause(order) : null);

    /// <summary>Why the order's doses are held (the refusal's code), or null when a dose may be given.</summary>
    private static string? HeldBecause(MedicationOrder? order) => order?.CanGiveDose() is { IsFailure: true } held ? held.Error!.Code : null;

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

    /// <summary>Whether the signed-in user may sign prescriptions (<c>clinical.prescriptions.sign</c>): a prescriber.</summary>
    private async Task<bool> PrescribesAsync(CancellationToken ct) =>
        currentUser.UserId is { } userId && !currentUser.IsPatient && await permissions.HasPermissionAsync(userId, ClinicalPermissions.PrescriptionsSign, ct);

    /// <summary>
    /// Only the doctor who countersigns, from their own account (the practitioner linked to it) and as a prescriber: never
    /// the nurse who wrote the order or asked for the stop, and never another doctor.
    /// </summary>
    private async Task<Result> EnsureCountersignerAsync(Guid? doctorId, string what, CancellationToken ct)
    {
        if (doctorId is { } id && currentUser.PractitionerId == id && await PrescribesAsync(ct))
        {
            return Result.Success();
        }

        var name = doctorId is { } named
            ? await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == named).Select(p => p.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        return Error.Forbidden("medication.countersign_not_prescriber",
            $"Only {name ?? "the doctor named on the order"} countersigns {what}, signed in as themselves.");
    }

    /// <summary>Who countersigns a nurse's stop: the doctor the order is in the name of, or else the patient's consultant.</summary>
    private async Task<Guid?> StopCountersignerAsync(MedicationOrder order, CancellationToken ct) =>
        order.OrderedByPractitionerId
        ?? await db.Set<Admission>().AsNoTracking().Where(a => a.Id == order.AdmissionId).Select(a => a.ConsultantPractitionerId).FirstOrDefaultAsync(ct);

    /// <summary>Cancels the order's doses still due from <paramref name="from"/> on.</summary>
    private async Task CancelDueAsync(Guid orderId, DateTimeOffset from, string reason, CancellationToken ct)
    {
        var future = await db.Set<MedicationAdministration>()
            .Where(m => m.MedicationOrderId == orderId && m.Status == MarStatus.Due && m.ScheduledAt >= from)
            .ToListAsync(ct);
        future.ForEach(m => m.CancelIfDue(reason));
    }

    /// <summary>Tells the doctor, on every staff account linked to their practitioner record.</summary>
    private async Task NotifyDoctorAsync(Guid practitionerId, string title, string body, CancellationToken ct, string severity = "info")
    {
        var users = await db.Set<User>().AsNoTracking()
            .Where(u => u.PractitionerId == practitionerId && u.IsActive && u.Type == UserType.Staff)
            .Select(u => u.Id)
            .ToListAsync(ct);
        await notifications.NotifyUsersAsync(users, title, body, CountersignaturesLink, Category, severity, ct);
    }

    private AuditEntry OrderAudit(MedicationOrder order, string action, string summary, AuditSeverity severity) =>
        new(AuditCategories.Clinical, action, summary.Length <= 1000 ? summary : summary[..999] + "…")
        {
            PatientId = order.PatientId,
            EntityType = nameof(MedicationOrder),
            EntityId = order.Id.ToString(),
            Severity = severity,
            Details = new
            {
                order.AdmissionId,
                order.Mode,
                order.IsEmergency,
                order.IsControlledDrug,
                order.IsHighAlert,
                order.OrderedByPractitionerId,
                order.EnteredBy,
                order.CountersignDueAt,
                order.CountersignedBy,
                order.StopRequestedBy,
                order.StopCountersignDueAt,
            },
        };

    /// <summary>"Morphine 2 mg IV, STAT".</summary>
    private static string Words(MedicationOrder order) =>
        $"{order.DrugName} {order.Dose}{(order.Route is null ? "" : " " + order.Route)}, {order.Frequency}";

    private static string Local(DateTimeOffset at) => IndiaClock.ToLocal(at).ToString("dd-MM-yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string Late(TimeSpan by) => by.TotalHours >= 1 ? $"{(int)by.TotalHours} h {by.Minutes} min" : $"{Math.Max(1, (int)by.TotalMinutes)} min";

    private string Name() => currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";

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
