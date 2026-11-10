using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// What two staff confirm at the bedside before a unit is started (BSH guideline on the administration of blood
/// components, 2017; the DGHS transfusion medicine manual): this patient, this unit, still usable, with consent. The
/// patient's identity and the unit number are read from the wristband and the bag, not from the screen.
/// </summary>
public static class BedsideCheck
{
    /// <summary>
    /// A unit out of controlled storage is transfused within 4 hours of leaving it; one not started by then cannot be
    /// finished in time, and goes back to the blood bank to be discarded.
    /// </summary>
    public static readonly TimeSpan OutOfStorageLimit = TimeSpan.FromHours(4);

    /// <summary>A consent asked for outside an admission counts for the day before the blood was requested.</summary>
    public static readonly TimeSpan ConsentLookBack = TimeSpan.FromHours(24);

    /// <summary>
    /// Since when a transfusion consent counts for a unit: the patient's stay, when the blood was asked for during an
    /// admission; otherwise the day before it was asked for. A consent to transfusion is for one stay or procedure.
    /// </summary>
    public static DateTimeOffset ConsentSince(DateTimeOffset requestedAt, DateTimeOffset? admittedAt) => admittedAt ?? requestedAt - ConsentLookBack;

    /// <summary>The wristband and the bag are the patient and the unit the blood bank issued.</summary>
    public static Result Identity(string patientUhid, string wristbandUhid, string unitNumber, string bagUnitNumber)
    {
        if (!Same(patientUhid, wristbandUhid))
        {
            return Error.Rule("bloodbank.wrong_patient",
                "The wristband is not the patient this unit was issued for. Stop, and check the patient and the unit with the blood bank.");
        }

        return Same(unitNumber, bagUnitNumber)
            ? Result.Success()
            : Error.Rule("bloodbank.wrong_unit",
                "This bag is not the unit issued for this patient. Stop, and check the unit with the blood bank.");
    }

    /// <summary>The unit is in date, its cross-match still stands (unless released in an emergency), and it has not been out too long.</summary>
    public static Result Usable(DateTimeOffset now, DateTimeOffset issuedAt, DateTimeOffset expiresAt, bool emergencyRelease, CrossMatch? crossMatch)
    {
        if (expiresAt <= now)
        {
            return Error.Rule("bloodbank.unit_expired", "The unit has expired. Do not start it: return it to the blood bank.");
        }

        if (!emergencyRelease && crossMatch?.IsValidCompatibleAt(now) != true)
        {
            return Error.Rule("bloodbank.crossmatch_expired",
                "The unit's cross-match is more than 72 hours old. Do not start it: return it, and send a new sample to the blood bank.");
        }

        return now - issuedAt > OutOfStorageLimit
            ? Error.Rule("bloodbank.out_of_storage_too_long",
                "The unit left the blood bank more than 4 hours ago and cannot be given in time. Return it to the blood bank to be discarded.")
            : Result.Success();
    }

    /// <summary>
    /// The patient's consent for this stay; a unit released in an emergency may be started without one when the reason
    /// is recorded (the consent is taken as soon as it can be).
    /// </summary>
    public static Result Consent(bool consented, bool emergencyRelease, string? deferredReason)
    {
        if (consented)
        {
            return Result.Success();
        }

        if (!emergencyRelease)
        {
            return Error.Rule("bloodbank.consent_required",
                "The patient's consent to this transfusion is not recorded for this stay. Record it under the patient's consents before starting.");
        }

        return string.IsNullOrWhiteSpace(deferredReason)
            ? Error.Rule("bloodbank.consent_required",
                "The patient's consent is not recorded. For blood released in an emergency, say why it cannot be taken now.")
            : Result.Success();
    }

