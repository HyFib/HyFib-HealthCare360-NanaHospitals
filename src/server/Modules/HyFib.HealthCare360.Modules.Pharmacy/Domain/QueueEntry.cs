using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

public enum QueueEntryStatus
{
    Pending = 0,
    PartiallyDispensed,
    Dispensed,
    Cancelled,
}

public enum QueueLineStatus
{
    Pending = 0,
    PartiallyDispensed,
    Dispensed,
    Cancelled,
}

/// <summary>
/// A signed e-prescription waiting in the pharmacy (PH-01, ERX-03), created from <see cref="PrescriptionSigned"/>.
/// Lines track requested versus dispensed quantities so dispensing can be partial; refills (PH-05) are separate
/// entries pointing back to the original through <see cref="RefillOfEntryId"/>.
/// </summary>
public sealed class PharmacyQueueEntry : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PrescriptionId { get; set; }
    public string PrescriptionNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid PractitionerId { get; set; }
    public bool IsInpatient { get; set; }
    public DateTimeOffset SignedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public QueueEntryStatus Status { get; private set; }
    public bool HasControlledDrugs { get; set; }

    /// <summary>Touched by every dispense so concurrent counters cannot both dispense the same line.</summary>
    public DateTimeOffset? LastDispensedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }
    public string? CloseReason { get; private set; }
    public Guid? RefillOfEntryId { get; set; }
    public int RefillsAuthorized { get; private set; }
    public int RefillsUsed { get; private set; }
    public DateOnly? RefillValidUntil { get; private set; }
    public string? RefillNote { get; private set; }
    public DateTimeOffset? LastRefillReminderAt { get; set; }

    /// <summary>
    /// The prescriber's reason for signing over a blocking safety alert (<see cref="PrescriptionSigned.SafetyOverrideReason"/>);
    /// the lines it covers carry the alerts (<see cref="PharmacyQueueLine.SafetyOverride"/>).
    /// </summary>
    public string? SafetyOverrideReason { get; set; }

    /// <summary>The account that signed the prescription: it never acknowledges its own override at the counter.</summary>
    public Guid? SignedByUserId { get; set; }

    /// <summary>
    /// When the prescription was handed over on paper during a downtime (the time on the paper), so the queue does not offer
    /// it again; <c>null</c> for one dispensed on the screen. Set by <see cref="MarkDispensedOnPaper"/>.
    /// </summary>
    public DateTimeOffset? DispensedOnPaperAt { get; private set; }

    /// <summary>The invoice number the paper sale was entered under, when it has been entered.</summary>
    public string? PaperSaleNumber { get; private set; }

    public List<PharmacyQueueLine> Lines { get; set; } = [];

    /// <summary>A line signed over a blocking alert that no pharmacist has acknowledged yet.</summary>
    public bool HasUnacknowledgedOverride => Lines.Any(l => l.NeedsOverrideAcknowledgement);

    public int RefillsRemaining => Math.Max(RefillsAuthorized - RefillsUsed, 0);

    public bool IsOpen => Status is QueueEntryStatus.Pending or QueueEntryStatus.PartiallyDispensed;

    public static PharmacyQueueEntry FromPrescription(PrescriptionSigned prescription, DateTimeOffset receivedAt)
    {
        var entry = new PharmacyQueueEntry
        {
            FacilityId = prescription.FacilityId,
            PrescriptionId = prescription.PrescriptionId,
            PrescriptionNumber = prescription.PrescriptionNumber,
            PatientId = prescription.PatientId,
            VisitId = prescription.VisitId,
            AdmissionId = prescription.AdmissionId,
            PractitionerId = prescription.PractitionerId,
            IsInpatient = prescription.IsInpatient,
            SignedAt = prescription.SignedAt,
            ReceivedAt = receivedAt,
            HasControlledDrugs = prescription.Lines.Any(l => l.IsControlledDrug),
            SafetyOverrideReason = Fit(prescription.SafetyOverrideReason, 1000),
            SignedByUserId = prescription.SignedByUserId,
        };
        entry.Lines.AddRange(prescription.Lines.Select(l => new PharmacyQueueLine
        {
            QueueEntryId = entry.Id,
            PrescriptionLineId = l.LineId,
            DrugItemId = l.DrugItemId,
            DrugName = l.DrugName,
            GenericName = l.GenericName,
            Strength = l.Strength,
            DosageForm = l.DosageForm,
            Route = l.Route,
            Dose = l.Dose,
            Frequency = l.Frequency,
            DurationDays = l.DurationDays,
            Instructions = l.Instructions,
            IsControlledDrug = l.IsControlledDrug,
            RequestedQuantity = l.Quantity,
            SafetyOverride = prescription.SafetyOverrideReason is null ? null : Fit(l.SafetyOverride, 1000),
        }));
        return entry;
    }

    /// <summary>
    /// A pharmacist acknowledges, from their own sign-in, that a line was signed over a blocking safety alert and that they
    /// have read the prescriber's reason (and, where they judge it needed, spoken to the prescriber): only then is the line
    /// dispensed or issued. The account that signed the prescription never acknowledges its own override.
    /// </summary>
    public Result AcknowledgeOverride(Guid lineId, Guid userId, string userName, string? note, DateTimeOffset now)
    {
        var line = Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound(nameof(PharmacyQueueLine), lineId);
        }

        if (line.SafetyOverride is null)
        {
            return Error.Rule("pharmacy.no_override", $"{line.DrugName} was not signed over a safety alert: there is nothing to acknowledge.");
        }

        if (line.OverrideAcknowledgedAt is not null)
        {
            return Error.Rule("pharmacy.override_acknowledged", $"{line.DrugName}'s override was already acknowledged by {line.OverrideAcknowledgedByName}.");
        }

        if (!IsOpen)
        {
            return Error.Rule("queue.closed", $"Prescription {PrescriptionNumber} is {Status}.");
        }

        if (SignedByUserId is { } signer && signer == userId)
        {
            return Error.Forbidden("pharmacy.override_own_prescription",
                $"You signed prescription {PrescriptionNumber} over the safety alert: another pharmacist acknowledges it.");
        }

        line.OverrideAcknowledgedByUserId = userId;
        line.OverrideAcknowledgedByName = Fit(userName, 200);
        line.OverrideAcknowledgedAt = now;
        line.OverrideAcknowledgementNote = Fit(string.IsNullOrWhiteSpace(note) ? null : note.Trim(), 500);
        return Result.Success();
    }

    private static string? Fit(string? text, int length) => text is null || text.Length <= length ? text : text[..(length - 1)] + "…";

    public Result RecordDispensed(Guid lineId, decimal quantity, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("queue.closed", $"Prescription {PrescriptionNumber} is {Status}.");
        }

        var line = Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound(nameof(PharmacyQueueLine), lineId);
        }

        var dispensed = line.Dispense(quantity);
        if (dispensed.IsFailure)
        {
            return dispensed;
        }

        LastDispensedAt = now;
        RefreshStatus();
        return Result.Success();
    }

    /// <summary>Puts quantity back on a line when its sale is cancelled the same day (the entry re-opens).</summary>
    public Result ReverseDispensed(Guid lineId, decimal quantity, DateTimeOffset now)
    {
        if (Status == QueueEntryStatus.Cancelled)
        {
            return Result.Success();
        }

        var line = Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound(nameof(PharmacyQueueLine), lineId);
        }

        line.Reverse(quantity);
        LastDispensedAt = now;
        ClosedAt = null;
        CloseReason = null;
        RefreshStatus();
        return Result.Success();
    }

    /// <summary>The pharmacist closes the entry: what is still outstanding will not be dispensed (patient declined, bought elsewhere).</summary>
    public Result Close(string reason, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("queue.closed", $"Prescription {PrescriptionNumber} is already {Status}.");
        }

        foreach (var line in Lines)
        {
            line.CloseRemaining();
        }

        CloseReason = reason;
        ClosedAt = now;
        Status = Lines.Any(l => l.DispensedQuantity > 0) ? QueueEntryStatus.Dispensed : QueueEntryStatus.Cancelled;
        return Result.Success();
    }

    /// <summary>
    /// The prescription was dispensed on paper during a downtime: the sale is written in the paper sale book and entered
    /// afterwards, and the prescription, which reaches the queue when HC360 is back, is closed as dispensed so it is not
    /// dispensed twice. What was handed over is on the paper sale (and so in the stock and the invoice), not on the queue
    /// lines, so the lines are marked dispensed without a quantity.
    /// </summary>
    /// <param name="saleNumber">The invoice number the paper sale was entered under, if it has been entered.</param>
    /// <param name="at">The time on the paper.</param>
    public Result MarkDispensedOnPaper(string? saleNumber, DateTimeOffset at, string? note, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("queue.closed", $"Prescription {PrescriptionNumber} is already {Status}.");
        }

        foreach (var line in Lines)
        {
            line.MarkDispensedOnPaper();
        }

        DispensedOnPaperAt = at;
        PaperSaleNumber = saleNumber;
        CloseReason = Fit(string.IsNullOrWhiteSpace(note)
            ? $"Dispensed on paper during a downtime (sale {saleNumber})"
            : $"Dispensed on paper during a downtime: {note.Trim()}", 300);
        ClosedAt = now;
        Status = QueueEntryStatus.Dispensed;
        return Result.Success();
    }

    /// <summary>The prescriber cancelled the prescription (<see cref="PrescriptionCancelled"/>): nothing more is dispensed.</summary>
    public Result CancelByPrescriber(string reason, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("queue.closed", $"Prescription {PrescriptionNumber} is already {Status}.");
        }

        foreach (var line in Lines)
        {
            line.CloseRemaining();
        }

        CloseReason = reason;
        ClosedAt = now;
        Status = QueueEntryStatus.Cancelled;
        return Result.Success();
    }

    /// <summary>
    /// Records the prescriber's refill authorisation for chronic medication (PH-05, ERX-05). Controlled medicines
    /// (Schedule H1/X, NDPS) always need a fresh prescription, so they cannot be refilled.
    /// </summary>
    public Result AuthorizeRefills(int refills, DateOnly validUntil, string note, DateOnly today)
    {
        if (IsInpatient || RefillOfEntryId is not null)
        {
            return Error.Rule("refill.not_refillable", "Refills are authorised on the original out-patient prescription.");
        }

        if (HasControlledDrugs)
        {
            return Error.Rule("refill.controlled", "Prescriptions with Schedule H1/X or narcotic medicines cannot be refilled; a new prescription is needed.");
        }

        if (Status == QueueEntryStatus.Cancelled)
        {
            return Error.Rule("refill.cancelled", "This prescription was cancelled.");
        }

        if (refills < RefillsUsed || refills > 12)
        {
            return Error.Field("refills", $"Allow between {RefillsUsed} and 12 refills.");
        }

        if (validUntil <= today)
        {
            return Error.Field("validUntil", "The refill authorisation must end in the future.");
        }

        RefillsAuthorized = refills;
        RefillValidUntil = validUntil;
        RefillNote = note;
        return Result.Success();
    }

    /// <summary>A cancelled prescription can no longer be refilled; refills already used stay on record.</summary>
    public void RevokeRefills() => RefillsAuthorized = RefillsUsed;

    public Result EnsureRefillable(DateOnly today)
    {
        if (IsInpatient || RefillOfEntryId is not null || HasControlledDrugs)
        {
            return Error.Rule("refill.not_refillable", "This prescription cannot be refilled.");
        }

        if (Status != QueueEntryStatus.Dispensed)
        {
            return Error.Rule("refill.not_dispensed", "The prescription must be dispensed before it can be refilled.");
        }

        if (RefillsRemaining <= 0)
        {
            return Error.Rule("refill.none_left", "No refills are left on this prescription. Ask the doctor for a new prescription.");
        }

        return RefillValidUntil is { } until && until < today
            ? Error.Rule("refill.expired", "The refill authorisation has expired. Ask the doctor for a new prescription.")
            : Result.Success();
    }

    /// <summary>Uses one refill and returns the queue entry to dispense it (the prescription's lines at full quantity).</summary>
    public Result<PharmacyQueueEntry> CreateRefill(DateOnly today, DateTimeOffset now)
    {
        var refillable = EnsureRefillable(today);
        if (refillable.IsFailure)
        {
            return refillable.Error!;
        }

        RefillsUsed++;
        var refill = new PharmacyQueueEntry
        {
            FacilityId = FacilityId,
            PrescriptionId = PrescriptionId,
            PrescriptionNumber = $"{PrescriptionNumber}/R{RefillsUsed}",
            PatientId = PatientId,
            VisitId = VisitId,
            PractitionerId = PractitionerId,
            SignedAt = SignedAt,
            ReceivedAt = now,
            HasControlledDrugs = false,
            RefillOfEntryId = Id,

            // A refill is dispensed again, so a line signed over an alert is acknowledged again, by whoever dispenses it.
            SafetyOverrideReason = SafetyOverrideReason,
            SignedByUserId = SignedByUserId,
        };
        refill.Lines.AddRange(Lines.Select(l => l.CopyForRefill(refill.Id)));
        return refill;
    }

    private void RefreshStatus()
    {
        if (Lines.All(l => l.Status is QueueLineStatus.Dispensed or QueueLineStatus.Cancelled))
        {
            Status = Lines.Any(l => l.DispensedQuantity > 0) ? QueueEntryStatus.Dispensed : QueueEntryStatus.Cancelled;
        }
        else
        {
            Status = Lines.Any(l => l.DispensedQuantity > 0) ? QueueEntryStatus.PartiallyDispensed : QueueEntryStatus.Pending;
        }
    }
}

