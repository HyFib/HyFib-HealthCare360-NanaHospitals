using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;

public sealed record DialysisMachineDto(
    Guid Id, string Code, string Model, string? Manufacturer, string? SerialNumber, string? Station, SeroStatus SeroDedication,
    DialysisMachineStatus Status, DateTimeOffset? LastDisinfectedAt, string? LastDisinfectionMethod, DateTimeOffset? LastUsedAt,
    bool NeedsDisinfection, bool IsActive, uint Version);

public sealed record SaveDialysisMachineRequest(
    string Code, string Model, string? Manufacturer, string? SerialNumber, string? Station, SeroStatus SeroDedication,
    DialysisMachineStatus Status, bool IsActive, uint? Version);

public sealed record RecordDisinfectionRequest(string Method);

public sealed record DialysisProfileDto(
    Guid Id, Guid PatientId, string Uhid, string PatientName, Gender Gender, string? Age, decimal DryWeightKg, VascularAccessType VascularAccess,
    string? AccessSite, DateOnly? AccessCreatedOn, IReadOnlyList<Weekday> ScheduleDays, DialysisShift Shift, int SessionMinutes, SeroStatus SeroStatus,
    DateOnly? SeroTestedOn, bool SeroRescreeningDue, DialyzerReusePolicy ReusePolicy, int MaxDialyzerUses, int EffectiveMaxDialyzerUses,
    Guid? NephrologistId, string? NephrologistName, string? PrimaryDiagnosis, string? AnticoagulationNotes, DateOnly EnrolledOn, bool IsActive, uint Version);

public sealed record SaveDialysisProfileRequest(
    decimal DryWeightKg, VascularAccessType VascularAccess, string? AccessSite, DateOnly? AccessCreatedOn, IReadOnlyList<Weekday> ScheduleDays,
    DialysisShift Shift, int SessionMinutes, SeroStatus SeroStatus, DateOnly SeroTestedOn, DialyzerReusePolicy ReusePolicy, int MaxDialyzerUses,
    Guid? NephrologistId, string? PrimaryDiagnosis, string? AnticoagulationNotes, bool IsActive, uint? Version);

public sealed record DialyzerDto(
    Guid Id, Guid PatientId, string SerialNumber, string? Brand, string? Model, decimal? SurfaceAreaM2, int UsesCount, int MaxUses, int RemainingUses,
    DateTimeOffset? FirstUsedAt, DateTimeOffset? LastUsedAt, bool IsDiscarded, DateTimeOffset? DiscardedAt, string? DiscardReason);

public sealed record RegisterDialyzerRequest(string SerialNumber, string? Brand, string? Model, decimal? SurfaceAreaM2);

public sealed record DialysisReasonRequest(string Reason);

public sealed record ScheduleDialysisRequest(Guid PatientId, DateOnly ScheduledDate, DialysisShift Shift, Guid? MachineId, Guid? VisitId, Guid? AdmissionId);

public sealed record AssignDialysisMachineRequest(Guid MachineId);

public sealed record PreDialysisRequest(decimal WeightKg, int Systolic, int Diastolic, int Pulse, decimal? TemperatureC, int? TargetUfMl);

/// <summary>Start with an existing (reused) dialyzer of this patient, or register a new one at the chair.</summary>
public sealed record StartDialysisRequest(Guid MachineId, Guid? DialyzerId, RegisterDialyzerRequest? NewDialyzer, int? HeparinLoadingUnits, int? HeparinHourlyUnits);

public sealed record AddDialysisReadingsRequest(IReadOnlyList<DialysisReading> Readings);

public sealed record CompleteDialysisRequest(
    decimal WeightKg, int Systolic, int Diastolic, int? Pulse, int? UfAchievedMl, IReadOnlyList<DialysisComplication>? Complications, string? ComplicationNotes);

public sealed record CancelDialysisRequest(string Reason, bool PatientDidNotCome);

public sealed record DialysisSessionListItem(
    Guid Id, string SessionNumber, Guid PatientId, string Uhid, string PatientName, DateOnly ScheduledDate, DialysisShift Shift,
    DialysisSessionStatus Status, Guid? MachineId, string? MachineCode, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, ChargeStatus ChargeStatus);

