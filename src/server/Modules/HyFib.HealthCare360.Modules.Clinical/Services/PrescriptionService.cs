using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
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
    IAuditLogger audit,
    IRealtimePublisher realtime,
    INotificationService notifications,
    PrescriptionSignatureSettings signatureSettings,
    PrescriptionFinalCopies finalCopies,
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
    /// prescription for the pharmacy. Safety checks run again because allergies or other medicines may have changed.
    /// </summary>
    public async Task<Result<SignPrescriptionResult>> RefillAsync(Guid id, RefillPrescriptionRequest request, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var source = loaded.Value;
        var used = source.UseRefill();
        if (used.IsFailure)
        {
            return used.Error!;
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
        };
        refill.PrescriptionNumber = await numbers.NextAsync(ClinicalNumberSeries.Prescription, refill.FacilityId, ct);
        refill.ReplaceItems(await FromDrugMasterAsync(source.Items.OrderBy(i => i.Sequence).Select(i => ToItem(ToRequest(i))), ct));

        var signed = await SignCoreAsync(refill, request.OverrideReason, request.AcknowledgeWarnings, request.ConfirmControlledDrugs, ct);
        if (signed.IsFailure)
        {
            return signed.Error!;
        }

        db.Add(refill);
        await QueueSignedNoticeAsync(refill, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PublishSignedAsync(refill, ct);
        return new SignPrescriptionResult(await ToDtoAsync(refill, ct), signed.Value);
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
        finalCopyAvailable);

    /// <summary>The medicines a teleconsultation may not prescribe, for a prescription written in one (TPG 2020).</summary>
    private async Task<IReadOnlyList<string>> TelemedicineProhibitedAsync(Prescription prescription, CancellationToken ct)
    {
        // A teleconsultation is a telehealth encounter, or anything written on a telehealth visit: the prescription's own
        // visit, or its encounter's.
        var encounter = prescription.EncounterId is { } encounterId
            ? await db.Set<Encounter>().AsNoTracking().Where(e => e.Id == encounterId).Select(e => new { e.Type, e.VisitId }).FirstOrDefaultAsync(ct)
            : null;
        Guid?[] visitIds = [prescription.VisitId, encounter?.VisitId];
        var teleconsult = encounter?.Type == EncounterType.Telehealth
                          || await db.Set<Visit>().AnyAsync(v => visitIds.Contains(v.Id) && v.Type == VisitType.Telehealth, ct);
        if (!teleconsult)
        {
            return [];
        }

        var drugIds = prescription.Items.Where(i => i.DrugItemId is not null).Select(i => i.DrugItemId!.Value).Distinct().ToList();
        var master = await db.Set<Item>().AsNoTracking().Where(i => drugIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Schedule, i.IsControlled }).ToDictionaryAsync(i => i.Id, ct);
        return TelemedicinePrescribing.Prohibited(prescription.Items.Select(i =>
            i.DrugItemId is { } id && master.TryGetValue(id, out var drug)
                ? new TelemedicinePrescribing.Medicine(i.DrugName, drug.Schedule, drug.IsControlled)
                // A medicine typed in, not from the drug master: what the line itself says. A controlled line may be an NDPS
                // drug, which the line cannot tell from Schedule H1, so it counts as one.
                : new TelemedicinePrescribing.Medicine(i.DrugName, i.ControlledSchedule == "X" ? DrugSchedule.X : DrugSchedule.None, i.IsControlledDrug)));
    }

    private async Task<Result<IReadOnlyList<SafetyAlert>>> SignCoreAsync(Prescription prescription, string? overrideReason, bool acknowledgeWarnings,
        bool confirmControlledDrugs, CancellationToken ct)
    {
        // A prescription carries the prescriber's registration number (Indian Medical Council (Professional Conduct,
        // Etiquette and Ethics) Regulations 2002, 1.4.1): one whose record has none is not signed.
        var registration = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.Id == prescription.PractitionerId)
            .Select(p => p.RegistrationNumber)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(registration))
        {
            return Error.Rule("prescription.registration_missing",
                "The prescriber's medical council registration number is not on their practitioner record, and a prescription must carry it. "
                + "Ask the administrator to add it under Practitioners, then sign.");
        }

        // Again at signing: a draft saved before the master decided, or whose drug the master has since reclassified.
        await FromDrugMasterAsync(prescription.Items, ct);
        if (await TelemedicineProhibitedAsync(prescription, ct) is { Count: > 0 } prohibited)
        {
            return TelemedicinePrescribing.Refusal(prohibited);
        }

        var check = await safety.CheckAsync(prescription, ct);
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
            JsonSerializer.Serialize(check.Alerts, JsonOptions), confirmControlledDrugs);
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
    /// names the prescription's PDF: WhatsApp sends it in the template's document header when the template has one.
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
        if (patient?.Phone is null)
        {
            prescription.ReleasePatientNotice();
            return;
        }

        var doctor = (await PrintQueries.PractitionerNamesAsync(db, [prescription.PractitionerId], ct)).GetValueOrDefault(prescription.PractitionerId) ?? "your doctor";
        notifications.Queue("prescription.shared",
            new NotificationRecipient(patient.Phone, patient.Email, prescription.PatientId, patient.FullName),
            new Dictionary<string, string>
            {
                ["name"] = patient.FullName,
                ["doctor"] = doctor,
                ["date"] = ClinicalClock.LocalDate(prescription.SignedAt!.Value).ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture),
            },
            [NotificationChannel.WhatsApp],
            TimeSpan.FromDays(2),
            prescription.FacilityId,
            document: new NotificationDocument(PrescriptionDocumentSource.DocumentKind, prescription.Id));
        prescription.MarkPatientNotified(time.GetUtcNow());
    }

    /// <summary>
    /// Whether a line is a controlled drug (Schedule H1/X, NDPS: the prescriber's confirmation and the controlled-drug
    /// register) is the drug master's to say when the line names one of its drugs, as the drug picker shows it; what the
    /// client sent can only add the care, never take it away. A line typed in keeps what it says.
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
            .Select(i => new { i.Id, i.Schedule, i.IsControlled }).ToDictionaryAsync(i => i.Id, ct);
        foreach (var item in items)
        {
            if (item.DrugItemId is { } id && master.TryGetValue(id, out var drug) && Item.NeedsRegister(drug.Schedule, drug.IsControlled))
            {
                item.IsControlledDrug = true;
                item.ControlledSchedule = drug.Schedule == DrugSchedule.X ? "X" : "H1";
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
}
