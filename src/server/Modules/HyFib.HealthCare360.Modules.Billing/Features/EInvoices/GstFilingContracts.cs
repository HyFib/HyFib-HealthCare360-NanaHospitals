using FluentValidation;

namespace HyFib.HealthCare360.Modules.Billing.Features.EInvoices;

/// <summary>That the month's GSTR-1 was filed on the portal: the month (2026-10), the ARN and the day it was filed.</summary>
public sealed record MarkGstr1FiledRequest(string Month, string Arn, DateOnly FiledOn);

/// <summary>A correction of a recorded filing (a mistyped ARN or date), with the reason.</summary>
public sealed record CorrectGstr1FilingRequest(string Month, string Arn, DateOnly FiledOn, string Reason, uint Version);

public sealed class MarkGstr1FiledRequestValidator : AbstractValidator<MarkGstr1FiledRequest>
{
    public MarkGstr1FiledRequestValidator()
    {
        RuleFor(x => x.Month).NotEmpty().Matches(@"^\d{4}-\d{2}$").WithMessage("Choose the month, as 2026-10.");
        RuleFor(x => x.Arn).NotEmpty().Matches("^[A-Za-z0-9]{15}$").WithMessage("The ARN is the 15 letters and digits the portal gave on filing.");
    }
}

public sealed class CorrectGstr1FilingRequestValidator : AbstractValidator<CorrectGstr1FilingRequest>
{
    public CorrectGstr1FilingRequestValidator()
    {
        RuleFor(x => x.Month).NotEmpty().Matches(@"^\d{4}-\d{2}$").WithMessage("Choose the month, as 2026-10.");
        RuleFor(x => x.Arn).NotEmpty().Matches("^[A-Za-z0-9]{15}$").WithMessage("The ARN is the 15 letters and digits the portal gave on filing.");
        RuleFor(x => x.Reason).NotEmpty().SafeText(500);
        RuleFor(x => x.Version).SeenVersion();
    }
}
