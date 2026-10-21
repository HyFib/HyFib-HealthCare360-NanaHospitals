using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;

public sealed record LabTestListItem(
    Guid Id,
    string Code,
    string Name,
    string? ShortName,
    LabSection Section,
    SpecimenType SpecimenType,
    ContainerType Container,
    LabResultType ResultType,
    string? Unit,
    bool IsPanel,
    bool IsOrderable,
    bool IsProvisional,
    bool IsActive,
    int TatHours,
    Guid? ServiceItemId,
    decimal? Price,
    string? Preparation,
    int ComponentCount);

public sealed record ReferenceRangeDto(
    Guid? Id,
    Gender? Gender,
    int AgeFromDays,
    int? AgeToDays,
    decimal? Low,
    decimal? High,
    decimal? CriticalLow,
    decimal? CriticalHigh,
    string? Text,
    IReadOnlyList<string>? NormalValues,
    IReadOnlyList<string>? CriticalValues);

public sealed record LabTestComponentDto(Guid TestId, string Code, string Name, int DisplayOrder, string? GroupName);

public sealed record LabTestDetail(
    Guid Id,
    string Code,
    string Name,
    string? ShortName,
    LabSection Section,
    SpecimenType SpecimenType,
    ContainerType Container,
    string? Method,
    int TatHours,
    string? Loinc,
    string? LoincName,
    Guid? ServiceItemId,
    bool IsOrderable,
    bool IsPanel,
    LabResultType ResultType,
    string? Unit,
    int Decimals,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> InstrumentChannelCodes,
    string? QcGroup,
    decimal? DeltaThresholdPercent,
    bool AllowAutoValidation,
    string? Preparation,
    bool DrawSeparately,
    string? Interpretation,
    Guid? DefaultReferralLabId,
    int DisplayOrder,
    bool IsProvisional,
    bool IsActive,
    IReadOnlyList<LabTestComponentDto> Components,
    IReadOnlyList<ReferenceRangeDto> Ranges,
    uint Version);

public sealed record SaveLabTestRequest(
    string Code,
    string Name,
    string? ShortName,
    LabSection Section,
    SpecimenType SpecimenType,
    ContainerType Container,
    string? Method,
    int TatHours,
    string? Loinc,
    string? LoincName,
    Guid? ServiceItemId,
    bool IsOrderable,
    bool IsPanel,
    LabResultType ResultType,
    string? Unit,
    int Decimals,
    IReadOnlyList<string>? Options,
    IReadOnlyList<string>? InstrumentChannelCodes,
    string? QcGroup,
    decimal? DeltaThresholdPercent,
    bool AllowAutoValidation,
    string? Preparation,
    bool DrawSeparately,
    string? Interpretation,
    Guid? DefaultReferralLabId,
    int DisplayOrder,
    bool IsActive,
    uint? Version);

public sealed record ComponentEntry(Guid TestId, string? GroupName);

public sealed record SetComponentsRequest(IReadOnlyList<ComponentEntry> Components, uint Version);

public sealed record SetRangesRequest(IReadOnlyList<ReferenceRangeDto> Ranges, uint Version);

public sealed record LabInstrumentDto(
    Guid Id,
    string Code,
    string Name,
    string? Manufacturer,
    string? Model,
    LabSection Section,
    InstrumentProtocol Protocol,
    bool IsBidirectional,
    bool AutoValidationEnabled,
    string? Location,
    bool IsActive,
    uint Version);

public sealed record SaveLabInstrumentRequest(
    string Code,
    string Name,
    string? Manufacturer,
    string? Model,
    LabSection Section,
    InstrumentProtocol Protocol,
    bool IsBidirectional,
    bool AutoValidationEnabled,
    string? Location,
    bool IsActive,
    uint? Version);

public sealed record ReferralLabDto(
    Guid Id,
    string Code,
    string Name,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? Address,
    string? AccreditationNumber,
    int TatDays,
    bool IsActive,
    uint Version);

public sealed record SaveReferralLabRequest(
    string Code,
    string Name,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? Address,
    string? AccreditationNumber,
    int TatDays,
    bool IsActive,
    uint? Version);

internal static class MasterRules
{
    /// <summary>Codes are upper-case letters, digits, dashes and underscores (they appear on labels, bills and HL7 messages).</summary>
    public static bool IsCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>Channel mapping: "HGB" or "XN550:HGB".</summary>
    public static bool IsChannelMapping(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.Count(c => c == ':') <= 1 && !value.StartsWith(':') && !value.EndsWith(':')
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '%' or '#' or ':' or '^');
}