    private static bool Same(string expected, string read) => string.Equals(expected.Trim(), read.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One unit transfused on the ward: checked at the bedside by two staff, started, observed, and ended or stopped.
/// With the issue, it says what became of the unit: returned, or transfused.
/// </summary>
public sealed class Transfusion : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid IssueId { get; set; }
    public Guid RequestId { get; set; }
    public Guid PatientId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public BloodComponentType ComponentType { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public Guid? StartedByUserId { get; set; }
    public string StartedByName { get; set; } = string.Empty;

    /// <summary>The second member of staff, who proved who they are at the starter's screen.</summary>
    public Guid WitnessUserId { get; set; }
    public string WitnessName { get; set; } = string.Empty;

    /// <summary>The consent the start relied on; none when it was deferred in an emergency (<see cref="ConsentDeferredReason"/>).</summary>
    public Guid? ConsentId { get; set; }
    public string? ConsentDeferredReason { get; set; }

    public TransfusionStatus Status { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public Guid? EndedByUserId { get; set; }
    public string? EndedByName { get; set; }
    public int? VolumeMl { get; set; }
    public string? StopReason { get; set; }

    public List<TransfusionObservation> Observations { get; set; } = [];

    /// <summary>A set of observations taken while it runs (at 15 minutes, or later).</summary>
    public Result Observe(TransfusionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (Status != TransfusionStatus.InProgress)
        {
            return Error.Rule("bloodbank.transfusion_ended", "This transfusion has ended; record later observations in the patient's vitals.");
        }

        if (observation.Kind is TransfusionObservationKind.Baseline or TransfusionObservationKind.End)
        {
            return Error.Field("kind", "Observations before the start and at the end are taken with the bedside check and with the end.");
        }

        if (observation.RecordedAt < StartedAt)
        {
            return Error.Field("recordedAt", "Observations during the transfusion are taken after it started.");
        }

        observation.TransfusionId = Id;
        Observations.Add(observation);
        return Result.Success();
    }

    /// <summary>
    /// Ends the transfusion, completed or stopped, with the volume given and a last set of observations. A unit of
    /// <paramref name="unitVolumeMl"/> cannot have given much more than it held.
    /// </summary>
    public Result End(DateTimeOffset endedAt, bool stopped, string? stopReason, int volumeMl, int unitVolumeMl, TransfusionObservation observation,
        Guid? userId, string userName)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (Status != TransfusionStatus.InProgress)
        {
            return Error.Rule("bloodbank.transfusion_ended", "This transfusion has already ended.");
        }

        if (endedAt < StartedAt)
        {
            return Error.Field("endedAt", "The transfusion cannot end before it started.");
        }

        if (stopped && string.IsNullOrWhiteSpace(stopReason))
        {
            return Error.Field("stopReason", "Say why the transfusion was stopped. Report a reaction as well, if it was one.");
        }

        // A bag holds a little more than its labelled volume; much more than that was not given from this unit.
        if (volumeMl < 0 || (unitVolumeMl > 0 && volumeMl > unitVolumeMl + (unitVolumeMl / 10)))
        {
            return Error.Field("volumeMl", $"Enter the volume given from this unit ({unitVolumeMl} ml when full).");
        }

        observation.Kind = TransfusionObservationKind.End;
        observation.TransfusionId = Id;
        Observations.Add(observation);
        (Status, EndedAt, EndedByUserId, EndedByName, VolumeMl) =
            (stopped ? TransfusionStatus.Stopped : TransfusionStatus.Completed, endedAt, userId, userName, volumeMl);
        StopReason = stopped ? stopReason!.Trim() : null;
        return Result.Success();
    }
}

/// <summary>A set of observations taken before, during or at the end of a transfusion.</summary>
public sealed class TransfusionObservation : AuditableEntity
{
    public Guid TransfusionId { get; set; }
    public TransfusionObservationKind Kind { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string RecordedByName { get; set; } = string.Empty;
    public decimal? TemperatureC { get; set; }
    public int? PulsePerMinute { get; set; }
    public int? SystolicMmHg { get; set; }
    public int? DiastolicMmHg { get; set; }
    public int? RespiratoryRate { get; set; }
    public int? SpO2Percent { get; set; }
}
