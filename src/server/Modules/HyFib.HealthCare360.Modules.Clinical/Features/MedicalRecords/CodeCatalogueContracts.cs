using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

/// <summary>
/// A catalogue code. <see cref="Usage"/> marks dagger (†, the underlying disease) and asterisk (*, its manifestation)
/// codes; <see cref="IncludedTerms"/> are the WHO file's inclusion terms, joined with "; ".
/// </summary>
public sealed record IcdCodeDto(
    Guid Id,
    string Code,
    string Title,
    string Release,
    string? Chapter,
    string? Block,
    string? BlockTitle,
    bool IsBillable,
    IcdUsage? Usage,
    string? Keywords,
    string? IncludedTerms,
    CodeSource Source,
    bool IsActive,
    uint Version);

public sealed record IcdBlockDto(string Range, string Title);

public sealed record IcdChapterDto(string Number, string Title, string Range, IReadOnlyList<IcdBlockDto> Blocks);

public sealed record ProcedureCodeDto(
    Guid Id, string Code, string Title, string System, string? Category, string? Keywords, CodeSource Source, bool IsActive, uint Version);

/// <summary>
/// Adds a code to the local catalogue (manual entry or caching a code found through the online ICD service).
/// <see cref="Release"/> is ICD-10 2019 (the default) or ICD-11 MMS, in any label <see cref="CodeReleases.Normalize"/>
/// accepts, so a result of the WHO online search can be saved as it came ("ICD-11 MMS 2025-01").
/// </summary>
public sealed record SaveIcdCodeRequest(
    string Code, string Title, string? Release, bool IsBillable, string? Keywords, bool FromOnlineLookup, bool IsActive, uint? Version = null);

public sealed record SaveProcedureCodeRequest(string Code, string Title, string? System, string? Category, string? Keywords, bool IsActive, uint? Version = null);

public sealed record OnlineIcdResult(string Code, string Title, string Release, string? Chapter, bool IsLeaf, bool IsCachedLocally);

public sealed class SaveIcdCodeRequestValidator : AbstractValidator<SaveIcdCodeRequest>
{
    public SaveIcdCodeRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches("^[A-Za-z0-9][A-Za-z0-9.&/]*$").WithMessage("Enter a valid code (letters, digits and dots).");
        RuleFor(x => x.Title).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Release).MaximumLength(32);
        RuleFor(x => x.Keywords).SafeText(300);
    }
}

public sealed class SaveProcedureCodeRequestValidator : AbstractValidator<SaveProcedureCodeRequest>
{
    public SaveProcedureCodeRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches("^[A-Za-z0-9][A-Za-z0-9.]*$").WithMessage("Enter a valid code (letters, digits and dots).");
        RuleFor(x => x.Title).NotEmpty().MaximumLength(300);
        RuleFor(x => x.System).MaximumLength(32);
        RuleFor(x => x.Category).MaximumLength(64);
        RuleFor(x => x.Keywords).SafeText(300);
    }
}