public sealed class PharmacyQueueLine : Entity
{
    public Guid QueueEntryId { get; set; }
    public Guid PrescriptionLineId { get; set; }

    /// <summary>Item master id when the doctor prescribed from the drug master (null for free-text drugs).</summary>
    public Guid? DrugItemId { get; set; }

    public string DrugName { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? Strength { get; set; }
    public string? DosageForm { get; set; }
    public string? Route { get; set; }
    public string Dose { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public int DurationDays { get; set; }
    public string? Instructions { get; set; }
    public bool IsControlledDrug { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal DispensedQuantity { get; private set; }
    public QueueLineStatus Status { get; private set; }

    /// <summary>The blocking safety alerts the prescriber signed this line over, in words; null when there were none.</summary>
    public string? SafetyOverride { get; set; }

    public Guid? OverrideAcknowledgedByUserId { get; internal set; }
    public string? OverrideAcknowledgedByName { get; internal set; }
    public DateTimeOffset? OverrideAcknowledgedAt { get; internal set; }
    public string? OverrideAcknowledgementNote { get; internal set; }

    /// <summary>Signed over a blocking alert and not yet acknowledged by a pharmacist: not dispensed or issued until it is.</summary>
    public bool NeedsOverrideAcknowledgement => SafetyOverride is not null && OverrideAcknowledgedAt is null;

    /// <summary>The refusal to dispense or issue this line while its override waits for a pharmacist.</summary>
    internal Error OverrideRefusal() => new("pharmacy.override_unacknowledged",
        $"{DrugName} was prescribed over a safety alert ({SafetyOverride}). Read the prescriber's reason and acknowledge the override before dispensing it.")
    {
        Data = new { queueLineId = Id },
    };

    public decimal RemainingQuantity => Status is QueueLineStatus.Dispensed or QueueLineStatus.Cancelled
        ? 0
        : Math.Max(RequestedQuantity - DispensedQuantity, 0);

    internal Result Dispense(decimal quantity)
    {
        if (Status is QueueLineStatus.Dispensed or QueueLineStatus.Cancelled)
        {
            return Error.Rule("queue.line_closed", $"{DrugName} is already {Status}.");
        }

        if (quantity <= 0)
        {
            return Error.Rule("queue.invalid_quantity", "Quantity must be more than zero.");
        }

        if (quantity > RemainingQuantity)
        {
            return Error.Rule("queue.over_dispense", $"{DrugName}: only {StockErrors.Format(RemainingQuantity)} is left to dispense on this prescription.");
        }

        DispensedQuantity += quantity;
        Status = DispensedQuantity >= RequestedQuantity ? QueueLineStatus.Dispensed : QueueLineStatus.PartiallyDispensed;
        return Result.Success();
    }

    internal void Reverse(decimal quantity)
    {
        DispensedQuantity = Math.Max(DispensedQuantity - quantity, 0);
        if (Status != QueueLineStatus.Cancelled || DispensedQuantity > 0)
        {
            Status = DispensedQuantity == 0 ? QueueLineStatus.Pending : QueueLineStatus.PartiallyDispensed;
        }
    }

    /// <summary>Closes what is outstanding: a partly dispensed line counts as dispensed, an untouched one as cancelled.</summary>
    internal void CloseRemaining()
    {
        if (Status is QueueLineStatus.Pending or QueueLineStatus.PartiallyDispensed)
        {
            Status = DispensedQuantity > 0 ? QueueLineStatus.Dispensed : QueueLineStatus.Cancelled;
        }
    }

    /// <summary>Handed over on paper: what is outstanding counts as dispensed, with the quantity left to the paper sale.</summary>
    internal void MarkDispensedOnPaper()
    {
        if (Status is QueueLineStatus.Pending or QueueLineStatus.PartiallyDispensed)
        {
            Status = QueueLineStatus.Dispensed;
        }
    }

    internal PharmacyQueueLine CopyForRefill(Guid entryId) => new()
    {
        QueueEntryId = entryId,
        PrescriptionLineId = PrescriptionLineId,
        DrugItemId = DrugItemId,
        DrugName = DrugName,
        GenericName = GenericName,
        Strength = Strength,
        DosageForm = DosageForm,
        Route = Route,
        Dose = Dose,
        Frequency = Frequency,
        DurationDays = DurationDays,
        Instructions = Instructions,
        IsControlledDrug = IsControlledDrug,
        RequestedQuantity = RequestedQuantity,
        SafetyOverride = SafetyOverride,
    };
}
