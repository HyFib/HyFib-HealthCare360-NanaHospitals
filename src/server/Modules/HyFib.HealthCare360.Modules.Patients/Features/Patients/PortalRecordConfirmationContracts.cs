using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

/// <summary>
/// The records registered with the portal account's number that wait to be confirmed before the portal shows them, and the
/// wrong tries the number has left (none while <paramref name="RetryAfterSeconds"/> counts down to the next). Only a count:
/// no name or anything else of a record is told before it is confirmed.
/// </summary>
public sealed record RecordsToConfirmDto(int Unconfirmed, int AttemptsLeft, int? RetryAfterSeconds);

/// <summary>
/// The patient's date of birth, or the UHID printed on their hospital card, bills and receipts. With both, a record must
/// match both.
/// </summary>
public sealed record ConfirmRecordsRequest(DateOnly? DateOfBirth, string? Uhid);

/// <summary>How many records the details confirmed (the portal shows them now), and how many still wait.</summary>
public sealed record RecordsConfirmedDto(int Confirmed, int Unconfirmed);

public sealed class ConfirmRecordsRequestValidator : AbstractValidator<ConfirmRecordsRequest>
{
    public const string DetailsRequiredMessage = "Enter the patient's date of birth, or the UHID printed on the hospital card.";
    public const string UhidMessage = "Enter the UHID printed on the hospital card or a bill.";

    public ConfirmRecordsRequestValidator(TimeProvider time)
    {
        // The hospital's date (IST): a baby born after midnight there was born today.
        var today = time.Today();
        RuleFor(x => x.DateOfBirth).NotNull().When(x => string.IsNullOrWhiteSpace(x.Uhid)).WithMessage(DetailsRequiredMessage);
        RuleFor(x => x.DateOfBirth).Must(d => d is null || (d <= today && d.Value.Year >= 1900)).WithMessage("Enter a date of birth that has passed.");
        RuleFor(x => x.Uhid).MaximumLength(20).WithMessage(UhidMessage).Matches("^[A-Za-z0-9-]+$").WithMessage(UhidMessage)
            .When(x => !string.IsNullOrWhiteSpace(x.Uhid));
    }
}
