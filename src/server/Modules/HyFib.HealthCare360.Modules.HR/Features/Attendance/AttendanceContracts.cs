using System.Net;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Features.Attendance;

public sealed record BiometricDeviceDto(
    Guid Id,
    string SerialNumber,
    string Name,
    string? Location,
    Guid? DepartmentId,
    string? Model,
    string? AllowedIpAddress,
    bool HasApiKey,
    bool IsActive,
    DateTimeOffset? LastPushAt,
    DateTimeOffset? LastHandshakeAt,
    string? LastIpAddress,
    bool IsOnline,
    uint Version,
    BiometricConnection Connection,
    string? PullAddress,
    int PullPort,
    int PullEveryMinutes,
    DateTimeOffset? LastPullAt,
    string? LastPullError,
    int? ClockOffsetSeconds,
    DateTimeOffset? OfflineSince);

/// <param name="Connection">Push (the terminal sends its punches) unless HC360 is to read it.</param>
/// <param name="PullAddress">A terminal HC360 reads: its IP address on the device network.</param>
public sealed record SaveBiometricDeviceRequest(
    string SerialNumber,
    string Name,
    string? Location,
    Guid? DepartmentId,
    string? Model,
    string? AllowedIpAddress,
    bool IsActive,
    uint? Version,
    BiometricConnection Connection = BiometricConnection.Push,
    string? PullAddress = null,
    int? PullPort = null,
    int? PullEveryMinutes = null);

/// <summary>
/// The device key is returned only once; HC360 stores just its hash. A terminal HC360 reads gets none (null): it
/// sends nothing.
/// </summary>
public sealed record DeviceKeyIssued(Guid DeviceId, string SerialNumber, string? ApiKey);

/// <summary>Training: the employee number to punch with on the simulated terminal.</summary>
public sealed record TrainingPunchRequest(string Pin);

/// <summary>Setting a terminal's clock: null error when it was set.</summary>
public sealed record TerminalClockResult(bool Success, string? Error);

public sealed record DevicePunchInput(string Pin, DateTimeOffset PunchedAt, PunchDirection? Direction, int? VerifyMode);

/// <summary>JSON punch upload for terminals/middleware that do not speak ADMS (headers X-Device-Serial and X-Device-Key).</summary>
public sealed record DevicePunchBatch(IReadOnlyList<DevicePunchInput> Punches);

public sealed record PunchDto(
    Guid Id,
    Guid? EmployeeId,
    string? EmployeeName,
    string Pin,
    DateTimeOffset PunchedAt,
    PunchDirection Direction,
    int? VerifyMode,
    PunchSource Source,
    string? DeviceName);

public sealed record UnmatchedPinDto(string Pin, int PunchCount, DateTimeOffset FirstPunchAt, DateTimeOffset LastPunchAt);

public sealed record AttendanceDayDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    DateOnly Date,
    AttendanceStatus Status,
    string? ShiftCode,
    DateTimeOffset? FirstIn,
    DateTimeOffset? LastOut,
    int WorkedMinutes,
    int LateMinutes,
    int EarlyExitMinutes,
    int OvertimeMinutes,
    decimal PresentDays,
    decimal PaidLeaveDays,
    decimal UnpaidLeaveDays,
    decimal AbsentDays,
    decimal OffDays,
    AttendanceFlags Flags,
    int PunchCount,
    bool IsLocked);

public sealed record AttendanceSummaryRow(
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string? DepartmentName,
    int RecordedDays,
    decimal PresentDays,
    decimal PaidLeaveDays,
    decimal UnpaidLeaveDays,
    decimal AbsentDays,
    decimal OffDays,
    int LateDays,
    int LateMinutes,
    int OvertimeMinutes,
    int MissingPunchDays);

public sealed record ComputeAttendanceRequest(DateOnly From, DateOnly To, Guid? EmployeeId);

public sealed record ComputeAttendanceResult(int EmployeeDays);

public sealed record RegularisationDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    DateOnly Date,
    RegularisationKind Kind,
    TimeOnly? RequestedIn,
    TimeOnly? RequestedOut,
    string Reason,
    ApprovalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string? DecisionNote);

public sealed record RequestRegularisationRequest(DateOnly Date, RegularisationKind Kind, TimeOnly? RequestedIn, TimeOnly? RequestedOut, string Reason);

