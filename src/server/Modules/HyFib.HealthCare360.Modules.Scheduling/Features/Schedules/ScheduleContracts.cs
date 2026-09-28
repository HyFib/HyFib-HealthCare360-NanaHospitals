using FluentValidation.Results;
using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Schedules;

public sealed record ScheduleDto(
    Guid Id,
    Guid FacilityId,
    Guid PractitionerId,
    string PractitionerName,
    Guid DepartmentId,
    string? DepartmentName,
    Weekday Weekday,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int SlotMinutes,
    int CapacityPerSlot,
    int SlotsPerSession,
    ConsultationMode Mode,
    string? Room,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive,
    string? Notes,
    uint Version);

/// <summary>Creates the same session on several weekdays at once (for example Monday–Saturday 09:00–13:00).</summary>
public sealed record CreateSchedulesRequest(
    Guid PractitionerId,
    Guid DepartmentId,
    IReadOnlyList<Weekday> Weekdays,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int SlotMinutes,
    int CapacityPerSlot,
    ConsultationMode Mode,
    string? Room,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? Notes);

public sealed record UpdateScheduleRequest(
    Guid DepartmentId,
    Weekday Weekday,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int SlotMinutes,
    int CapacityPerSlot,
    ConsultationMode Mode,
    string? Room,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive,
    string? Notes,
    uint Version);

public sealed record SchedulesCreated(IReadOnlyList<Guid> Ids);

/// <summary>Bookings that fall inside a new leave/block or outside an edited session: the desk should reschedule them.</summary>
public sealed record AffectedAppointment(Guid Id, string Number, Guid PatientId, string PatientName, string? PhoneMasked, DateTimeOffset StartsAt, AppointmentStatus Status);

public sealed record ScheduleSaved(Guid Id, IReadOnlyList<AffectedAppointment> AffectedAppointments);

public sealed record ScheduleExceptionDto(
    Guid Id,
    Guid PractitionerId,
    string PractitionerName,
    Guid? FacilityId,
    ScheduleExceptionType Type,
    DateOnly FromDate,
    DateOnly ToDate,
    bool IsAllDay,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string Reason,
    Guid? DepartmentId,
    int? SlotMinutes,
    int? CapacityPerSlot,
    ConsultationMode? Mode,
    string? Room,
    uint Version);

/// <param name="AllFacilities">Leave normally applies at every branch; blocks and extra sessions belong to the current facility.</param>
public sealed record SaveScheduleExceptionRequest(
    Guid PractitionerId,
    ScheduleExceptionType Type,
    DateOnly FromDate,
    DateOnly ToDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string Reason,
    bool AllFacilities,
    Guid? DepartmentId,
    int? SlotMinutes,
    int? CapacityPerSlot,
    ConsultationMode? Mode,
    string? Room,
    uint? Version);

public sealed class CreateSchedulesRequestValidator : AbstractValidator<CreateSchedulesRequest>
{
    public CreateSchedulesRequestValidator()
    {
        RuleFor(x => x.PractitionerId).NotEmpty().WithMessage("Choose a doctor.");
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("Choose a department.");
        RuleFor(x => x.Weekdays).NotEmpty().WithMessage("Choose at least one weekday.");
        RuleForEach(x => x.Weekdays).IsInEnum();
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.Room).MaximumLength(32);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x).Custom((x, context) =>
            context.AddDomainFailures(DoctorSchedule.Validate(x.StartTime, x.EndTime, x.SlotMinutes, x.CapacityPerSlot, x.EffectiveFrom, x.EffectiveTo)));
    }
}

public sealed class UpdateScheduleRequestValidator : AbstractValidator<UpdateScheduleRequest>
{
    public UpdateScheduleRequestValidator()
    {
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("Choose a department.");
        RuleFor(x => x.Weekday).IsInEnum();
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.Room).MaximumLength(32);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x).Custom((x, context) =>
            context.AddDomainFailures(DoctorSchedule.Validate(x.StartTime, x.EndTime, x.SlotMinutes, x.CapacityPerSlot, x.EffectiveFrom, x.EffectiveTo)));
    }
}

public sealed class SaveScheduleExceptionRequestValidator : AbstractValidator<SaveScheduleExceptionRequest>
{
    public SaveScheduleExceptionRequestValidator()
    {
        RuleFor(x => x.PractitionerId).NotEmpty().WithMessage("Choose a doctor.");
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter a reason.").SafeText(300);
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.Room).MaximumLength(32);
        RuleFor(x => x.AllFacilities).Equal(false)
            .When(x => x.Type == ScheduleExceptionType.ExtraSession)
            .WithMessage("An extra session belongs to one facility.");
        RuleFor(x => x).Custom((x, context) =>
            context.AddDomainFailures(ScheduleException.Validate(
                x.Type, x.FromDate, x.ToDate, x.StartTime, x.EndTime, x.DepartmentId, x.SlotMinutes, x.CapacityPerSlot)));
    }
}

/// <summary>Turns a domain <see cref="Result"/> into FluentValidation failures so the UI highlights the same fields.</summary>
internal static class DomainValidation
{
    public static void AddDomainFailures<T>(this ValidationContext<T> context, Result result)
    {
        if (result.Error is not { } error)
        {
            return;
        }

        if (error.Fields is { Count: > 0 } fields)
        {
            foreach (var (field, messages) in fields)
            {
                foreach (var message in messages)
                {
                    context.AddFailure(new ValidationFailure(field, message));
                }
            }

            return;
        }

        context.AddFailure(new ValidationFailure(string.Empty, error.Message));
    }
}
