namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>
/// A physical bed. Every status change goes through <see cref="BedStateMachine"/> so the live bed board, the
/// housekeeping queue and the occupancy statistics always agree. <see cref="CurrentAdmissionId"/> is set exactly while
/// the bed is occupied (a unique index backs this up), and the concurrency version stops two desks allocating the
/// same bed at the same moment.
/// </summary>
public sealed class Bed : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid WardId { get; set; }
    public Guid? RoomId { get; set; }
    public string Label { get; set; } = string.Empty;
    public BedStatus Status { get; set; }

    /// <summary>Why the bed is blocked, under maintenance or reserved (shown on the bed board).</summary>
    public string? StatusReason { get; set; }

    public DateTimeOffset? StatusChangedAt { get; set; }
    public Guid? CurrentAdmissionId { get; set; }
    public Guid? ReservedForPatientId { get; set; }
    public DateTimeOffset? ReservedUntil { get; set; }
    public bool HasOxygen { get; set; }
    public bool HasVentilator { get; set; }
    public bool HasMonitor { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>A reservation lapses at <see cref="ReservedUntil"/>; the bed may then be given to anyone.</summary>
    public bool IsReservationExpired(DateTimeOffset now) => Status == BedStatus.Reserved && ReservedUntil is { } until && until <= now;

    /// <summary>Available for a new admission or transfer: vacant, reserved for this patient, or with a lapsed reservation.</summary>
    public bool IsAvailableFor(Guid patientId, DateTimeOffset now) =>
        IsActive && (Status == BedStatus.Vacant
                     || (Status == BedStatus.Reserved && (ReservedForPatientId == patientId || IsReservationExpired(now))));

    public Result Occupy(Guid admissionId, Guid patientId, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Rule("bed.inactive", $"Bed {Label} is not in service.");
        }

        if (Status == BedStatus.Reserved && ReservedForPatientId != patientId && !IsReservationExpired(now))
        {
            return Error.Rule("bed.reserved", $"Bed {Label} is reserved for another patient.");
        }

        var moved = MoveTo(BedStatus.Occupied, null, now);
        if (moved.IsFailure)
        {
            return moved;
        }

        CurrentAdmissionId = admissionId;
        return Result.Success();
    }

    /// <summary>The patient left the bed (discharge, transfer out or cancelled admission); it must be cleaned before reuse.</summary>
    public Result Release(string reason, DateTimeOffset now)
    {
        if (Status != BedStatus.Occupied)
        {
            return Error.Rule("bed.not_occupied", $"Bed {Label} is not occupied.");
        }

        var moved = MoveTo(BedStatus.Cleaning, reason, now);
        if (moved.IsSuccess)
        {
            CurrentAdmissionId = null;
        }

        return moved;
    }

    public Result MarkForCleaning(string? reason, DateTimeOffset now) => MoveTo(BedStatus.Cleaning, reason, now);

    public Result CompleteCleaning(DateTimeOffset now) =>
        Status == BedStatus.Cleaning
            ? MoveTo(BedStatus.Vacant, null, now)
            : Error.Rule("bed.not_cleaning", $"Bed {Label} is not waiting for cleaning.");

    public Result Reserve(Guid patientId, DateTimeOffset until, string? note, DateTimeOffset now)
    {
        if (until <= now)
        {
            return Error.Rule("bed.reservation_past", "The reservation must end in the future.");
        }

        var moved = MoveTo(BedStatus.Reserved, note, now);
        if (moved.IsFailure)
        {
            return moved;
        }

        ReservedForPatientId = patientId;
        ReservedUntil = until;
        return Result.Success();
    }

    public Result CancelReservation(DateTimeOffset now) =>
        Status == BedStatus.Reserved
            ? MoveTo(BedStatus.Vacant, null, now)
            : Error.Rule("bed.not_reserved", $"Bed {Label} is not reserved.");

    public Result Block(string reason, DateTimeOffset now) => MoveTo(BedStatus.Blocked, reason, now);

    public Result Unblock(DateTimeOffset now) =>
        Status == BedStatus.Blocked
            ? MoveTo(BedStatus.Vacant, null, now)
            : Error.Rule("bed.not_blocked", $"Bed {Label} is not blocked.");

    public Result StartMaintenance(string reason, DateTimeOffset now) => MoveTo(BedStatus.Maintenance, reason, now);

    /// <summary>Repaired beds go to cleaning, never straight back into use.</summary>
    public Result EndMaintenance(DateTimeOffset now) =>
        Status == BedStatus.Maintenance
            ? MoveTo(BedStatus.Cleaning, "Cleaning after maintenance", now)
            : Error.Rule("bed.not_in_maintenance", $"Bed {Label} is not under maintenance.");

    private Result MoveTo(BedStatus target, string? reason, DateTimeOffset now)
    {
        var check = BedStateMachine.Validate(Status, target, reason);
        if (check.IsFailure)
        {
            return check;
        }

        Status = target;
        StatusReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        StatusChangedAt = now;
        if (target != BedStatus.Reserved)
        {
            ReservedForPatientId = null;
            ReservedUntil = null;
        }

        return Result.Success();
    }
}
