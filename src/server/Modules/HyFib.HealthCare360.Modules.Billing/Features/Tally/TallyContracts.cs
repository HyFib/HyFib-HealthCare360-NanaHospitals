using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Tally;

public sealed record TallyExportDto(
    Guid Id,
    DateOnly Date,
    TallyExportStatus Status,
    int VoucherCount,
    int Attempts,
    int CreatedCount,
    int AlteredCount,
    int ErrorCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? PostedAt,
    string? Message);

/// <summary>Queue (or re-queue) the day book of a date for posting to Tally.</summary>
public sealed record PostDayBookRequest(DateOnly Date);

public sealed class PostDayBookRequestValidator : AbstractValidator<PostDayBookRequest>
{
    public PostDayBookRequestValidator(TimeProvider time) =>
        RuleFor(x => x.Date)
            .Must(date => date <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1))
            .WithMessage("The day book of a future date cannot be posted.");
}