/// <summary>Approve/reject payload (a note is mandatory for rejections).</summary>
public sealed record DecisionRequest(string? Note);

public sealed class SaveBiometricDeviceRequestValidator : AbstractValidator<SaveBiometricDeviceRequest>
{
    public SaveBiometricDeviceRequestValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().Must(AdmsProtocol.IsValidSerial).WithMessage("Enter the serial number exactly as shown on the terminal.");
        RuleFor(x => x.Name).NotEmpty().SafeText(100);
        RuleFor(x => x.Location).SafeText(150);
        RuleFor(x => x.Model).SafeText(64);
        RuleFor(x => x.AllowedIpAddress)
            .Must(ip => string.IsNullOrWhiteSpace(ip) || IPAddress.TryParse(ip.Trim(), out _))
            .WithMessage("Enter a valid IP address.");
        RuleFor(x => x.Connection).IsInEnum();
        When(x => x.Connection == BiometricConnection.Pull, () =>
        {
            RuleFor(x => x.PullAddress)
                .Must(BiometricDevice.IsAllowedPullAddress)
                .WithMessage("Enter the terminal's IP address on the device network, for example 192.168.1.201.");
            RuleFor(x => x.PullPort).InclusiveBetween(1, 65535).When(x => x.PullPort is not null)
                .WithMessage("Enter the terminal's port, 4370 unless it was changed on the terminal.");
            RuleFor(x => x.PullEveryMinutes).InclusiveBetween(1, BiometricDevice.MaxPullMinutes).When(x => x.PullEveryMinutes is not null)
                .WithMessage($"Read the terminal every 1 to {BiometricDevice.MaxPullMinutes} minutes.");
        });
    }
}

public sealed class TrainingPunchRequestValidator : AbstractValidator<TrainingPunchRequest>
{
    public TrainingPunchRequestValidator() =>
        RuleFor(x => x.Pin).NotEmpty().Must(pin => ZkTecoAttLogParser.IsValidPin(pin?.Trim()))
            .WithMessage("Enter the employee's number on the terminal (letters and digits).");
}

public sealed class DevicePunchBatchValidator : AbstractValidator<DevicePunchBatch>
{
    public const int MaxPunches = 1000;

    public DevicePunchBatchValidator()
    {
        RuleFor(x => x.Punches).NotEmpty().Must(p => p.Count <= MaxPunches).WithMessage($"Send at most {MaxPunches} punches per request.");
        RuleForEach(x => x.Punches).ChildRules(punch =>
        {
            punch.RuleFor(p => p.Pin).Must(ZkTecoAttLogParser.IsValidPin).WithMessage("Invalid PIN.");
            punch.RuleFor(p => p.VerifyMode).InclusiveBetween(0, 255);
        });
    }
}

public sealed class ComputeAttendanceRequestValidator : AbstractValidator<ComputeAttendanceRequest>
{
    public ComputeAttendanceRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From).WithMessage("The end date is before the start date.");
        RuleFor(x => x.To)
            .Must((x, to) => to.DayNumber - x.From.DayNumber < AttendanceService.MaxRangeDays)
            .WithMessage($"Compute at most {AttendanceService.MaxRangeDays} days at a time.");
        RuleFor(x => x.To).Must(to => to <= time.LocalToday()).WithMessage("Attendance cannot be computed for future dates.");
    }
}

public sealed class RequestRegularisationRequestValidator : AbstractValidator<RequestRegularisationRequest>
{
    public RequestRegularisationRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Date).Must(d => d <= time.LocalToday()).WithMessage("You can regularise past days only.");
        RuleFor(x => x.Date).Must(d => d >= time.LocalToday().AddDays(-RegularisationService.MaxAgeDays))
            .WithMessage($"Requests must be raised within {RegularisationService.MaxAgeDays} days.");
        RuleFor(x => x)
            .Must(x => x.Kind != RegularisationKind.MissedPunch || x.RequestedIn is not null || x.RequestedOut is not null)
            .WithName("requestedIn").WithMessage("Enter the missed in-time and/or out-time.");
        RuleFor(x => x.Reason).NotEmpty().SafeText(500);
    }
}

public sealed class DecisionRequestValidator : AbstractValidator<DecisionRequest>
{
    public DecisionRequestValidator() => RuleFor(x => x.Note).SafeText(500);
}
