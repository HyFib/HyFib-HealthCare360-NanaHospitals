using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Surgery booking lifecycle (OT-01..03, OT-06): booking numbers, double-booking prevention under advisory locks,
/// state transitions (the WHO gates live in <see cref="SurgeryBooking"/>) and OT charges on completion.
/// </summary>
public sealed class SurgeryService(
    HospitalDbContext db,
    INumberSeries numbers,
    SpecialtyChargeService charges,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    IAuditLogger audit,
    INotificationService notifications,
    IWitnessConfirmations witnesses,
    TimeProvider time)
{
    /// <summary>
    /// The second doctor who agrees, at the operating surgeon's screen, that an emergency operation cannot wait for the
    /// patient's consent (<see cref="WaiveConsentAsync"/>): a doctor who may operate (<c>ot.surgery.perform</c>), never the surgeon.
    /// </summary>
    public static readonly WitnessPurpose ConsentWaiverWitness = new("ot.consent_waiver", SpecialtyCarePermissions.OtSurgery,
        "a second doctor's agreement that an emergency operation goes ahead without the patient's consent");

    public async Task<Result<SurgeryCreated>> CreateAsync(CreateSurgeryBookingRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var checks = await ValidateReferencesAsync(
            request.PatientId, request.SurgeonId, request.AnaesthetistId, request.AssistantIds, request.NurseId, request.ProcedureServiceItemId, ct);
        if (checks.IsFailure)
        {
            return checks.Error!;
        }

        var (admissionId, visitId) = await SpecialtyLookups.SurgeryEncounterAsync(db, request.PatientId, request.AdmissionId, request.VisitId, ct);
        var booking = new SurgeryBooking
        {
            FacilityId = facilityId,
            PatientId = request.PatientId,
            AdmissionId = admissionId,
            VisitId = visitId,
            Status = SurgeryStatus.Requested,
            PreferredDate = request.PreferredDate,
        };
        await ApplyPlanAsync(booking, request.ProcedureServiceItemId, request.ProcedureCode, request.ProcedureName, request.Laterality, request.SiteLevel,
            request.Diagnosis, request.SurgeonId, request.AssistantIds, request.AnaesthetistId, request.NurseId, request.AnaesthesiaType, request.Priority,
            request.Notes, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (request.TheatreId is { } theatreId)
        {
            var scheduled = await ScheduleCoreAsync(booking, theatreId, request.ScheduledStart!.Value, request.ScheduledEnd!.Value, ct);
            if (scheduled.IsFailure)
            {
                return scheduled.Error!;
            }
        }

        booking.BookingNumber = await numbers.NextAsync(SpecialtyCareNumberSeries.SurgeryBooking, facilityId, ct);
        db.Add(booking);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await realtime.OtChangedAsync(facilityId, booking.Id, ct);
        return new SurgeryCreated(booking.Id, booking.BookingNumber, booking.Status);
    }

    /// <summary>
    /// Edits the plan. Before the WHO Sign-in, as asked. After it, a change of side, level or procedure is made only by the
    /// operating surgeon or the anaesthetist, from their own sign-in, with a reason (<see cref="SiteChangeRule"/>): the Sign-in
    /// and Time-out done are voided and redone, the change is audited at Warning, and the theatre team is told. A change of
    /// anaesthesia technique or priority after it is made the same way (<see cref="TechniqueOrPriorityChangeRule"/>) and
    /// audited at Warning, without voiding the checklist; a consent to anaesthesia needed at the Sign-in stays needed. After
    /// the incision the plan is refused (maker-checker slice 10). Another operating surgeon ends a consent waived in an
    /// emergency (<see cref="SurgeryBooking.AssignSurgeon"/>).
    /// </summary>
    public async Task<Result> UpdatePlanAsync(Guid id, UpdateSurgeryPlanRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), id);
        }

        var before = booking.Site;
        var signedIn = booking.SignInCompletedAt is not null;
        var asked = new SurgicalSite(request.ProcedureServiceItemId, request.ProcedureName, request.Laterality, Clean(request.SiteLevel));
        var siteChange = SiteChangeRule.Check(booking.Status, signedIn, before, asked, booking.SurgeonId, booking.AnaesthetistId,
            currentUser.PractitionerId, request.ChangeReason);
        var editable = booking.CanChangePlan();
        if (editable.IsFailure && booking.Status != SurgeryStatus.InProgress)
        {
            return editable;
        }

        if (siteChange.IsFailure)
        {
            return siteChange;
        }

        if (editable.IsFailure)
        {
            return editable;
        }

        var (anaesthesiaBefore, priorityBefore) = (booking.AnaesthesiaType, booking.Priority);
        var careChange = TechniqueOrPriorityChangeRule.Check(signedIn, anaesthesiaBefore, request.AnaesthesiaType, priorityBefore, request.Priority,
            booking.SurgeonId, booking.AnaesthetistId, currentUser.PractitionerId, request.ChangeReason);
        if (careChange.IsFailure)
        {
            return careChange;
        }

        var checks = await ValidateReferencesAsync(
            booking.PatientId, request.SurgeonId, request.AnaesthetistId, request.AssistantIds, request.NurseId, request.ProcedureServiceItemId, ct);
        if (checks.IsFailure)
        {
            return checks;
        }

        db.SetExpectedVersion(booking, request.Version);
        var voids = signedIn && before.Differs(asked);
        booking.PreferredDate = request.PreferredDate;
        await ApplyPlanAsync(booking, request.ProcedureServiceItemId, request.ProcedureCode, request.ProcedureName, request.Laterality, request.SiteLevel,
            request.Diagnosis, request.SurgeonId, request.AssistantIds, request.AnaesthetistId, request.NurseId, request.AnaesthesiaType, request.Priority,
            request.Notes, ct);
        if (signedIn && (anaesthesiaBefore, priorityBefore) != (booking.AnaesthesiaType, booking.Priority))
        {
            var change = TechniqueOrPriorityChangeRule.Describe(anaesthesiaBefore, booking.AnaesthesiaType, priorityBefore, booking.Priority);
            var by = currentUser.Stamp(time.GetUtcNow());
            audit.Add(new AuditEntry(AuditCategories.Clinical, "surgery.plan_changed_after_sign_in",
                Fit($"Surgery {booking.BookingNumber} ({booking.Site.Words}): {change} after the WHO Sign-in, by {by.Name}: {request.ChangeReason!.Trim()}."
                    + (booking.AnaesthesiaConsentRequiredAtSignIn ? " The consent to anaesthesia needed at the Sign-in stays needed." : string.Empty), 1000))
            {
                PatientId = booking.PatientId,
                EntityType = nameof(SurgeryBooking),
                EntityId = booking.Id.ToString(),
                Severity = AuditSeverity.Warning,
                Details = new
                {
                    anaesthesia = new { before = anaesthesiaBefore, after = booking.AnaesthesiaType },
                    priority = new { before = priorityBefore, after = booking.Priority },
                    reason = request.ChangeReason!.Trim(),
                    anaesthesiaConsentStillNeeded = booking.AnaesthesiaConsentRequiredAtSignIn,
                },
            });
        }

        booking.ResetSiteMarkingIfMoved(before);
        var voided = voids ? booking.VoidChecklistForSiteChange(before, request.ChangeReason!, currentUser.Stamp(time.GetUtcNow())) : null;
        if (voided is not null)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "surgery.site_changed_after_sign_in",
                Fit($"Surgery {booking.BookingNumber}: {voided.Before} → {voided.After} after the WHO Sign-in, by {voided.ByName}: {voided.Reason}. "
                    + $"Voided: {string.Join(", ", voided.Phases.Select(SurgeryBooking.Describe))}; redo them before the incision.", 1000))
            {
                PatientId = booking.PatientId,
                EntityType = nameof(SurgeryBooking),
                EntityId = booking.Id.ToString(),
                Severity = AuditSeverity.Warning,
                Details = new { before = voided.Before, after = voided.After, reason = voided.Reason, phases = voided.Phases, voided = voided.Voided },
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (SurgeryScheduling.IsBlocking(booking.Status))
        {
            // The team changed on a booked slot: the new surgeon/anaesthetist must be free at that time too.
            var slotCheck = await RecheckSlotAsync(booking, ct);
            if (slotCheck.IsFailure)
            {
                return slotCheck;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (voided is not null)
        {
            await TellTheTeamAsync(booking, voided, ct);
        }

        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return Result.Success();
    }

    public async Task<Result> ScheduleAsync(Guid id, ScheduleSurgeryRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), id);
        }

        db.SetExpectedVersion(booking, request.Version);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var scheduled = await ScheduleCoreAsync(booking, request.TheatreId, request.ScheduledStart, request.ScheduledEnd, ct);
        if (scheduled.IsFailure)
        {
            return scheduled;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return Result.Success();
    }

    public Task<Result> ReceiveInPreOpAsync(Guid id, CancellationToken ct) =>
        TakeIntoTheatreAsync(id, b => Task.FromResult(b.ReceiveInPreOp(time.GetUtcNow())), ct);

    public Task<Result> PostponeAsync(Guid id, string reason, CancellationToken ct) => ChangeAsync(id, b => b.Postpone(reason), ct);

    public Task<Result> CancelAsync(Guid id, string reason, CancellationToken ct) => ChangeAsync(id, b => b.Cancel(reason), ct);

    /// <summary>
    /// Knife to skin: after the WHO Sign-in and Time-out, and only with the patient's consent to the operation on record, read
    /// as the incision is saved, or waived in an emergency (<see cref="SurgicalConsent"/>, <see cref="WaiveConsentAsync"/>).
    /// </summary>
    public Task<Result> StartAsync(Guid id, CancellationToken ct) =>
        TakeIntoTheatreAsync(id, async b => b.Start(time.GetUtcNow(), await SurgicalConsentRecords.ForBookingAsync(db, b, ct)), ct);

    /// <summary>
    /// The emergency doctrine: an emergency operation goes ahead without the patient's consent when it cannot wait for it.
    /// The operating surgeon waives it from their own sign-in, with the clinical reason, and a second doctor agrees at the
    /// surgeon's screen with their own user name and password (<see cref="ConsentWaiverWitness"/>), their confirmation spent
    /// in this save. Only for an emergency booking that has not started, while the consent is not on record and the
    /// patient's latest word is not a refusal or a withdrawal (<see cref="SurgeryBooking.WaiveConsent"/>). Audited at
    /// Critical; the booking names both doctors and the reason, and the consent gates (the pre-op item, the WHO Sign-in and
    /// Time-out, the incision) then stand on it while it stands.
    /// </summary>
    public async Task<Result> WaiveConsentAsync(Guid id, WaiveSurgicalConsentRequest request, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), id);
        }

        if (currentUser.UserId is null)
        {
            return Error.Forbidden("surgery.consent_waiver_surgeon", "Only the operating surgeon of this case waives consent, from their own sign-in.");
        }

        var consents = await SurgicalConsentRecords.ForBookingAsync(db, booking, ct);
        var witness = await witnesses.UseAsync(request.ConcurringDoctorConfirmationId, ConsentWaiverWitness.Key, booking.PatientId, nameof(SurgeryBooking), booking.Id, ct);
        if (witness.IsFailure)
        {
            return witness.Error!;
        }

        // The doctor who agrees is a doctor on record: their account is linked to an active doctor's practitioner record.
        var practitioner = await (from u in db.Set<User>().AsNoTracking()
                                  join p in db.Set<Practitioner>().AsNoTracking() on u.PractitionerId equals p.Id
                                  where u.Id == witness.Value.UserId
                                  select new { p.Id, p.Kind, p.IsActive }).FirstOrDefaultAsync(ct);
        var concurring = new ConcurringDoctor(witness.Value.UserId, witness.Value.Name, practitioner?.Id,
            practitioner is { IsActive: true } && PractitionerKinds.IsDoctor(practitioner.Kind));
        var missing = booking.Consent(consents).Missing;
        var waived = booking.WaiveConsent(request.Reason, currentUser.Stamp(time.GetUtcNow()), currentUser.PractitionerId, concurring, consents);
        if (waived.IsFailure)
        {
            return waived;
        }

        var waiver = booking.ConsentWaiver!;
        audit.Add(new AuditEntry(AuditCategories.Clinical, "surgery.consent_waived",
            Fit($"Surgery {booking.BookingNumber} ({booking.Site.Words}) goes ahead in an emergency without the patient's consent "
                + $"({string.Join("; ", missing.Select(SurgicalConsent.Describe))}): waived by the operating surgeon {waiver.SurgeonName}, "
                + $"{waiver.ConcurringName} agreeing at the surgeon's screen. Reason: {waiver.Reason}", 1000))
        {
            PatientId = booking.PatientId,
            EntityType = nameof(SurgeryBooking),
            EntityId = booking.Id.ToString(),
            Severity = AuditSeverity.Critical,
            Details = new
            {
                reason = waiver.Reason,
                surgeon = waiver.SurgeonUserId,
                concurringDoctor = waiver.ConcurringUserId,
                missing = missing.Select(m => new { m.Kind, m.State }).ToList(),
            },
        });
        await db.SaveChangesAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return Result.Success();
    }

    /// <summary>Completes the surgery (WHO Sign-out gate in the aggregate) and posts OT charges to the patient's bill.</summary>
    public async Task<Result> CompleteAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), id);
        }

        var result = booking.Complete(time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        var outcome = await PostChargesAsync(booking, ct);
        booking.RecordCharges(outcome.Status, outcome.Error);
        await db.SaveChangesAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return Result.Success();
    }

    /// <summary>Re-posts OT charges after a Billing failure (idempotent per charge line).</summary>
    public async Task<Result<ChargeOutcome>> RetryChargesAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), id);
        }

        if (booking.Status != SurgeryStatus.Completed)
        {
            return Error.Rule("surgery.not_completed", "Charges are posted when the surgery is completed.");
        }

        var outcome = await PostChargesAsync(booking, ct);
        booking.RecordCharges(outcome.Status, outcome.Error);
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    /// <summary>
    /// Takes the patient into the theatre (pre-op, or the table), which holds a discharge back (<see cref="TheatreDischargeHolds"/>),
    /// under the lock of the case's stay (<see cref="EncounterLocks"/>), held to the commit, as the discharge asks the theatre
    /// under it. A discharge saved at the same moment either commits first, and its stay's cases are then cancelled or given
    /// back before this reads the case again, or waits for this and finds the patient in the theatre.
    /// </summary>
    private async Task<Result> TakeIntoTheatreAsync(Guid id, Func<SurgeryBooking, Task<Result>> change, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == id, ct);
        Guid? locked = null;
        while (booking?.AdmissionId is { } stay && stay != locked)
        {
            // Read again under the lock: the stay may have ended, or the case moved, while the lock was awaited.
            await EncounterLocks.LockAsync(db, [stay], ct);
            locked = stay;
            await db.Entry(booking).ReloadAsync(ct);
        }

        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), id);
        }

        var result = await change(booking);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return Result.Success();
    }

    private async Task<Result> ChangeAsync(Guid id, Func<SurgeryBooking, Result> change, CancellationToken ct)
    {
        var booking = await db.Set<SurgeryBooking>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound(nameof(SurgeryBooking), id);
        }

        var result = change(booking);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await realtime.OtChangedAsync(booking.FacilityId, booking.Id, ct);
        return Result.Success();
    }

    /// <summary>Validates the slot, takes the scheduling locks and rejects any overlap. Must run inside a transaction.</summary>
    private async Task<Result> ScheduleCoreAsync(SurgeryBooking booking, Guid theatreId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var theatreActive = await db.Set<OperationTheatre>().AnyAsync(t => t.Id == theatreId && t.IsActive, ct);
        if (!theatreActive)
        {
            return Error.Field("theatreId", "Choose an active operation theatre.");
        }

        var result = booking.Schedule(theatreId, start, end, time.GetUtcNow());
        return result.IsFailure ? result : await RecheckSlotAsync(booking, ct);
    }

    private async Task<Result> RecheckSlotAsync(SurgeryBooking booking, CancellationToken ct)
    {
        if (booking.SurgeonId is null)
        {
            return Error.Field("surgeonId", "A scheduled surgery needs an operating surgeon.");
        }

        if (booking.RequiresAnaesthetist && booking.AnaesthetistId is null)
        {
            return Error.Field("anaesthetistId", "Choose the anaesthetist (required for every technique except local and topical anaesthesia).");
        }

        if (booking.ToSlot() is not { } slot)
        {
            return Result.Success();
        }

        await SchedulingLocks.AcquireAsync(db, [slot.TheatreId, slot.SurgeonId, slot.PatientId, slot.AnaesthetistId ?? Guid.Empty], ct);
        var conflicts = await FindConflictsAsync(slot, ct);
        return conflicts.Count == 0 ? Result.Success() : Error.Conflict("surgery.slot_conflict", SurgeryScheduling.Describe(conflicts), conflicts);
    }

    /// <summary>
    /// Candidate clashes across all facilities (a surgeon or patient cannot be in two branches at once); the pure
    /// rule in <see cref="SurgeryScheduling"/> decides which of them really conflict.
    /// </summary>
    private async Task<IReadOnlyList<SchedulingConflict>> FindConflictsAsync(SurgerySlot slot, CancellationToken ct)
    {
        var blocking = SurgeryScheduling.BlockingStatuses;
        var rows = await db.Set<SurgeryBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(b => b.Id != slot.BookingId && blocking.Contains(b.Status)
                        && b.TheatreId != null && b.SurgeonId != null && b.ScheduledStart != null && b.ScheduledEnd != null
                        && b.ScheduledStart < slot.End && b.ScheduledEnd > slot.Start
                        && (b.TheatreId == slot.TheatreId || b.SurgeonId == slot.SurgeonId || b.PatientId == slot.PatientId
                            || (slot.AnaesthetistId != null && b.AnaesthetistId == slot.AnaesthetistId)))
            .Select(b => new { b.Id, b.TheatreId, b.SurgeonId, b.PatientId, b.AnaesthetistId, b.ScheduledStart, b.ScheduledEnd })
            .ToListAsync(ct);

        return SurgeryScheduling.FindConflicts(slot, rows.Select(r =>
            new SurgerySlot(r.Id, r.TheatreId!.Value, r.SurgeonId!.Value, r.PatientId, r.AnaesthetistId, r.ScheduledStart!.Value, r.ScheduledEnd!.Value)));
    }

    private async Task<Result> ValidateReferencesAsync(
        Guid patientId, Guid? surgeonId, Guid? anaesthetistId, IReadOnlyList<Guid>? assistants, Guid? nurseId, Guid? procedureItemId, CancellationToken ct)
    {
        var patient = await SpecialtyLookups.EnsurePatientAsync(db, patientId, ct);
        if (patient.IsFailure)
        {
            return patient;
        }

        var team = new List<(string, Guid?)> { ("surgeonId", surgeonId), ("anaesthetistId", anaesthetistId) };
        team.AddRange((assistants ?? []).Select(a => ("assistantIds", (Guid?)a)));
        var practitioners = await SpecialtyLookups.EnsurePractitionersAsync(db, team, ct);
        if (practitioners.IsFailure)
        {
            return practitioners;
        }

        if (surgeonId is not null && surgeonId == anaesthetistId)
        {
            return Error.Field("anaesthetistId", "The anaesthetist must be a different doctor from the operating surgeon.");
        }

        // The OT register names the assisting nurse or OT in-charge with a nursing qualification and registration: a nurse or
        // an OT technician from the practitioner master, still in use.
        if (nurseId is { } nurse && !await db.Set<Practitioner>().AnyAsync(
                p => p.Id == nurse && p.IsActive && (p.Kind == PractitionerKind.Nurse || p.Kind == PractitionerKind.Technician), ct))
        {
            return Error.Field("nurseId", "Choose a nurse or OT technician who is in use.");
        }

        return procedureItemId is { } itemId && !await db.Set<ServiceItem>().AnyAsync(s => s.Id == itemId && s.IsActive, ct)
            ? Error.Field("procedureServiceItemId", "Choose an active procedure from the service catalogue.")
            : Result.Success();
    }

    private async Task ApplyPlanAsync(
        SurgeryBooking booking, Guid? procedureItemId, string? procedureCode, string procedureName, Laterality laterality, string? siteLevel, string? diagnosis,
        Guid? surgeonId, IReadOnlyList<Guid>? assistants, Guid? anaesthetistId, Guid? nurseId, AnaesthesiaType anaesthesia, SurgeryPriority priority,
        string? notes, CancellationToken ct)
    {
        booking.ProcedureServiceItemId = procedureItemId;
        booking.ProcedureCode = string.IsNullOrWhiteSpace(procedureCode)
            ? procedureItemId is { } itemId ? await db.Set<ServiceItem>().Where(s => s.Id == itemId).Select(s => s.Code).FirstOrDefaultAsync(ct) : null
            : procedureCode.Trim().ToUpperInvariant();
        booking.ProcedureName = procedureName.Trim();
        booking.Laterality = laterality;
        booking.SiteLevel = Clean(siteLevel);
        booking.Diagnosis = Clean(diagnosis);
        booking.AssignSurgeon(surgeonId);
        booking.AssistantIds = (assistants ?? []).Distinct().Where(a => a != surgeonId).ToList();
        booking.AnaesthetistId = anaesthetistId;
        booking.NurseId = nurseId;
        booking.AnaesthesiaType = anaesthesia;
        booking.Priority = priority;
        booking.Notes = Clean(notes);
    }

    private async Task<ChargeOutcome> PostChargesAsync(SurgeryBooking booking, CancellationToken ct)
    {
        var reference = booking.Id.ToString();
        ChargeLine Line(string key, string? code = null, Guid? itemId = null, decimal quantity = 1, string? description = null, Guid? practitionerId = null) =>
            new(ChargeSources.Ot, $"{reference}:{key}", booking.PatientId, booking.VisitId, booking.AdmissionId, code, itemId, quantity, null,
                description, practitionerId, booking.StartedAt);

        var lines = new List<ChargeLine>();
        if (booking.ProcedureServiceItemId is { } procedure)
        {
            lines.Add(Line("procedure", itemId: procedure, description: booking.ProcedureName, practitionerId: booking.SurgeonId));
        }

        var theatre = await db.Set<OperationTheatre>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == booking.TheatreId, ct);
        if (theatre is not null && booking.MinutesInTheatre is { } minutes)
        {
            lines.Add(Line("theatre-time", OtTariff.HourlyChargeCode(theatre.Type), quantity: OtTariff.BillableHours(minutes),
                description: $"{theatre.Name}: {(int)Math.Round(minutes)} min"));
        }

        if (booking.SurgeonId is { } surgeon)
        {
            lines.Add(Line("surgeon-fee", OtServiceCodes.SurgeonFee, practitionerId: surgeon));
        }

        if (booking.RequiresAnaesthetist && booking.AnaesthetistId is { } anaesthetist)
        {
            lines.Add(Line("anaesthetist-fee", OtServiceCodes.AnaesthetistFee, practitionerId: anaesthetist));
        }

        return await charges.PostAsync(lines, ct);
    }

    /// <summary>
    /// Tells the theatre team (the surgeon, assistants, anaesthetist and assisting nurse with an account), other than the one who
    /// changed it, that the side or procedure changed after the Sign-in and the checklist is to be redone.
    /// </summary>
    private async Task TellTheTeamAsync(SurgeryBooking booking, ChecklistVoid voided, CancellationToken ct)
    {
        var team = new List<Guid?> { booking.SurgeonId, booking.AnaesthetistId, booking.NurseId };
        team.AddRange(booking.AssistantIds.Select(a => (Guid?)a));
        var practitioners = team.OfType<Guid>().Distinct().ToList();
        var users = await db.Set<User>().AsNoTracking()
            .Where(u => u.PractitionerId != null && practitioners.Contains(u.PractitionerId.Value) && u.IsActive && u.Id != currentUser.UserId)
            .Select(u => u.Id)
            .ToListAsync(ct);
        if (users.Count == 0)
        {
            return;
        }

        await notifications.NotifyUsersAsync(users, $"Surgery {booking.BookingNumber}: side or procedure changed, redo the WHO checklist",
            $"{voided.Before} → {voided.After}, by {voided.ByName}: {voided.Reason}. The {string.Join(" and ", voided.Phases.Select(SurgeryBooking.Describe))} must be done again before the incision.",
            $"/ot/bookings/{booking.Id}", "ot", "warning", ct);
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
