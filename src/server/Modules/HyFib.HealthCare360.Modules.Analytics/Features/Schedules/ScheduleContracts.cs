using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Services;

namespace HyFib.HealthCare360.Modules.Analytics.Features.Schedules;

/// <summary>A scheduled report e-mail or BI extract as shown in its screen.</summary>
/// <param name="ExtractName">The extract's file name without ".csv"; null for e-mails.</param>
/// <param name="ExtractFile">The file the extract writes in the facility's folder (<see cref="ExtractFolderDto.FacilityFolder"/>).</param>
/// <param name="LastSucceededAt">When the file was last produced: how fresh an extract's data is.</param>
/// <param name="ExtractState">How fresh the extract's data is; null for e-mails.</param>
public sealed record ScheduleDto(
    Guid Id,
    string Name,
    string ReportId,
    string ReportTitle,
    IReadOnlyDictionary<string, string> Parameters,
    ExportFormat Format,
    ScheduleFrequency Frequency,
    string Time,
    Weekday? DayOfWeek,
    int? DayOfMonth,
    string When,
    IReadOnlyList<string> Recipients,
    bool IsActive,
    string OwnerName,
    bool IsMine,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    ScheduleRunStatus? LastStatus,
    string? LastError,
    uint Version,
    ScheduleDestination Destination,
    string? ExtractName,
    string? ExtractFile,
    DateTimeOffset? LastSucceededAt,
    ExtractState? ExtractState);

/// <summary>
/// Create or change a schedule. <c>Format</c> is the attachment: Xlsx, Csv or Pdf (a BI extract is always Csv).
/// <c>Time</c> is the IST time as "HH:mm" (for hourly extracts, only its minutes count). Weekly schedules need
/// <c>DayOfWeek</c>; monthly schedules need <c>DayOfMonth</c> (1–28, or 31 for the last day). Report inputs must use a
/// relative period. An e-mail needs <c>Recipients</c>; a BI extract (<c>Destination</c> ExtractFolder) needs
/// <c>ExtractName</c> instead.
/// </summary>
public sealed record SaveScheduleRequest(
    string Name,
    string ReportId,
    Dictionary<string, string?>? Parameters,
    ExportFormat Format,
    ScheduleFrequency Frequency,
    string Time,
    Weekday? DayOfWeek,
    int? DayOfMonth,
    IReadOnlyList<string>? Recipients,
    bool IsActive = true,
    uint? Version = null,
    ScheduleDestination Destination = ScheduleDestination.Email,
    string? ExtractName = null)
{
    public bool IsExtract => Destination == ScheduleDestination.ExtractFolder;
}

/// <summary>Where BI extracts are written: the folder IT shares with the BI tool.</summary>
/// <param name="FacilityFolder">The signed-in user's facility's sub-folder, which holds its extracts.</param>
/// <param name="RefreshLog">The file listing when each extract was last refreshed.</param>
/// <param name="Problem">Null when HC360 can write the folder; otherwise what is wrong.</param>
public sealed record ExtractFolderDto(string Root, string? FacilityFolder, string RefreshLog, string? Problem);

public sealed record DeliveryRecipientDto(string Email, RecipientStatus Status, int Attempts, string? LastError, DateTimeOffset? SentAt);

/// <summary>One generated file of a schedule and what happened to its e-mails.</summary>
public sealed record DeliveryDto(
    Guid Id,
    Guid ScheduleId,
    string ReportTitle,
    DeliveryTrigger Trigger,
    DateTimeOffset CreatedAt,
    DeliveryStatus Status,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    ExportFormat Format,
    string? FileName,
    long SizeBytes,
    int RowCount,
    bool FileAvailable,
    string? Error,
    int Sent,
    int Pending,
    IReadOnlyList<DeliveryRecipientDto> Recipients);

public sealed class SaveScheduleRequestValidator : AbstractValidator<SaveScheduleRequest>
{
    public SaveScheduleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Give the schedule a name.").SafeText(100);
        RuleFor(x => x.ReportId).NotEmpty().WithMessage("Choose a report.").MaximumLength(64);
        RuleFor(x => x.Format).IsInEnum().WithMessage("Choose Excel (xlsx), CSV or PDF.");
        RuleFor(x => x.Frequency).IsInEnum();
        RuleFor(x => x.Time).NotEmpty().Matches("^([01][0-9]|2[0-3]):[0-5][0-9]$").WithMessage("Enter the time as HH:mm (24-hour clock, IST).");
        RuleFor(x => x.DayOfWeek).NotNull().IsInEnum().When(x => x.Frequency == ScheduleFrequency.Weekly).WithMessage("Choose the day of the week.");
        RuleFor(x => x.DayOfMonth)
            .Must(d => d is >= 1 and <= 28 or ScheduleCalendar.LastDayOfMonth)
            .When(x => x.Frequency == ScheduleFrequency.Monthly)
            .WithMessage("Choose a day from 1 to 28, or the last day of the month.");
        RuleFor(x => x.Destination).IsInEnum();
        RuleFor(x => x.Parameters).Must(p => p is null || p.Count <= 30).WithMessage("Too many report inputs.");

        When(x => !x.IsExtract, () =>
        {
            RuleFor(x => x.Frequency).NotEqual(ScheduleFrequency.Hourly).WithMessage("E-mails go daily, weekly or monthly.");
            RuleFor(x => x.Recipients).NotEmpty().WithMessage("Add at least one e-mail address.")
                .Must(r => r is null || r.Count <= ReportSchedule.MaxRecipients).WithMessage($"Add at most {ReportSchedule.MaxRecipients} e-mail addresses.");
            RuleForEach(x => x.Recipients).NotEmpty().MaximumLength(254).EmailAddress().WithMessage("Enter a valid e-mail address.");
        });

        When(x => x.IsExtract, () =>
        {
            RuleFor(x => x.Format).Equal(ExportFormat.Csv).WithMessage("BI extracts are CSV files.");
            RuleFor(x => x.ExtractName)
                .Must(name => ExtractFolder.HasValidForm(name?.Trim().ToLowerInvariant()))
                .WithMessage($"Name the file with lower-case letters, digits and dashes (at most {ReportSchedule.MaxExtractNameLength}), for example daily-revenue.")
                .Must(name => !ExtractFolder.IsDeviceName(name?.Trim().ToLowerInvariant()))
                .WithMessage(ExtractFolder.DeviceNameMessage);
        });
    }
}
