using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>How oxygen is given in recovery.</summary>
public enum OxygenDelivery
{
    RoomAir = 0,
    NasalCannula,
    FaceMask,
    VenturiMask,
    NonRebreatherMask,
}

/// <summary>Level of consciousness on the AVPU scale.</summary>
public enum RecoveryConsciousness
{
    Alert = 0,
    Voice,
    Pain,
    Unresponsive,
}

/// <summary>Breathing as the modified Aldrete score grades it.</summary>
public enum RecoveryBreathing
{
    DeepBreathAndCough = 0,

    /// <summary>Dyspnoea, shallow or limited breathing.</summary>
    Limited,
    Apnoeic,
}

/// <summary>Post-operative nausea and vomiting.</summary>
public enum Ponv
{
    None = 0,
    Nausea,
    Vomiting,
}

/// <summary>Where the patient goes from recovery.</summary>
public enum RecoveryDestination
{
    Ward = 0,
    Icu,
    Hdu,
}

/// <summary>
/// One row of the recovery chart, taken at intervals (every 5 minutes at first, then 15). The vital signs are the
/// anaesthesia chart's (<see cref="AnaesthesiaVitals"/>); the rest is what the modified Aldrete score, the pain score and
/// PONV need.
/// </summary>
/// <param name="LimbsMoving">How many limbs the patient moves on command (0–4).</param>
/// <param name="OnOxygen">The SpO₂ was taken on oxygen.</param>
public sealed record RecoveryObservation(
    Guid Id,
    DateTimeOffset At,
    int? HeartRate,
    int? Systolic,
    int? Diastolic,
    int? SpO2,
    bool OnOxygen,
    int? RespiratoryRate,
    decimal? TemperatureC,
    RecoveryConsciousness? Consciousness,
    RecoveryBreathing? Breathing,
    int? LimbsMoving,
    int? PainScore,
    Ponv? Ponv,
    Guid? RecordedByUserId,
    string RecordedByName);

