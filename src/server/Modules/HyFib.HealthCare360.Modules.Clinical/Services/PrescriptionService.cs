using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// E-prescribing (ERX-01..06): drafts, safety-checked signing (blocking alerts need an audited override reason,
/// warnings an acknowledgement, Schedule H1/X medicines an explicit confirmation and a register entry), cancellation,
/// repeat prescriptions and authorised refills. Signing raises <see cref="PrescriptionSigned"/>, which queues the
/// prescription at the OP or IP pharmacy.
/// </summary>
public sealed class PrescriptionService(
    HospitalDbContext db,
    INumberSeries numbers,
    ClinicalAccess access,
    PrescriptionSafetyService safety,
    ICurrentUser currentUser,
    IPermissionService permissions,
    IAuditLogger audit,
    IRealtimePublisher realtime,
    INotificationService notifications,
    PrescriptionSignatureSettings signatureSettings,
    PrescriptionFinalCopies finalCopies,
    IEnumerable<ITeleconsultModes> teleconsultModes,
    TimeProvider time)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<Result<PrescriptionDto>> CreateAsync(CreatePrescriptionRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var allowed = await access.EnsureCanRecordAsync(request.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        if ((request.PractitionerId ?? currentUser.PractitionerId) is not { } practitionerId
            || !await db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId && p.IsActive, ct))
        {
            return Error.Rule("practitioner.required", "Prescriptions must be issued by an active doctor linked to your account.");
        }

        var visitId = request.VisitId;
        var admissionId = request.AdmissionId;
        if (request.EncounterId is { } encounterId)
        {
            var encounter = await db.Set<Encounter>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == encounterId, ct);
            if (encounter is null || encounter.PatientId != request.PatientId)
            {
                return Error.Field("encounterId", "This encounter does not belong to the patient.");
            }

            visitId ??= encounter.VisitId;
            admissionId ??= encounter.AdmissionId;
        }

        if (visitId is { } vid && !await db.Set<Visit>().AnyAsync(v => v.Id == vid && v.PatientId == request.PatientId, ct))
        {
            return Error.Field("visitId", "This visit does not belong to the patient.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var prescription = new Prescription
        {
            FacilityId = facilityId,
            PrescriptionNumber = await numbers.NextAsync(ClinicalNumberSeries.Prescription, facilityId, ct),
            PatientId = request.PatientId,
            EncounterId = request.EncounterId,
            VisitId = visitId,
            AdmissionId = admissionId,
            PractitionerId = practitionerId,
            IsInpatient = request.IsInpatient || admissionId is not null,
            PrescribedAt = time.GetUtcNow(),
            Notes = Clean(request.Notes),
            Advice = Clean(request.Advice),
            RefillsAllowed = request.RefillsAllowed,
        };
        prescription.ReplaceItems(await FromDrugMasterAsync(request.Items.Select(ToItem), ct));
        db.Add(prescription);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ToDtoAsync(prescription, ct);
    }

    public async Task<Result<PrescriptionDto>> UpdateAsync(Guid id, UpdatePrescriptionRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var prescription = loaded.Value;
        db.SetExpectedVersion(prescription, request.Version);
        var replaced = prescription.ReplaceItems(await FromDrugMasterAsync(request.Items.Select(ToItem), ct));
        if (replaced.IsFailure)
        {
            return replaced.Error!;
        }

        prescription.Notes = Clean(request.Notes);
        prescription.Advice = Clean(request.Advice);
        prescription.RefillsAllowed = request.RefillsAllowed;
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(prescription, ct);
    }

    /// <summary>Dry run of the signing checks so the UI can show every alert before the doctor signs.</summary>
    public async Task<Result<SafetyCheckResponse>> CheckAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        await FromDrugMasterAsync(loaded.Value.Items, ct);
        var result = await safety.CheckAsync(loaded.Value, ct);
        return new SafetyCheckResponse(result.IsBlocked, result.Warnings.Count > 0, loaded.Value.HasControlledDrugs, result.Alerts);
    }

    public async Task<Result<SignPrescriptionResult>> SignAsync(Guid id, SignPrescriptionRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var prescription = loaded.Value;
        if (currentUser.PractitionerId != prescription.PractitionerId)
        {
            return Error.Forbidden("prescription.not_prescriber", "Only the prescribing doctor can sign this prescription.");
        }

        db.SetExpectedVersion(prescription, request.Version);
        var signed = await SignCoreAsync(prescription, request.OverrideReason, request.AcknowledgeWarnings, request.ConfirmControlledDrugs, ct);
        if (signed.IsFailure)
        {
            return signed.Error!;
        }

        await QueueSignedNoticeAsync(prescription, ct);
        await db.SaveChangesAsync(ct);
        await PublishSignedAsync(prescription, ct);
        return new SignPrescriptionResult(await ToDtoAsync(prescription, ct), signed.Value);
    }

    public async Task<Result<PrescriptionDto>> CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var prescription = loaded.Value;
        var wasSigned = prescription.Status == PrescriptionStatus.Signed;
        var cancelled = prescription.Cancel(reason, time.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        await db.SaveChangesAsync(ct);
        if (wasSigned)
        {
            await realtime.PublishAsync(Topics.Pharmacy(prescription.FacilityId), "pharmacy.prescription.cancelled", new { prescription.Id }, ct);
        }

        return await ToDtoAsync(prescription, ct);
    }

    /// <summary>Repeat prescription (ERX-05): a new draft with the same medicines, for the current doctor to review and sign.</summary>
    public async Task<Result<PrescriptionDto>> RepeatAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var source = loaded.Value;
        return await CreateAsync(new CreatePrescriptionRequest(
            source.PatientId, null, null, null, source.IsInpatient, currentUser.PractitionerId ?? source.PractitionerId, source.Notes, source.Advice,
            source.RefillsAllowed, source.Items.OrderBy(i => i.Sequence).Select(ToRequest).ToList()), ct);
    }

    /// <summary>
    /// Authorised refill (ERX-05): consumes one refill of a signed prescription and issues it as a new signed
    /// prescription for the pharmacy, in the original doctor's name and registration, recording who refilled it. Safety
    /// checks run again because allergies or other medicines may have changed. A prescription with a controlled drug
    /// (Schedule H1 or X, or NDPS, as the drug master says now) is not refilled: the doctor writes a new one. A blocking
    /// alert found at refill is overridden only by the doctor who wrote the prescription, signed in as themselves; anyone
    /// else's refill is refused and the doctor is told (another doctor repeats it as their own prescription instead).
    /// </summary>
    public async Task<Result<SignPrescriptionResult>> RefillAsync(Guid id, RefillPrescriptionRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var source = loaded.Value;

        // A refill is issued as it stands, so a medicine typed in without its generic name, before a prescription had to
        // name it, can't be named here: the doctor repeats the prescription as a draft and names it there.
        var items = await FromDrugMasterAsync(source.Items.OrderBy(i => i.Sequence).Select(i => ToItem(ToRequest(i))), ct);
        if (items.Where(i => i.IsControlledDrug).Select(i => i.DrugName).ToList() is { Count: > 0 } controlled)
        {
            return Error.Rule("prescription.refill_controlled",
                $"Prescription {source.PrescriptionNumber} has a controlled drug ({string.Join(", ", controlled)}: Schedule H1 or X, or NDPS), "
                + "which is not repeated on a refill. The doctor writes a new prescription for it.");
        }

        var used = source.UseRefill();
        if (used.IsFailure)
        {
            return used.Error!;
        }

        if (Prescription.WithoutGenericName(items) is { } unnamed)
        {
            return Error.Rule("prescription.generic_missing",
                $"{unnamed.DrugName} has no generic name on prescription {source.PrescriptionNumber}, and a refill can't be edited. "
                + "Repeat the prescription as a new draft, enter the generic name and sign that.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var refill = new Prescription
        {
            FacilityId = currentUser.FacilityId ?? source.FacilityId,
            PatientId = source.PatientId,
            PractitionerId = source.PractitionerId,
            IsInpatient = source.IsInpatient,
            PrescribedAt = time.GetUtcNow(),
            Notes = source.Notes,
            Advice = source.Advice,
            RefillOfPrescriptionId = source.Id,
            RefilledByName = Fit(currentUser.DisplayName ?? currentUser.UserName, 200),
        };
        refill.PrescriptionNumber = await numbers.NextAsync(ClinicalNumberSeries.Prescription, refill.FacilityId, ct);
        refill.ReplaceItems(items);

        // The doctor who wrote it may override a hard stop at refill, as at signing; nobody else does so in their name.
        var byPrescriber = currentUser.PractitionerId == source.PractitionerId && currentUser.UserId is { } userId
                           && await permissions.HasPermissionAsync(userId, ClinicalPermissions.PrescriptionsSign, ct);
        var signed = await SignCoreAsync(refill, request.OverrideReason, request.AcknowledgeWarnings, request.ConfirmControlledDrugs, ct, overrideAllowed: byPrescriber);
        if (signed.IsFailure)
        {
            if (signed.Error!.Code == RefillNeedsPrescriberCode)
            {
                await SendBackToPrescriberAsync(source, signed.Error.Data as IReadOnlyList<SafetyAlert>, ct);
            }

            return signed.Error!;
        }

        db.Add(refill);
        audit.Add(new AuditEntry(AuditCategories.Clinical, "prescription.refilled",
            $"{refill.PrescriptionNumber} issued as a refill of {source.PrescriptionNumber} by {refill.RefilledByName}, in the name of the doctor who wrote it")
        {
            PatientId = refill.PatientId,
            EntityType = nameof(Prescription),
            EntityId = refill.Id.ToString(),
            Details = new { refillOf = source.Id, refilledBy = currentUser.UserId, prescriber = source.PractitionerId, byPrescriber },
        });
        await QueueSignedNoticeAsync(refill, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PublishSignedAsync(refill, ct);
        return new SignPrescriptionResult(await ToDtoAsync(refill, ct), signed.Value);
    }

    /// <summary>A refill refused because its safety check blocks it and only the doctor who wrote the prescription may override that.</summary>
    public const string RefillNeedsPrescriberCode = "prescription.refill_needs_prescriber";

    /// <summary>Tells the doctor who wrote the prescription that a refill of it waits for their decision on a blocking alert.</summary>
    private async Task SendBackToPrescriberAsync(Prescription source, IReadOnlyList<SafetyAlert>? alerts, CancellationToken ct)
    {
        var doctors = await db.Set<User>().AsNoTracking()
            .Where(u => u.PractitionerId == source.PractitionerId && u.IsActive && u.Type == UserType.Staff)
            .Select(u => u.Id)
            .ToListAsync(ct);
        var found = alerts is null ? "" : " " + string.Join(" ", alerts.Where(a => a.IsBlocking).Select(a => a.Message).Take(3));
        await notifications.NotifyUsersAsync(doctors, "Refill held for your decision",
            $"{currentUser.DisplayName ?? currentUser.UserName} could not refill {source.PrescriptionNumber}: its safety check blocks it, and only you may override that.{found}",
            $"/emr/prescriptions/{source.Id}", "prescriptions", "warning", ct);
        // Written in its own transaction: the refill's is rolled back.
        await audit.WriteAsync(new AuditEntry(AuditCategories.Clinical, "prescription.refill_blocked",
            $"Refill of {source.PrescriptionNumber} by {currentUser.DisplayName ?? currentUser.UserName} refused: a blocking safety alert, sent back to the prescriber")
        {
            PatientId = source.PatientId,
            EntityType = nameof(Prescription),
            EntityId = source.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = alerts?.Where(a => a.IsBlocking).ToList(),
        }, ct);
    }

    public async Task<Result<Prescription>> LoadAsync(Guid id, CancellationToken ct)
    {
        var prescription = await db.Set<Prescription>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (prescription is null)
        {
            return Error.NotFound(nameof(Prescription), id);
        }

        var allowed = await access.EnsureCanOpenAsync(prescription.PatientId, ct);
        return allowed.IsFailure ? allowed.Error! : Result<Prescription>.Ok(prescription);
    }

    public async Task<PrescriptionDto> ToDtoAsync(Prescription p, CancellationToken ct)
    {
        var names = await PrintQueries.PractitionerNamesAsync(db, [p.PractitionerId], ct);
        var finalCopy = p.Status == PrescriptionStatus.Draft || (await finalCopies.CheckAsync(p, ct)).FinalCopyAvailable;
        return ToDto(p, names.GetValueOrDefault(p.PractitionerId), finalCopy);
    }

    public static PrescriptionDto ToDto(Prescription p, string? practitionerName, bool finalCopyAvailable = true) => new(
        p.Id, p.PrescriptionNumber, p.PatientId, p.EncounterId, p.VisitId, p.AdmissionId, p.PractitionerId, practitionerName, p.IsInpatient, p.Status,
        p.PrescribedAt, p.SignedAt, p.Notes, p.Advice, p.RefillsAllowed, p.RefillsRemaining, p.RefillOfPrescriptionId, p.SafetyOverrideReason,
        p.CancelledAt, p.CancelReason,
        p.Items.OrderBy(i => i.Sequence).Select(i => new PrescriptionItemDto(i.Id, i.Sequence, i.DrugItemId, i.DrugName, i.GenericName, i.Strength,
            i.DosageForm, i.Route, i.Dose, i.Frequency, i.CustomFrequency, i.FrequencyText, i.DosesPerDay, i.DurationDays, i.Quantity, i.Instructions,
            i.IsControlledDrug, i.ControlledSchedule)).ToList(),
        p.Version,
        p.IsDigitallySigned,
        p.DigitallySignedAt,
        p.SignerName,
        finalCopyAvailable,
        p.SignedByUserId,
        p.RefilledByName);

    /// <summary>
    /// The refusal of a prescription written in a teleconsultation that prescribes a medicine its list keeps out of it
    /// (Telemedicine Practice Guidelines 2020, <see cref="TelemedicinePrescribing"/>); null when it is not written in one, or
    /// all its medicines may be.
    /// </summary>
    private async Task<Error?> TelemedicineRefusalAsync(Prescription prescription, CancellationToken ct)
    {
        // A teleconsultation is a telehealth encounter, or anything written on a telehealth visit: the prescription's own
        // visit, or its encounter's.
        var encounter = prescription.EncounterId is { } encounterId
            ? await db.Set<Encounter>().AsNoTracking().Where(e => e.Id == encounterId).Select(e => new { e.Type, e.VisitId }).FirstOrDefaultAsync(ct)
            : null;
        Guid?[] visitIds = [prescription.VisitId, encounter?.VisitId];
        var teleVisitId = await db.Set<Visit>().AsNoTracking()
            .Where(v => visitIds.Contains(v.Id) && v.Type == VisitType.Telehealth)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(ct);
        if (encounter?.Type != EncounterType.Telehealth && teleVisitId is null)
        {
            return null;
        }

        var drugIds = prescription.Items.Where(i => i.DrugItemId is not null).Select(i => i.DrugItemId!.Value).Distinct().ToList();
        var master = await db.Set<Item>().AsNoTracking().Where(i => drugIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Schedule, i.IsControlled, i.TelemedicineList }).ToDictionaryAsync(i => i.Id, ct);
        // In the order the lines were written, as the doctor reads the refusal.
        var medicines = prescription.Items.OrderBy(i => i.Sequence).Select(i =>
                i.DrugItemId is { } id && master.TryGetValue(id, out var drug)
                    ? new TelemedicinePrescribing.Medicine(i.DrugName, drug.Schedule, drug.IsControlled, drug.TelemedicineList)
                    // A medicine typed in, not from the drug master: what the line itself says, on no list. A controlled line may
                    // be an NDPS drug, which the line cannot tell from Schedule H1, so it counts as one.
                    : new TelemedicinePrescribing.Medicine(i.DrugName, i.ControlledSchedule == "X" ? DrugSchedule.X : DrugSchedule.None, i.IsControlledDrug))
            .ToList();

        // Only what List O leaves out needs the consultation's facts.
        if (medicines.All(m => m.Effective == TelemedicineList.O))
        {
            return null;
        }

        var consult = new TelemedicinePrescribing.Consult(
            teleVisitId is { } visitId && await HeldByVideoAsync(visitId, ct),
            await IsFollowUpAsync(prescription, ct));
        var refused = TelemedicinePrescribing.Refused(medicines, consult);
        return refused.Count == 0 ? null : TelemedicinePrescribing.Refusal(refused, consult);
    }

    /// <summary>Whether the doctor saw the patient by video on the telehealth visit, as the module holding the calls says.</summary>
    private async Task<bool> HeldByVideoAsync(Guid visitId, CancellationToken ct)
    {
        foreach (var modes in teleconsultModes)
        {
            if (await modes.HeldByVideoAsync(visitId, ct))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the teleconsultation is a follow-up (TPG 2020, section 3.4): the prescribing doctor saw the patient in person
    /// in the last six months, and a diagnosis recorded then is of the same ICD-10 category (the condition) as one recorded
    /// in this teleconsultation. A differential is not the condition, and a diagnosis with no ICD-10 code cannot be compared.
    /// </summary>
    private async Task<bool> IsFollowUpAsync(Prescription prescription, CancellationToken ct)
    {
        var teleEncounters = db.Set<Encounter>().AsNoTracking()
            .Where(e => e.Id == prescription.EncounterId || (prescription.VisitId != null && e.VisitId == prescription.VisitId))
            .Select(e => e.Id);
        var codes = await db.Set<Diagnosis>().AsNoTracking()
            .Where(d => d.PatientId == prescription.PatientId && d.EncounterId != null && teleEncounters.Contains(d.EncounterId.Value)
                        && d.Type != DiagnosisType.Differential && d.IcdCode != null)
            .Select(d => d.IcdCode!)
            .ToListAsync(ct);
        var conditions = codes.Select(IcdCategory).Where(c => c.Length > 0).Distinct().ToList();
        if (conditions.Count == 0)
        {
            return false;
        }

        var now = time.GetUtcNow();
        var since = now.AddMonths(-TelemedicinePrescribing.FollowUpMonths);
        var seenInPerson = db.Set<Encounter>().AsNoTracking()
            .Where(e => e.PatientId == prescription.PatientId && e.PractitionerId == prescription.PractitionerId
                        && e.Type != EncounterType.Telehealth && e.Type != EncounterType.HealthCheckup
                        && e.StartedAt >= since && e.StartedAt <= now)
            .Select(e => e.Id);
        var earlier = await db.Set<Diagnosis>().AsNoTracking()
            .Where(d => d.PatientId == prescription.PatientId && d.EncounterId != null && seenInPerson.Contains(d.EncounterId.Value)
                        && d.Type != DiagnosisType.Differential && d.IcdCode != null)
            .Select(d => d.IcdCode!)
            .ToListAsync(ct);
        return earlier.Select(IcdCategory).Any(conditions.Contains);
    }

    /// <summary>An ICD-10 code's category, its first three characters ("E11.9" is E11), which names the condition.</summary>
    private static string IcdCategory(string code)
    {
        var trimmed = code.Trim().ToUpperInvariant();
        return trimmed.Length < 3 ? string.Empty : trimmed[..3];
    }

    private async Task<Result<IReadOnlyList<SafetyAlert>>> SignCoreAsync(Prescription prescription, string? overrideReason, bool acknowledgeWarnings,
        bool confirmControlledDrugs, CancellationToken ct, bool overrideAllowed = true)
    {
        // A prescription carries the prescriber's registration number (Indian Medical Council (Professional Conduct,
        // Etiquette and Ethics) Regulations 2002, 1.4.1): one whose record has none is not signed.
        // The number and council are kept on the prescription as they are now, for every copy of it.
        var registration = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.Id == prescription.PractitionerId)
            .Select(p => new { Number = p.RegistrationNumber, Council = p.RegistrationCouncil })
            .FirstOrDefaultAsync(ct);
        if (registration is null || string.IsNullOrWhiteSpace(registration.Number))
        {
            return Error.Rule("prescription.registration_missing",
                "The prescriber's medical council registration number is not on their practitioner record, and a prescription must carry it. "
                + "Ask the administrator to add it under Practitioners, then sign.");
        }

        // Again at signing: a draft saved before the master decided, or whose drug the master has since reclassified.
        await FromDrugMasterAsync(prescription.Items, ct);
        if (await TelemedicineRefusalAsync(prescription, ct) is { } telemedicine)
        {
            return telemedicine;
        }

        var check = await safety.CheckAsync(prescription, ct);
        if (check.IsBlocked && !overrideAllowed)
        {
            var doctor = (await PrintQueries.PractitionerNamesAsync(db, [prescription.PractitionerId], ct)).GetValueOrDefault(prescription.PractitionerId) ?? "the doctor who wrote it";
            return new Error(RefillNeedsPrescriberCode,
                $"The safety check blocks this refill, and only {doctor}, who wrote the prescription, may override that. It has been sent back to them; "
                + "another doctor may repeat the prescription as their own and sign it.")
            {
                Data = check.Alerts,
            };
        }

        if (check.IsBlocked && string.IsNullOrWhiteSpace(overrideReason))
        {
            return new Error("prescription.safety_block",
                "Safety checks found problems that block signing. Change the prescription or give a clinical override reason.")
            {
                Data = check.Alerts,
            };
        }

        if (check.Warnings.Count > 0 && !acknowledgeWarnings)
        {
            return new Error("prescription.warnings_unacknowledged", "Review and acknowledge the safety warnings before signing.") { Data = check.Alerts };
        }

        var signature = new PrescriptionSignature(currentUser.UserId, NewVerificationToken(), check.IsBlocked ? overrideReason : null,
            JsonSerializer.Serialize(check.Alerts, JsonOptions), confirmControlledDrugs, registration.Number.Trim(),
            string.IsNullOrWhiteSpace(registration.Council) ? null : registration.Council.Trim(),
            check.IsBlocked ? OverriddenAlerts(check.BlockingAlerts) : null);
        var signed = prescription.Sign(signature, time.GetUtcNow());
        if (signed.IsFailure)
        {
            return signed.Error!;
        }

        foreach (var item in prescription.Items.Where(i => i.IsControlledDrug))
        {
            db.Add(new ControlledDrugRegisterEntry
            {
                FacilityId = prescription.FacilityId,
                PrescriptionId = prescription.Id,
                PrescriptionNumber = prescription.PrescriptionNumber,
                PrescriptionItemId = item.Id,
                PatientId = prescription.PatientId,
                PractitionerId = prescription.PractitionerId,
                DrugName = item.DrugName,
                GenericName = item.GenericName,
                Strength = item.Strength,
                Schedule = item.ControlledSchedule,
                Quantity = item.Quantity,
                PrescribedAt = prescription.SignedAt!.Value,
                ConfirmedByUserId = currentUser.UserId,
            });
        }

        if (check.IsBlocked)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "prescription.safety_override", $"{prescription.PrescriptionNumber}: {overrideReason!.Trim()}")
            {
                PatientId = prescription.PatientId,
                EntityType = nameof(Prescription),
                EntityId = prescription.Id.ToString(),
                Severity = AuditSeverity.Critical,
                Details = check.BlockingAlerts,
            });
        }

        if (prescription.HasControlledDrugs)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "prescription.controlled_confirmed", $"{prescription.PrescriptionNumber}: Schedule H1/X medicines confirmed")
            {
                PatientId = prescription.PatientId,
                EntityType = nameof(Prescription),
                EntityId = prescription.Id.ToString(),
                Severity = AuditSeverity.Warning,
            });
        }

        return Result<IReadOnlyList<SafetyAlert>>.Ok(check.Alerts);
    }

    /// <summary>
    /// The blocking alerts each prescribed line was signed over, in words ("ALLERGY: …; Maximum dose …"), keyed by the line:
    /// the pharmacy shows them on the line and holds it until a pharmacist acknowledges them.
    /// </summary>
    internal static Dictionary<Guid, string> OverriddenAlerts(IEnumerable<SafetyAlert> blocking)
    {
        var lines = new Dictionary<Guid, List<string>>();
        foreach (var alert in blocking)
        {
            foreach (var itemId in alert.ItemIds.Distinct())
            {
                if (!lines.TryGetValue(itemId, out var messages))
                {
                    lines[itemId] = messages = [];
                }

                if (!messages.Contains(alert.Message))
                {
                    messages.Add(alert.Message);
                }
            }
        }

        return lines.ToDictionary(l => l.Key, l => Fit(string.Join(" ", l.Value), 1000)!);
    }

    /// <summary>Refreshes pharmacy queue screens (ids only; the pharmacy reloads with its own permissions).</summary>
    private Task PublishSignedAsync(Prescription prescription, CancellationToken ct) =>
        realtime.PublishAsync(Topics.Pharmacy(prescription.FacilityId), "pharmacy.prescription.signed", new { prescription.Id, prescription.IsInpatient }, ct);

    // Where the facility requires the doctor's DSC (ERX-04), the message is held: the patient is told once it is signed
    // with it (PrescriptionDigitalSignatureService.AttachAsync), or once the facility no longer requires it
    // (PrescriptionNoticeRelease).
    private async Task QueueSignedNoticeAsync(Prescription prescription, CancellationToken ct)
    {
        if (await signatureSettings.PolicyAsync(prescription.FacilityId, ct) == PrescriptionSignaturePolicy.Required)
        {
            prescription.HoldPatientNotice(time.GetUtcNow());
        }
        else
        {
            await QueuePatientNoticeAsync(prescription, ct);
        }
    }

    /// <summary>
    /// Tells OP patients the prescription is on the portal (outbox message, committed with the signature). The message
    /// names the prescription's PDF: WhatsApp sends it in the template's document header when the template has one, and
    /// only to a verified number (<see cref="VerifiedNumbers"/>); to a number nobody has verified the message goes alone.
    /// Once per prescription (<see cref="Prescription.PatientNotifiedAt"/>): a prescription the patient was told about
    /// at signing is not announced again when the doctor signs it with their DSC. A held message
    /// (<see cref="Prescription.PatientNoticeHeldAt"/>) is let go here, sent or not. The message is the prescription's
    /// facility's (its templates, name and WhatsApp account), not the caller's: the job that sends held messages has no
    /// signed-in user, and a doctor may sign with the DSC after switching facility.
    /// </summary>
    internal async Task QueuePatientNoticeAsync(Prescription prescription, CancellationToken ct)
    {
        if (prescription.IsInpatient || prescription.PatientNotifiedAt is not null)
        {
            return;
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == prescription.PatientId)
            .Select(p => new { p.Phone, p.Email, p.FullName, p.PreferredLanguage }).FirstOrDefaultAsync(ct);
        // A record in a protected category is not sent to a number another patient shares (SharedNumbers): the
        // prescription's PDF would tell whoever holds the phone. The patient takes the printed copy.
        if (patient?.Phone is null || await SharedNumbers.HiddenFromNumberAsync(db, prescription.PatientId, ct))
        {
            prescription.ReleasePatientNotice();
            return;
        }

        var doctor = (await PrintQueries.PractitionerNamesAsync(db, [prescription.PractitionerId], ct)).GetValueOrDefault(prescription.PractitionerId) ?? "your doctor";
        var numberVerified = await VerifiedNumbers.CarriesDocumentsAsync(db, prescription.PatientId, ct);
        notifications.Queue("prescription.shared",
            NotificationRecipient.Patient(patient.Phone, patient.Email, prescription.PatientId, patient.FullName, patient.PreferredLanguage),
            new Dictionary<string, string>
            {
                ["name"] = patient.FullName,
                ["doctor"] = doctor,
                ["date"] = ClinicalClock.LocalDate(prescription.SignedAt!.Value).ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture),
            },
            [NotificationChannel.WhatsApp],
            TimeSpan.FromDays(2),
            prescription.FacilityId,
            document: VerifiedNumbers.ForNumber(new NotificationDocument(PrescriptionDocumentSource.DocumentKind, prescription.Id), numberVerified));
        prescription.MarkPatientNotified(time.GetUtcNow());
    }

    /// <summary>
    /// Whether a line is a controlled drug (Schedule H1/X, NDPS: the prescriber's confirmation and the controlled-drug
    /// register) is the drug master's to say when the line names one of its drugs, as the drug picker shows it; what the
    /// client sent can only add the care, never take it away. So are the generic name the prescription names the medicine
    /// by and the dosage form the route check reads (an injection stays one), when the master has them. A line typed in
    /// keeps what it says.
    /// </summary>
    private async Task<List<PrescriptionItem>> FromDrugMasterAsync(IEnumerable<PrescriptionItem> lines, CancellationToken ct)
    {
        var items = lines.ToList();
        var drugIds = items.Where(i => i.DrugItemId is not null).Select(i => i.DrugItemId!.Value).Distinct().ToList();
        if (drugIds.Count == 0)
        {
            return items;
        }

        var master = await db.Set<Item>().AsNoTracking().Where(i => drugIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Schedule, i.IsControlled, i.GenericName, i.DosageForm }).ToDictionaryAsync(i => i.Id, ct);
        foreach (var item in items)
        {
            if (item.DrugItemId is not { } id || !master.TryGetValue(id, out var drug))
            {
                continue;
            }

            if (Item.NeedsRegister(drug.Schedule, drug.IsControlled))
            {
                item.IsControlledDrug = true;
                item.ControlledSchedule = drug.Schedule == DrugSchedule.X ? "X" : "H1";
            }

            if (!string.IsNullOrWhiteSpace(drug.GenericName))
            {
                item.GenericName = drug.GenericName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(drug.DosageForm))
            {
                item.DosageForm = drug.DosageForm.Trim();
            }
        }

        return items;
    }

    internal static PrescriptionItem ToItem(PrescriptionItemRequest r)
    {
        var dosesPerDay = DoseCalculator.DosesPerDay(r.Frequency, r.CustomFrequency, r.DosesPerDay);
        var quantity = r.Quantity is > 0
            ? r.Quantity.Value
            : DoseCalculator.QuantityToDispense(r.Dose, r.Strength, r.Frequency, r.CustomFrequency, r.DosesPerDay, r.DurationDays) ?? 0m;
        return new PrescriptionItem
        {
            DrugItemId = r.DrugItemId,
            DrugName = r.DrugName.Trim(),
            GenericName = Clean(r.GenericName),
            Strength = Clean(r.Strength),
            DosageForm = Clean(r.DosageForm),
            Route = Clean(r.Route),
            Dose = r.Dose.Trim(),
            Frequency = r.Frequency,
            CustomFrequency = r.Frequency == DoseFrequency.Custom ? Clean(r.CustomFrequency) : null,
            DosesPerDay = dosesPerDay is { } perDay ? Math.Round(perDay, 4) : null,
            DurationDays = r.Frequency == DoseFrequency.STAT ? 1 : r.DurationDays,
            Quantity = quantity,
            Instructions = Clean(r.Instructions),
            IsControlledDrug = r.IsControlledDrug,
            ControlledSchedule = r.IsControlledDrug ? Clean(r.ControlledSchedule)?.ToUpperInvariant() : null,
        };
    }

    private static PrescriptionItemRequest ToRequest(PrescriptionItem i) => new(
        i.DrugItemId, i.DrugName, i.GenericName, i.Strength, i.DosageForm, i.Route, i.Dose, i.Frequency, i.CustomFrequency, i.DosesPerDay,
        i.DurationDays, i.Quantity, i.Instructions, i.IsControlledDrug, i.ControlledSchedule);

    /// <summary>144-bit random URL-safe token printed as the QR verification code.</summary>
    private static string NewVerificationToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Fit(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
