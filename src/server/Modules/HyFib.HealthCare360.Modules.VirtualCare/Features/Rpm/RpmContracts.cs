using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;

public sealed record RpmProgramDto(Guid Id, string Code, string Name, RpmProgramKind Kind, string? Description, int ExpectedReadingsPerDay, bool IsActive, uint Version);

public sealed record SaveRpmProgramRequest(string Code, string Name, RpmProgramKind Kind, string? Description, int ExpectedReadingsPerDay, bool IsActive, uint? Version);

public sealed record ThresholdDto(RpmReadingType Type, string Label, string Unit, decimal? Min, decimal? Max, decimal? CriticalMin, decimal? CriticalMax)
{
    public static ThresholdDto From(RpmReadingType type, ThresholdRange range) =>
        new(type, ReadingUnits.Label(type), ReadingUnits.CanonicalUnit(type), range.Min, range.Max, range.CriticalMin, range.CriticalMax);
}

public sealed record CareTeamMember(Guid PractitionerId, string Name, bool IsPrimary);

public sealed record RpmDeviceDto(
    Guid Id,
    string SerialNumber,
    RpmDeviceType Type,
    string? Manufacturer,
    string? Model,
    Guid? EnrollmentId,
    Guid? PatientId,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? ApiKeyIssuedAt,
    bool IsActive,
    bool SilenceAlerted,
    uint Version);

public sealed record RpmEnrollmentListItem(
    Guid Id,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    Guid ProgramId,
    string ProgramName,
    RpmProgramKind ProgramKind,
    RpmEnrollmentStatus Status,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateTimeOffset? LastReadingAt,
    int OpenAlerts,
    int CriticalAlerts);

public sealed record RpmEnrollmentDetail(
    Guid Id,
    Guid FacilityId,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string? PhoneMasked,
    Guid ProgramId,
    string ProgramName,
    RpmProgramKind ProgramKind,
    RpmEnrollmentStatus Status,
    string? StatusReason,
    DateOnly StartDate,
    DateOnly? EndDate,
    string? Goals,
    string? Notes,
    IReadOnlyList<CareTeamMember> CareTeam,
    IReadOnlyList<ThresholdDto> Thresholds,
    IReadOnlyList<RpmDeviceDto> Devices,
    DateTimeOffset? LastReadingAt,
    DateTimeOffset? ThresholdsUpdatedAt,
    int OpenAlerts,
    uint Version);

public sealed record EnrollPatientRequest(
    Guid PatientId,
    Guid ProgramId,
    IReadOnlyList<Guid> CareTeamPractitionerIds,
    Guid? PrimaryPractitionerId,
    DateOnly? StartDate,
    string? Goals,
    string? Notes);

public sealed record UpdateEnrollmentRequest(
    IReadOnlyList<Guid> CareTeamPractitionerIds,
    Guid? PrimaryPractitionerId,
    RpmEnrollmentStatus Status,
    string? StatusReason,
    string? Goals,
    string? Notes,
    uint Version);

public sealed record ThresholdInput(RpmReadingType Type, decimal? Min, decimal? Max, decimal? CriticalMin, decimal? CriticalMax);

/// <summary>Replaces the enrolment's limits; <paramref name="Version"/> is the enrolment's version (optimistic concurrency).</summary>
public sealed record SetThresholdsRequest(IReadOnlyList<ThresholdInput> Thresholds, uint Version);

public sealed record RpmReadingDto(
    Guid Id,
    RpmReadingType Type,
    decimal Value,
    string Unit,
    string? TextValue,
    DateTimeOffset MeasuredAt,
    DateTimeOffset ReceivedAt,
    ReadingSeverity Severity,
    ReadingSource Source,
    Guid? DeviceId);

public sealed record ManualReading(RpmReadingType Type, decimal Value, string? Unit, DateTimeOffset? MeasuredAt, string? TextValue);

public sealed record RecordReadingsRequest(IReadOnlyList<ManualReading> Readings);

/// <summary>Outcome of an ingestion batch: accepted, duplicates of earlier deliveries, and rejected lines with the reason.</summary>
public sealed record IngestResult(int Accepted, int Duplicates, IReadOnlyList<string> Rejected, int AlertsRaised);

public sealed record TrendPoint(DateTimeOffset MeasuredAt, decimal Value, ReadingSeverity Severity);

public sealed record TrendDay(DateOnly Date, decimal Min, decimal Max, decimal Average, int Count);

/// <summary>A chart series (RPM-04): raw points, daily summary and the limits to draw as bands.</summary>
public sealed record RpmTrend(RpmReadingType Type, string Label, string Unit, ThresholdDto? Threshold, IReadOnlyList<TrendPoint> Points, IReadOnlyList<TrendDay> Daily);