/// <summary>
/// The recovery room (PACU) record of one surgery, from the patient's arrival after the WHO Sign-out until they leave for
/// the ward or for intensive care. The receiving nurse is the signed-in user who opens it; the anaesthetist hands over
/// from their own sign-in or confirms at the nurse's screen; the chart is kept at intervals; and the modified Aldrete
/// score of the latest observations decides whether the patient may go back to the ward (9 or more), unless an anaesthetist
/// overrides it with a reason. Intensive care takes the patient whatever the score. Once discharged the record is closed.
/// </summary>
public sealed class RecoveryRecord : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid SurgeryBookingId { get; set; }
    public Guid PatientId { get; set; }

    /// <summary>Time in recovery.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public Guid? ReceivedByUserId { get; set; }
    public string ReceivedByName { get; set; } = string.Empty;

    /// <summary>The patient's airway on arrival and now (none: their own airway).</summary>
    public AirwayDevice Airway { get; set; }

    public OxygenDelivery Oxygen { get; set; }
    public decimal? OxygenLitresPerMinute { get; set; }

    /// <summary>The systolic pressure before anaesthesia, against which the circulation is scored.</summary>
    public int? PreAnaesthesiaSystolic { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset? HandoverAt { get; set; }
    public Guid? HandedOverByUserId { get; set; }
    public string? HandedOverByName { get; set; }

    /// <summary>The anaesthetist confirmed the handover at the nurse's screen, with their own credentials.</summary>
    public bool HandoverConfirmedAtScreen { get; set; }

    public string? HandoverNotes { get; set; }

    public List<RecoveryObservation> Observations { get; set; } = [];

    /// <summary>Time out of recovery.</summary>
    public DateTimeOffset? DischargedAt { get; set; }

    public RecoveryDestination? Destination { get; set; }

    /// <summary>The modified Aldrete total at the discharge, when it could be scored.</summary>
    public int? DischargeScore { get; set; }

    public Guid? DischargedByUserId { get; set; }
    public string? DischargedByName { get; set; }

    /// <summary>Why the anaesthetist sent the patient to the ward below the score.</summary>
    public string? OverrideReason { get; set; }

    public Guid? OverrideByUserId { get; set; }
    public string? OverrideByName { get; set; }

    public bool IsDischarged => DischargedAt is not null;

    /// <summary>The latest row of the chart.</summary>
    public RecoveryObservation? Latest => Observations.OrderBy(o => o.At).LastOrDefault();

    /// <summary>The modified Aldrete score of the latest observations.</summary>
    public AldreteResult Score() => AldreteScore.Score(Latest, PreAnaesthesiaSystolic);

    public Result EnsureOpen() =>
        IsDischarged
            ? Error.Rule("recovery.discharged", "The patient has left recovery: the recovery record is closed.")
            : Result.Success();

    /// <summary>Records who handed the patient over: an anaesthetist, from their own sign-in or at the nurse's screen.</summary>
    public Result RecordHandover(StaffStamp from, bool confirmedAtScreen, string? notes)
    {
        ArgumentNullException.ThrowIfNull(from);
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (HandoverAt is not null)
        {
            return Error.Rule("recovery.handover_recorded", $"The handover from {HandedOverByName} is already recorded.");
        }

        HandoverAt = from.At;
        HandedOverByUserId = from.UserId;
        HandedOverByName = from.Name;
        HandoverConfirmedAtScreen = confirmedAtScreen;
        HandoverNotes = notes;
        return Result.Success();
    }

    /// <summary>Adds a row to the chart, kept in time order (rows are often written up after the round).</summary>
    public Result AddObservation(RecoveryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (observation.At < ReceivedAt)
        {
            return Error.Field("at", "The observations are taken after the patient arrived in recovery.");
        }

        Observations = [.. Observations.Append(observation).OrderBy(o => o.At)];
        return Result.Success();
    }

    /// <summary>
    /// Discharges the patient from recovery. The patient has left the theatre (the surgery is completed), and a case that
    /// had an anaesthetist has been handed over by one. To the ward the latest observations, taken within the last
    /// <see cref="AldreteScore.FreshFor"/>, score 9 or more, or an anaesthetist overrides (<paramref name="overrideBy"/>,
    /// with the reason); to ICU or HDU any score will do.
    /// </summary>
    public Result Discharge(
        RecoveryDestination destination, bool surgeryCompleted, bool handoverRequired, StaffStamp by, StaffStamp? overrideBy = null, string? overrideReason = null)
    {
        ArgumentNullException.ThrowIfNull(by);
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (!surgeryCompleted)
        {
            return Error.Rule("recovery.surgery_not_completed",
                "The surgery is not completed in the theatre yet: the surgeon completes it before the patient leaves recovery.");
        }

        if (handoverRequired && HandoverAt is null)
        {
            return Error.Rule("recovery.handover_missing", "Record the anaesthetist's handover before the patient leaves recovery.");
        }

        var score = Score();
        var check = WardCheck(score, by.At);
        if (destination == RecoveryDestination.Ward && check is not null && overrideBy is null)
        {
            return Error.Rule("recovery.score_too_low",
                $"{check} The patient goes to the ward at a modified Aldrete score of {AldreteScore.WardDischargeMinimum} or more, "
                + "or with the anaesthetist's override; to ICU or HDU at any score.");
        }

        if (overrideBy is not null)
        {
            if (destination != RecoveryDestination.Ward || check is null)
            {
                return Error.Rule("recovery.override_not_needed",
                    destination == RecoveryDestination.Ward
                        ? $"The score is {score.Total}: the patient may go to the ward without an override."
                        : "ICU and HDU take the patient at any score: no override is needed.");
            }

            if (string.IsNullOrWhiteSpace(overrideReason))
            {
                return Error.Field("reason", "Give the reason the patient goes to the ward below the score.");
            }

            OverrideReason = overrideReason.Trim();
            OverrideByUserId = overrideBy.UserId;
            OverrideByName = overrideBy.Name;
        }

        DischargedAt = by.At;
        Destination = destination;
        DischargeScore = score.Total;
        DischargedByUserId = by.UserId;
        DischargedByName = by.Name;
        return Result.Success();
    }

    /// <summary>Why the score does not send the patient to the ward now, or null when it does.</summary>
    public static string? WardCheck(AldreteResult score, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (score.At is null)
        {
            return "No observations are recorded yet.";
        }

        if (!score.IsComplete)
        {
            return $"The score cannot be given yet: record {string.Join(", ", score.Missing)}.";
        }

        if (score.At < now - AldreteScore.FreshFor)
        {
            return $"The last observations are more than {AldreteScore.FreshFor.TotalMinutes:0} minutes old: take them again.";
        }

        return score.MeetsWardDischarge ? null : $"The modified Aldrete score is {score.Total} of 10.";
    }
}