public sealed record DialysisSessionDetail(
    Guid Id, string SessionNumber, Guid PatientId, string Uhid, string PatientName, SeroStatus? SeroStatus, decimal? DryWeightKg, Guid? VisitId,
    Guid? AdmissionId, DateOnly ScheduledDate, DialysisShift Shift, DialysisSessionStatus Status, Guid? MachineId, string? MachineCode,
    decimal? PreWeightKg, int? PreSystolic, int? PreDiastolic, int? PrePulse, decimal? PreTemperatureC, int? TargetUfMl, DateTimeOffset? PreAssessedAt,
    IReadOnlyList<DialysisReading> Readings, decimal? PostWeightKg, int? PostSystolic, int? PostDiastolic, int? PostPulse, int? UfAchievedMl,
    int? HeparinLoadingUnits, int? HeparinHourlyUnits, Guid? DialyzerId, string? DialyzerSerial, int? DialyzerUseNumber, int? DialyzerMaxUses,
    IReadOnlyList<DialysisComplication> Complications, string? ComplicationNotes, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, int? DurationMinutes,
    string? PerformedByName, string? CancelReason, ChargeStatus ChargeStatus, string? ChargeError, uint Version);

public sealed record DialysisBoardMachine(
    Guid Id, string Code, string? Station, SeroStatus SeroDedication, DialysisMachineStatus Status, bool NeedsDisinfection,
    IReadOnlyList<DialysisSessionListItem> Sessions);

public sealed record DialysisDuePatient(Guid PatientId, string Uhid, string PatientName, SeroStatus SeroStatus, DialysisShift Shift, bool SeroRescreeningDue);

/// <summary>Schedule board for one day (optionally one shift): machines by sero category, unassigned sessions, and patients due but not booked.</summary>
public sealed record DialysisBoard(
    DateOnly Date, DialysisShift? Shift, IReadOnlyList<DialysisBoardMachine> Machines, IReadOnlyList<DialysisSessionListItem> Unassigned,
    IReadOnlyList<DialysisDuePatient> DueNotScheduled);

public sealed class SaveDialysisMachineRequestValidator : AbstractValidator<SaveDialysisMachineRequest>
{
    public SaveDialysisMachineRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, digits and hyphens (for example HD-01).");
        RuleFor(x => x.Model).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Manufacturer).MaximumLength(100);
        RuleFor(x => x.SerialNumber).MaximumLength(64);
        RuleFor(x => x.Station).MaximumLength(64);
    }
}

public sealed class RecordDisinfectionRequestValidator : AbstractValidator<RecordDisinfectionRequest>
{
    public RecordDisinfectionRequestValidator() => RuleFor(x => x.Method).NotEmpty().MaximumLength(100);
}

public sealed class SaveDialysisProfileRequestValidator : AbstractValidator<SaveDialysisProfileRequest>
{
    public SaveDialysisProfileRequestValidator(TimeProvider time)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1);
        RuleFor(x => x.DryWeightKg).InclusiveBetween(10m, 250m).WithMessage("Enter a dry weight between 10 and 250 kg.");
        RuleFor(x => x.AccessSite).SafeText(200);
        RuleFor(x => x.ScheduleDays).NotEmpty().WithMessage("Choose the dialysis days.")
            .Must(d => d.Distinct().Count() == d.Count && d.Count <= 7).WithMessage("Choose each day once.");
        RuleFor(x => x.SessionMinutes).InclusiveBetween(60, 720);
        RuleFor(x => x.SeroTestedOn).LessThanOrEqualTo(today).WithMessage("The sero test date cannot be in the future.");
        RuleFor(x => x.MaxDialyzerUses).InclusiveBetween(1, 20);
        RuleFor(x => x.PrimaryDiagnosis).SafeText(300);
        RuleFor(x => x.AnticoagulationNotes).SafeText(1000);
    }
}