public sealed record DayCount(DateOnly Date, int Readings);

public sealed record RpmAdherenceDto(int WindowDays, int DaysWithReadings, decimal Percent, IReadOnlyList<DateOnly> MissedDates, int ExpectedReadingsPerDay, IReadOnlyList<DayCount> ReadingsPerDay);

public sealed record RegisterDeviceRequest(string SerialNumber, RpmDeviceType Type, string? Manufacturer, string? Model, Guid? EnrollmentId);

/// <summary>The device API key is shown exactly once; only its hash is stored.</summary>
public sealed record DeviceKeyIssued(Guid DeviceId, string SerialNumber, string ApiKey, string IngestPath, string HeaderName);

public sealed record AssignDeviceRequest(Guid? EnrollmentId);

public sealed record RpmAlertDto(
    Guid Id,
    Guid EnrollmentId,
    Guid PatientId,
    string Uhid,
    string PatientName,
    RpmAlertKind Kind,
    RpmAlertSeverity Severity,
    RpmAlertStatus Status,
    RpmReadingType? ReadingType,
    decimal? Value,
    string? Unit,
    string Message,
    DateTimeOffset RaisedAt,
    DateTimeOffset LastOccurredAt,
    int Occurrences,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt,
    string? Notes);

public sealed record AlertActionRequest(string? Notes);

public sealed record RpmDashboard(
    int ActiveEnrollments,
    int PausedEnrollments,
    int OpenAlerts,
    int CriticalAlerts,
    int UnacknowledgedCritical,
    int SilentDevices,
    int ReadingsLast24Hours,
    int EnrollmentsWithoutReadingToday);

public sealed class SaveRpmProgramRequestValidator : AbstractValidator<SaveRpmProgramRequest>
{
    public SaveRpmProgramRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches("^[A-Z0-9_-]+$").WithMessage("Use capital letters, digits, dashes or underscores.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Description).SafeText(500);
        RuleFor(x => x.ExpectedReadingsPerDay).InclusiveBetween(1, 12);
    }
}

public sealed class EnrollPatientRequestValidator : AbstractValidator<EnrollPatientRequest>
{
    public EnrollPatientRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty().WithMessage("Choose a patient.");
        RuleFor(x => x.ProgramId).NotEmpty().WithMessage("Choose a programme.");
        RuleFor(x => x.CareTeamPractitionerIds).NotEmpty().WithMessage("Choose at least one care-team member.")
            .Must(ids => ids.Count <= 10).WithMessage("A care team can have at most 10 members.");
        RuleFor(x => x.Goals).SafeText(1000);
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class UpdateEnrollmentRequestValidator : AbstractValidator<UpdateEnrollmentRequest>
{
    public UpdateEnrollmentRequestValidator()
    {
        RuleFor(x => x.CareTeamPractitionerIds).NotEmpty().WithMessage("Choose at least one care-team member.")
            .Must(ids => ids.Count <= 10).WithMessage("A care team can have at most 10 members.");
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.StatusReason).SafeText(300);
        RuleFor(x => x.Goals).SafeText(1000);
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class SetThresholdsRequestValidator : AbstractValidator<SetThresholdsRequest>
{
    public SetThresholdsRequestValidator()
    {
        RuleFor(x => x.Thresholds).NotNull().Must(t => t.Select(x => x.Type).Distinct().Count() == t.Count).WithMessage("Each reading type can appear once.");
        RuleForEach(x => x.Thresholds).ChildRules(t => t.RuleFor(x => x.Type).IsInEnum());
    }
}

public sealed class RecordReadingsRequestValidator : AbstractValidator<RecordReadingsRequest>
{
    public RecordReadingsRequestValidator()
    {
        RuleFor(x => x.Readings).NotEmpty().Must(r => r.Count <= 50).WithMessage("Record at most 50 readings at once.");
        RuleForEach(x => x.Readings).ChildRules(r =>
        {
            r.RuleFor(x => x.Type).IsInEnum();
            r.RuleFor(x => x.Unit).MaximumLength(16);
            r.RuleFor(x => x.TextValue).SafeText(200);
        });
    }
}

public sealed class RegisterDeviceRequestValidator : AbstractValidator<RegisterDeviceRequest>
{
    public RegisterDeviceRequestValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(64).Matches("^[A-Za-z0-9._:/-]+$").WithMessage("Use letters, digits and . _ : / - only.");
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Manufacturer).MaximumLength(100);
        RuleFor(x => x.Model).MaximumLength(100);
    }
}

public sealed class AlertActionRequestValidator : AbstractValidator<AlertActionRequest>
{
    public AlertActionRequestValidator() => RuleFor(x => x.Notes).SafeText(500);
}
