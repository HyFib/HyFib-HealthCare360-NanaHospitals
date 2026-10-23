using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;

public sealed record BookCheckupRequest(Guid PatientId, Guid PackageId, DateOnly ScheduledDate, Guid? VisitId, string? Notes);

/// <summary>A corporate drive: <see cref="PayerId"/> is the company, a corporate payer of the payer master.</summary>
public sealed record CorporateBatchRequest(
    Guid PayerId,
    string? ContactPerson,
    string? ContactPhone,
    string? Reference,
    Guid PackageId,
    DateOnly ScheduledDate,
    IReadOnlyList<Guid> PatientIds,
    string? Notes);

public sealed record CheckInCheckupRequest(Guid? VisitId);

public sealed class CheckInCheckupRequestValidator : AbstractValidator<CheckInCheckupRequest>
{
    public CheckInCheckupRequestValidator() => RuleFor(x => x.VisitId).OptionalId();
}

public sealed record CompleteStopRequest(string? Notes);

public sealed record SkipStopRequest(string Reason);

public sealed record CheckupSummaryRequest(string Summary, string? Recommendations, uint Version);

public sealed record CancelBookingRequest(string Reason);

public sealed record ItineraryItemDto(
    Guid Id,
    int Sequence,
    Guid? DepartmentId,
    string DepartmentName,
    string Description,
    ItineraryStatus Status,
    DateTimeOffset? CompletedAt,
    string? Notes);

public sealed record BookingDto(
    Guid Id,
    string BookingNumber,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid PackageId,
    string PackageCode,
    string PackageName,
    DateOnly ScheduledDate,
    BookingStatus Status,
    Guid? VisitId,
    Guid? OrderId,
    Guid? CorporateBatchId,
    string? CorporateName,
    Guid? CorporatePayerId,
    string? Notes,
    DateTimeOffset? CheckedInAt,
    string? Summary,
    string? Recommendations,
    Guid? ReviewedByPractitionerId,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? CompletedAt,
    string? CancelReason,
    IReadOnlyList<ItineraryItemDto> Itinerary,
    uint Version);

public sealed record BatchSkip(Guid PatientId, string Reason);

public sealed record CorporateBatchResult(Guid BatchId, string BatchNumber, int Booked, IReadOnlyList<BatchSkip> Skipped);

public sealed record CorporateBatchDto(
    Guid Id,
    string BatchNumber,
    Guid? PayerId,
    string CorporateName,
    string? ContactPerson,
    string? ContactPhone,
    string? Reference,
    Guid PackageId,
    DateOnly ScheduledDate,
    int BookedCount,
    int SkippedCount,
    string? Notes,
    DateTimeOffset CreatedAt);

/// <summary>Consolidated checkup report (MHC-04): itinerary, ordered investigations and the reviewing doctor's summary.</summary>
public sealed record CheckupReport(BookingDto Booking, PatientSummary Patient, FacilityLetterhead? Letterhead, PrescriberInfo? ReviewingDoctor, OrderDto? Order);

public sealed class BookCheckupRequestValidator : AbstractValidator<BookCheckupRequest>
{
    public BookCheckupRequestValidator(TimeProvider time)
    {
        var today = time.Today();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.PackageId).NotEmpty();
        RuleFor(x => x.ScheduledDate).InclusiveBetween(today, today.AddDays(180)).WithMessage("Choose a date from today up to six months ahead.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class CorporateBatchRequestValidator : AbstractValidator<CorporateBatchRequest>
{
    public CorporateBatchRequestValidator(TimeProvider time)
    {
        var today = time.Today();
        RuleFor(x => x.PayerId).NotEmpty().WithMessage("Choose the company.");
        RuleFor(x => x.ContactPerson).MaximumLength(200);
        RuleFor(x => x.ContactPhone).PhoneNumber();
        RuleFor(x => x.Reference).MaximumLength(100);
        RuleFor(x => x.PackageId).NotEmpty();
        RuleFor(x => x.ScheduledDate).InclusiveBetween(today, today.AddDays(365)).WithMessage("Choose a date from today up to one year ahead.");
        RuleFor(x => x.PatientIds).NotEmpty().Must(p => p is null || p.Count <= 500).WithMessage("Book between 1 and 500 employees per batch.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class SkipStopRequestValidator : AbstractValidator<SkipStopRequest>
{
    public SkipStopRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}

public sealed class CompleteStopRequestValidator : AbstractValidator<CompleteStopRequest>
{
    public CompleteStopRequestValidator() => RuleFor(x => x.Notes).SafeText(500);
}

public sealed class CheckupSummaryRequestValidator : AbstractValidator<CheckupSummaryRequest>
{
    public CheckupSummaryRequestValidator()
    {
        RuleFor(x => x.Summary).NotEmpty().SafeText(8000);
        RuleFor(x => x.Recommendations).SafeText(4000);
    }
}

public sealed class CancelBookingRequestValidator : AbstractValidator<CancelBookingRequest>
{
    public CancelBookingRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}
