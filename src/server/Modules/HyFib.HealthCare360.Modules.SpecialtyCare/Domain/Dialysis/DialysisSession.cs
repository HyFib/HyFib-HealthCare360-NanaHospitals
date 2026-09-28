using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;

/// <summary>Hourly intra-dialytic observation row.</summary>
public sealed record DialysisReading(
    DateTimeOffset At,
    int? Systolic,
    int? Diastolic,
    int? Pulse,
    int? BloodFlowMlMin,
    int? UfRateMlHr,
    int? VenousPressure,
    int? ArterialPressure,
    int? TransmembranePressure,
    string? Notes);

public sealed record PreDialysisAssessment(decimal WeightKg, int Systolic, int Diastolic, int Pulse, decimal? TemperatureC, int? TargetUfMl);

public sealed record PostDialysisAssessment(decimal WeightKg, int Systolic, int Diastolic, int? Pulse, int? UfAchievedMl);

/// <summary>
/// One haemodialysis session (DIA-03/04): pre, intra and post parameters, anticoagulation, the dialyzer and its
/// use number, complications and the performing technician. Safety checks run on the machine and dialyzer the
/// session is about to use, so a sero-mismatched machine or another patient's dialyzer can never be connected.
/// </summary>
public sealed class DialysisSession : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string SessionNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? ProfileId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public DateOnly ScheduledDate { get; set; }
    public DialysisShift Shift { get; set; }
    public DialysisSessionStatus Status { get; set; }
    public Guid? MachineId { get; set; }

    public decimal? PreWeightKg { get; set; }
    public int? PreSystolic { get; set; }
    public int? PreDiastolic { get; set; }
    public int? PrePulse { get; set; }
    public decimal? PreTemperatureC { get; set; }
    public int? TargetUfMl { get; set; }
    public DateTimeOffset? PreAssessedAt { get; set; }

    public List<DialysisReading> Readings { get; set; } = [];

    public decimal? PostWeightKg { get; set; }
    public int? PostSystolic { get; set; }
    public int? PostDiastolic { get; set; }
    public int? PostPulse { get; set; }
    public int? UfAchievedMl { get; set; }

    public int? HeparinLoadingUnits { get; set; }
    public int? HeparinHourlyUnits { get; set; }
    public Guid? DialyzerId { get; set; }
    public int? DialyzerUseNumber { get; set; }

    public List<DialysisComplication> Complications { get; set; } = [];
    public string? ComplicationNotes { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public Guid? PerformedByUserId { get; set; }
    public string? PerformedByName { get; set; }
    public string? CancelReason { get; set; }

    public ChargeStatus ChargeStatus { get; set; }
    public string? ChargeError { get; set; }

    public int? DurationMinutes => StartedAt is { } start && EndedAt is { } end ? (int)Math.Round((end - start).TotalMinutes) : null;

    /// <summary>Allocates (or changes) the machine while the session is still only scheduled.</summary>
    public Result AssignMachine(DialysisMachine machine, SeroStatus patientStatus)
    {
        if (Status != DialysisSessionStatus.Scheduled)
        {
            return Error.Rule("dialysis.session_not_scheduled", "The machine can only be changed before the session starts.");
        }

        var sero = SeroSegregation.EnsureCompatible(patientStatus, machine.SeroDedication, machine.Code);
        if (sero.IsFailure)
        {
            return sero;
        }

        MachineId = machine.Id;
        return Result.Success();
    }

    public Result RecordPreAssessment(PreDialysisAssessment pre, DateTimeOffset now)
    {
        if (Status != DialysisSessionStatus.Scheduled)
        {
            return Error.Rule("dialysis.session_not_scheduled", "Pre-dialysis parameters are recorded before the session starts.");
        }

        PreWeightKg = pre.WeightKg;
        PreSystolic = pre.Systolic;
        PreDiastolic = pre.Diastolic;
        PrePulse = pre.Pulse;
        PreTemperatureC = pre.TemperatureC;
        TargetUfMl = pre.TargetUfMl;
        PreAssessedAt = now;
        return Result.Success();
    }

    /// <summary>Connects the patient: sero-matched, disinfected machine; the patient's own usable dialyzer; pre-assessment done.</summary>
    public Result Start(DialysisMachine machine, Dialyzer dialyzer, SeroStatus patientStatus, int? heparinLoadingUnits, int? heparinHourlyUnits, StaffStamp by)
    {
        if (Status != DialysisSessionStatus.Scheduled)
        {
            return Error.Rule("dialysis.session_not_scheduled", "Only a scheduled session can be started.");
        }

        if (PreAssessedAt is null)
        {
            return Error.Rule("dialysis.pre_assessment_missing", "Record the pre-dialysis weight and blood pressure before starting.");
        }

        var ready = machine.EnsureReadyFor(patientStatus);
        if (ready.IsFailure)
        {
            return ready;
        }

        var use = dialyzer.Use(PatientId, by.At);
        if (use.IsFailure)
        {
            return use.Error!;
        }

        machine.MarkInUse();
        MachineId = machine.Id;
        DialyzerId = dialyzer.Id;
        DialyzerUseNumber = use.Value;
        HeparinLoadingUnits = heparinLoadingUnits;
        HeparinHourlyUnits = heparinHourlyUnits;
        StartedAt = by.At;
        PerformedByUserId = by.UserId;
        PerformedByName = by.Name;
        Status = DialysisSessionStatus.InProgress;
        return Result.Success();
    }

    public Result AddReadings(IEnumerable<DialysisReading> rows)
    {
        if (Status != DialysisSessionStatus.InProgress)
        {
            return Error.Rule("dialysis.session_not_running", "Intra-dialysis readings are recorded while the session is running.");
        }

        Readings = Readings.Concat(rows).OrderBy(r => r.At).ToList();
        return Result.Success();
    }

    /// <summary>
    /// Ends the session and frees the machine (which then needs disinfection). Ultrafiltration achieved defaults to
    /// the weight lost (1 kg ≈ 1000 ml) when the machine value is not entered.
    /// </summary>
    public Result Complete(PostDialysisAssessment post, IReadOnlyList<DialysisComplication> complications, string? complicationNotes, DialysisMachine machine, DateTimeOffset now)
    {
        if (Status != DialysisSessionStatus.InProgress)
        {
            return Error.Rule("dialysis.session_not_running", "Only a running session can be completed.");
        }

        if (complications.Contains(DialysisComplication.Other) && string.IsNullOrWhiteSpace(complicationNotes))
        {
            return Error.Field("complicationNotes", "Describe the complication marked as 'Other'.");
        }

        PostWeightKg = post.WeightKg;
        PostSystolic = post.Systolic;
        PostDiastolic = post.Diastolic;
        PostPulse = post.Pulse;
        UfAchievedMl = post.UfAchievedMl ?? (PreWeightKg is { } pre ? (int)Math.Round((pre - post.WeightKg) * 1000m) : null);
        Complications = complications.Distinct().ToList();
        ComplicationNotes = string.IsNullOrWhiteSpace(complicationNotes) ? null : complicationNotes.Trim();
        EndedAt = now;
        Status = DialysisSessionStatus.Completed;
        machine.Release(now);
        return Result.Success();
    }

    public Result Cancel(string reason, bool patientDidNotCome)
    {
        if (Status != DialysisSessionStatus.Scheduled)
        {
            return Error.Rule("dialysis.session_not_scheduled", "Only a session that has not started can be cancelled.");
        }

        Status = patientDidNotCome ? DialysisSessionStatus.Missed : DialysisSessionStatus.Cancelled;
        CancelReason = reason.Trim();
        return Result.Success();
    }

    public void RecordCharges(ChargeStatus status, string? error)
    {
        ChargeStatus = status;
        ChargeError = error;
    }
}
