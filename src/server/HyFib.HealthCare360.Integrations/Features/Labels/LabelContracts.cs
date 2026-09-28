using HyFib.HealthCare360.Integrations.Labels;

namespace HyFib.HealthCare360.Integrations.Features.Labels;

/// <summary>A label to render. <c>Language</c> is <c>zpl</c> (Zebra) or <c>tspl</c> (TSC and compatibles).</summary>
public sealed record PrintLabelRequest(
    string Value,
    LabelSymbology Symbology,
    IReadOnlyList<string>? TextLines,
    int WidthMm = 50,
    int HeightMm = 25,
    int Copies = 1,
    string Language = "zpl");

/// <summary>Raw printer commands for the Desktop bridge (<c>printer.printRaw</c>); nothing is printed by the server.</summary>
public sealed record PrintLabelResponse(string Language, string Content, int Copies);

public sealed class PrintLabelRequestValidator : AbstractValidator<PrintLabelRequest>
{
    public PrintLabelRequestValidator()
    {
        RuleFor(x => x.Value).NotEmpty().SafeText(LabelLayout.MaxQrLength);
        RuleFor(x => x.Value)
            .Must(v => v.All(c => c is >= ' ' and <= '~')).WithMessage("Code 128 supports printable ASCII characters only.")
            .MaximumLength(LabelLayout.MaxCode128Length)
            .When(x => x.Symbology == LabelSymbology.Code128 && !string.IsNullOrEmpty(x.Value));
        RuleFor(x => x.Symbology).IsInEnum();
        RuleFor(x => x.TextLines).Must(lines => lines is null || lines.Count <= LabelLayout.MaxTextLines)
            .WithMessage($"At most {LabelLayout.MaxTextLines} text lines.");
        RuleForEach(x => x.TextLines).SafeText(LabelLayout.MaxLineLength);
        RuleFor(x => x.WidthMm).InclusiveBetween(10, 150);
        RuleFor(x => x.HeightMm).InclusiveBetween(10, 150);
        RuleFor(x => x.Copies).InclusiveBetween(1, 100);
        RuleFor(x => x.Language).Must(l => LabelPrinterService.Languages.Contains(l?.Trim().ToLowerInvariant()))
            .WithMessage("Use ZPL or TSPL.");
    }
}