public sealed class SaveLabTestRequestValidator : AbstractValidator<SaveLabTestRequest>
{
    public SaveLabTestRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Must(MasterRules.IsCode).WithMessage("Use upper-case letters, digits, '-' or '_'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.ShortName).MaximumLength(32);
        RuleFor(x => x.Section).IsInEnum();
        RuleFor(x => x.SpecimenType).IsInEnum();
        RuleFor(x => x.Container).IsInEnum();
        RuleFor(x => x.ResultType).IsInEnum();
        RuleFor(x => x.Method).SafeText(200);
        RuleFor(x => x.TatHours).InclusiveBetween(0, 24 * 60);
        RuleFor(x => x.Loinc).Matches("^[0-9]{1,7}-[0-9]$").When(x => !string.IsNullOrWhiteSpace(x.Loinc)).WithMessage("Enter a LOINC code such as 718-7.");
        RuleFor(x => x.LoincName).SafeText(255);
        RuleFor(x => x.LoincName).Empty().When(x => string.IsNullOrWhiteSpace(x.Loinc)).WithMessage("Enter the LOINC code this name belongs to.");
        RuleFor(x => x.Unit).MaximumLength(32);
        RuleFor(x => x.Decimals).InclusiveBetween(0, 4);
        RuleFor(x => x.Options).NotEmpty().When(x => x.ResultType == LabResultType.Options).WithMessage("List the allowed answers.");
        RuleForEach(x => x.Options).NotEmpty().MaximumLength(100);
        RuleForEach(x => x.InstrumentChannelCodes).Must(MasterRules.IsChannelMapping).WithMessage("Use CHANNEL or INSTRUMENT:CHANNEL.");
        RuleFor(x => x.QcGroup).MaximumLength(32).Must(v => v is null || MasterRules.IsCode(v)).WithMessage("Use upper-case letters, digits, '-' or '_'.");
        RuleFor(x => x.DeltaThresholdPercent).InclusiveBetween(0m, 10000m);
        RuleFor(x => x.Preparation).SafeText(500);
        RuleFor(x => x.Interpretation).SafeText(2000);
        RuleFor(x => x.ServiceItemId).NotNull().When(x => x.IsOrderable).WithMessage("Link an orderable test to its billable service.");
    }
}

public sealed class SetComponentsRequestValidator : AbstractValidator<SetComponentsRequest>
{
    public SetComponentsRequestValidator()
    {
        RuleFor(x => x.Components).NotEmpty().Must(c => c.Count <= 100).WithMessage("A panel can have at most 100 components.")
            .Must(c => c.Select(e => e.TestId).Distinct().Count() == c.Count).WithMessage("Each analyte can appear only once.");
        RuleForEach(x => x.Components).ChildRules(c =>
        {
            c.RuleFor(e => e.TestId).NotEmpty();
            c.RuleFor(e => e.GroupName).MaximumLength(100);
        });
    }
}

public sealed class SetRangesRequestValidator : AbstractValidator<SetRangesRequest>
{
    public SetRangesRequestValidator()
    {
        RuleFor(x => x.Ranges).Must(r => r.Count <= 50).WithMessage("At most 50 reference intervals per test.");
        RuleForEach(x => x.Ranges).ChildRules(r =>
        {
            r.RuleFor(e => e.AgeFromDays).InclusiveBetween(0, 365 * 130);
            r.RuleFor(e => e.AgeToDays).GreaterThan(e => e.AgeFromDays).When(e => e.AgeToDays is not null);
            r.RuleFor(e => e.Text).SafeText(500);
            r.RuleForEach(e => e.NormalValues).NotEmpty().MaximumLength(100);
            r.RuleForEach(e => e.CriticalValues).NotEmpty().MaximumLength(100);
        });
    }
}

public sealed class SaveLabInstrumentRequestValidator : AbstractValidator<SaveLabInstrumentRequest>
{
    public SaveLabInstrumentRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Must(MasterRules.IsCode).WithMessage("Use upper-case letters, digits, '-' or '_'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.Manufacturer).MaximumLength(100);
        RuleFor(x => x.Model).MaximumLength(100);
        RuleFor(x => x.Location).MaximumLength(100);
        RuleFor(x => x.Section).IsInEnum();
        RuleFor(x => x.Protocol).IsInEnum();
    }
}

public sealed class SaveReferralLabRequestValidator : AbstractValidator<SaveReferralLabRequest>
{
    public SaveReferralLabRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Must(MasterRules.IsCode).WithMessage("Use upper-case letters, digits, '-' or '_'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.ContactPerson).MaximumLength(200);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address).SafeText(500);
        RuleFor(x => x.AccreditationNumber).MaximumLength(64);
        RuleFor(x => x.TatDays).InclusiveBetween(1, 60);
    }
}