public sealed class RegisterDialyzerRequestValidator : AbstractValidator<RegisterDialyzerRequest>
{
    public RegisterDialyzerRequestValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Brand).MaximumLength(100);
        RuleFor(x => x.Model).MaximumLength(100);
        RuleFor(x => x.SurfaceAreaM2).InclusiveBetween(0.1m, 3m);
    }
}

public sealed class DialysisReasonRequestValidator : AbstractValidator<DialysisReasonRequest>
{
    public DialysisReasonRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class ScheduleDialysisRequestValidator : AbstractValidator<ScheduleDialysisRequest>
{
    public ScheduleDialysisRequestValidator() => RuleFor(x => x.PatientId).NotEmpty();
}

public sealed class AssignDialysisMachineRequestValidator : AbstractValidator<AssignDialysisMachineRequest>
{
    public AssignDialysisMachineRequestValidator() => RuleFor(x => x.MachineId).NotEmpty();
}

public sealed class PreDialysisRequestValidator : AbstractValidator<PreDialysisRequest>
{
    public PreDialysisRequestValidator()
    {
        RuleFor(x => x.WeightKg).WeightKg();
        RuleFor(x => x.Systolic).Systolic();
        RuleFor(x => x.Diastolic).Diastolic();
        RuleFor(x => x.Pulse).Pulse();
        RuleFor(x => x.TemperatureC).TemperatureC();
        RuleFor(x => x.TargetUfMl).InclusiveBetween(0, 6000).WithMessage("Target ultrafiltration must be between 0 and 6000 ml.");
    }
}

public sealed class StartDialysisRequestValidator : AbstractValidator<StartDialysisRequest>
{
    public StartDialysisRequestValidator()
    {
        RuleFor(x => x.MachineId).NotEmpty();
        RuleFor(x => x).Must(x => (x.DialyzerId is null) != (x.NewDialyzer is null))
            .WithName("dialyzerId").WithMessage("Choose the patient's dialyzer or register a new one (not both).");
        RuleFor(x => x.NewDialyzer!).SetValidator(new RegisterDialyzerRequestValidator()).When(x => x.NewDialyzer is not null);
        RuleFor(x => x.HeparinLoadingUnits).InclusiveBetween(0, 10_000);
        RuleFor(x => x.HeparinHourlyUnits).InclusiveBetween(0, 5_000);
    }
}

public sealed class AddDialysisReadingsRequestValidator : AbstractValidator<AddDialysisReadingsRequest>
{
    public AddDialysisReadingsRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Readings).NotEmpty().Must(r => r.Count <= 24).WithMessage("Send between 1 and 24 readings.");
        RuleForEach(x => x.Readings).ChildRules(r =>
        {
            r.RuleFor(v => v.At).NotInFuture(time);
            r.RuleFor(v => v.Systolic).Systolic();
            r.RuleFor(v => v.Diastolic).Diastolic();
            r.RuleFor(v => v.Pulse).Pulse();
            r.RuleFor(v => v.BloodFlowMlMin).InclusiveBetween(0, 600);
            r.RuleFor(v => v.UfRateMlHr).InclusiveBetween(0, 3000);
            r.RuleFor(v => v.VenousPressure).InclusiveBetween(-500, 500);
            r.RuleFor(v => v.ArterialPressure).InclusiveBetween(-500, 500);
            r.RuleFor(v => v.TransmembranePressure).InclusiveBetween(-500, 800);
            r.RuleFor(v => v.Notes).SafeText(300);
        });
    }
}

public sealed class CompleteDialysisRequestValidator : AbstractValidator<CompleteDialysisRequest>
{
    public CompleteDialysisRequestValidator()
    {
        RuleFor(x => x.WeightKg).WeightKg();
        RuleFor(x => x.Systolic).Systolic();
        RuleFor(x => x.Diastolic).Diastolic();
        RuleFor(x => x.Pulse).Pulse();
        RuleFor(x => x.UfAchievedMl).InclusiveBetween(0, 8000);
        RuleFor(x => x.ComplicationNotes).SafeText(1000);
    }
}

public sealed class CancelDialysisRequestValidator : AbstractValidator<CancelDialysisRequest>
{
    public CancelDialysisRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}
