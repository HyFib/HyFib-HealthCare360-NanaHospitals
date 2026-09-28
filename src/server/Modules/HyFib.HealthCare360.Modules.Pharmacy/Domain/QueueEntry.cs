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
    public List<PharmacyQueueLine> Lines { get; set; } = [];

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
        }));
        return entry;
    }

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
    };
}
