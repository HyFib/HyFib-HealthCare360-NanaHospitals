using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Integrations.Labels.Templates;

namespace HyFib.HealthCare360.Integrations.Features.Labels;

/// <summary>A general-purpose label. <c>Language</c> is <c>zpl</c> (Zebra) or <c>tspl</c> (TSC and compatibles).</summary>
public sealed record PrintLabelRequest(
    string Value,
    LabelSymbology Symbology,
    IReadOnlyList<string>? TextLines,
    int WidthMm = 50,
    int HeightMm = 25,
    int Copies = 1,
    string Language = "zpl",
    int Dpi = 203);

/// <summary>Raw printer commands for the Desktop bridge (<c>printer.printRaw</c>); nothing is printed by the server.</summary>
public sealed record PrintLabelResponse(string Language, string Content, int Copies);

/// <summary>
/// The workstation's label or wristband printer, as HC360 Desktop keeps it (Printers on this computer): its language,
/// its resolution and the size of one label, or of one band (width across the band, length along it).
/// </summary>
public sealed record LabelPrinterSetup(string Language, int Dpi, decimal WidthMm, decimal HeightMm)
{
    internal LabelMedia ToMedia() =>
        new(LabelMedia.ParseLanguage(Language) ?? LabelLanguage.Zpl, Dpi, WidthMm, HeightMm);
}

/// <summary>Raw printer commands for every label asked for, ready for HC360 Desktop's <c>printer.printRaw</c>.</summary>
public sealed record LabelCommands(string Language, string Content, int Labels);

public sealed record LabTubeLabelsRequest(LabelPrinterSetup Printer, IReadOnlyList<LabTubeLabel> Labels, int Copies = 1);

/// <param name="Code">How the UHID is coded: Code 128 (the default, for line scanners) or a QR code.</param>
public sealed record WristbandLabelsRequest(
    LabelPrinterSetup Printer,
    IReadOnlyList<WristbandLabel> Labels,
    WristbandCode Code = WristbandCode.Code128,
    int Copies = 1);

public sealed record DispensingLabelsRequest(LabelPrinterSetup Printer, IReadOnlyList<DispensingLabel> Labels, int Copies = 1);

public sealed record BloodUnitLabelsRequest(LabelPrinterSetup Printer, IReadOnlyList<BloodUnitLabel> Labels, int Copies = 1);

public sealed record CssdPackLabelsRequest(LabelPrinterSetup Printer, IReadOnlyList<CssdPackLabel> Labels, int Copies = 1);

public sealed record AssetTagLabelsRequest(LabelPrinterSetup Printer, IReadOnlyList<AssetTagLabel> Labels, int Copies = 1);

/// <summary>A test label for a printer being set up: on label rolls or on wristband bands.</summary>
/// <param name="Computer">The workstation's name, printed on the label.</param>
public sealed record TestLabelRequest(LabelPrinterSetup Printer, LabelStock Stock = LabelStock.Label, string? Computer = null);

/// <summary>
/// A change to the printers of one workstation, recorded in the audit trail before HC360 Desktop saves it there. The
/// server keeps nothing else: the printers belong to the workstation (Printers on this computer).
/// </summary>
/// <param name="Computer">The workstation's name, as HC360 Desktop reports it.</param>
/// <param name="DeviceId">The workstation's enrolled device id, when it has one.</param>
public sealed record PrinterSetupChange(string Computer, Guid? DeviceId, IReadOnlyList<PrinterRoleSetup> Roles);

/// <param name="Role">labels, wristbands, receipts or documents.</param>
/// <param name="Printer">The Windows printer, or null for "show the print dialog".</param>
/// <param name="Language">For labels and wristbands: zpl, tspl, or driver (print the page through the Windows driver).</param>
public sealed record PrinterRoleSetup(string Role, string? Printer, string? Language, int? Dpi, decimal? WidthMm, decimal? HeightMm);

/// <summary>The sizes each kind of stock may have, and the rules the label requests share.</summary>
internal static class LabelRules
{
    public const int MaxLabels = 100;
    public const int MaxWristbands = 10;
    public const int MaxCopies = 10;

    public static IReadOnlyList<string> Roles { get; } = ["labels", "wristbands", "receipts", "documents"];

    public static IReadOnlyList<string> RoleLanguages { get; } = ["zpl", "tspl", "driver"];

    /// <summary>Label rolls: 25–110 mm wide, 15–100 mm tall. Wristbands: 15–40 mm wide, 80–350 mm long.</summary>
    public static (decimal MinWidth, decimal MaxWidth, decimal MinHeight, decimal MaxHeight) Sizes(LabelStock stock) =>
        stock == LabelStock.Wristband ? (15m, 40m, 80m, 350m) : (25m, 110m, 15m, 100m);

    public static void Printer(InlineValidator<LabelPrinterSetup> printer, LabelStock stock)
    {
        var (minWidth, maxWidth, minHeight, maxHeight) = Sizes(stock);
        var band = stock == LabelStock.Wristband;
        printer.RuleFor(p => p.Language).Must(l => LabelMedia.ParseLanguage(l) is not null).WithMessage("Use ZPL or TSPL.");
        printer.RuleFor(p => p.Dpi).Must(d => d is LabelMedia.LowResolution or LabelMedia.HighResolution).WithMessage("Use 203 or 300 dpi.");
        printer.RuleFor(p => p.WidthMm).Must(w => Within(w, minWidth, maxWidth))
            .WithMessage($"The {(band ? "band" : "label")} width must be {minWidth:0} to {maxWidth:0} mm, to one decimal place.");
        printer.RuleFor(p => p.HeightMm).Must(h => Within(h, minHeight, maxHeight))
            .WithMessage($"The {(band ? "band length" : "label height")} must be {minHeight:0} to {maxHeight:0} mm, to one decimal place.");
    }

    private static bool Within(decimal value, decimal min, decimal max) => value >= min && value <= max && decimal.Round(value, 1) == value;

    public static IRuleBuilderOptions<T, string?> Barcode<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(value => Code128Plan.IsEncodable(value))
            .WithMessage($"Use 1 to {Code128Plan.MaxLength} letters, digits, spaces and common marks (not ! > \" or \\).");

    public static string TooMany(int max) => $"Print at most {max} labels at a time.";
}

public sealed class PrintLabelRequestValidator : AbstractValidator<PrintLabelRequest>
{
    public PrintLabelRequestValidator()
    {
        RuleFor(x => x.Value).NotEmpty().SafeText(QrSize.MaxBytes);
        RuleFor(x => x.Value).Barcode().When(x => x.Symbology == LabelSymbology.Code128 && !string.IsNullOrEmpty(x.Value));
        RuleFor(x => x.Symbology).IsInEnum();
        RuleFor(x => x.TextLines).Must(lines => lines is null || lines.Count <= GenericLabelTemplate.MaxTextLines)
            .WithMessage($"At most {GenericLabelTemplate.MaxTextLines} text lines.");
        RuleForEach(x => x.TextLines).SafeText(GenericLabelTemplate.MaxLineLength);
        RuleFor(x => x.WidthMm).InclusiveBetween(10, 150);
        RuleFor(x => x.HeightMm).InclusiveBetween(10, 150);
        RuleFor(x => x.Copies).InclusiveBetween(1, 100);
        RuleFor(x => x.Language).Must(l => LabelMedia.ParseLanguage(l) is not null).WithMessage("Use ZPL or TSPL.");
        RuleFor(x => x.Dpi).Must(d => d is LabelMedia.LowResolution or LabelMedia.HighResolution).WithMessage("Use 203 or 300 dpi.");
    }
}

public sealed class LabTubeLabelValidator : AbstractValidator<LabTubeLabel>
{
    public LabTubeLabelValidator()
    {
        RuleFor(x => x.Barcode).Cascade(CascadeMode.Stop).NotEmpty().Barcode();
        RuleFor(x => x.PatientName).NotEmpty().SafeText(200);
        RuleFor(x => x.Uhid).NotEmpty().SafeText(40);
        RuleFor(x => x.AgeSex).SafeText(20);
        RuleFor(x => x.AccessionNumber).SafeText(40);
        RuleFor(x => x.Tube).SafeText(20);
        RuleFor(x => x.Bench).SafeText(20);
        RuleFor(x => x.Tests).Must(tests => tests is null || tests.Count <= 30).WithMessage("At most 30 tests on one tube.");
        RuleForEach(x => x.Tests).SafeText(80);
    }
}

public sealed class WristbandLabelValidator : AbstractValidator<WristbandLabel>
{
    public WristbandLabelValidator()
    {
        RuleFor(x => x.Uhid).Cascade(CascadeMode.Stop).NotEmpty().Barcode();
        RuleFor(x => x.PatientName).NotEmpty().SafeText(200);
        RuleFor(x => x.AgeSex).SafeText(20);
        RuleFor(x => x.AdmissionNumber).SafeText(40);
        RuleFor(x => x.Ward).SafeText(80);
        RuleFor(x => x.Bed).SafeText(40);
    }
}

public sealed class DispensingLabelValidator : AbstractValidator<DispensingLabel>
{
    public DispensingLabelValidator()
    {
        RuleFor(x => x.PatientName).NotEmpty().SafeText(200);
        RuleFor(x => x.Medicine).NotEmpty().SafeText(200);
        RuleFor(x => x.Quantity).NotEmpty().SafeText(40);
        RuleFor(x => x.Directions).SafeText(500);
        RuleFor(x => x.Batch).SafeText(120);
        RuleFor(x => x.Hospital).SafeText(200);
    }
}

public sealed class BloodUnitLabelValidator : AbstractValidator<BloodUnitLabel>
{
    public BloodUnitLabelValidator()
    {
        RuleFor(x => x.UnitNumber).Cascade(CascadeMode.Stop).NotEmpty().Barcode();
        RuleFor(x => x.BloodGroup).NotEmpty().SafeText(20);
        RuleFor(x => x.Component).NotEmpty().SafeText(20);
        RuleFor(x => x.VolumeMl).InclusiveBetween(1, 2000);
        RuleFor(x => x.Storage).SafeText(100);
    }
}

public sealed class CssdPackLabelValidator : AbstractValidator<CssdPackLabel>
{
    public CssdPackLabelValidator()
    {
        RuleFor(x => x.PackNumber).Cascade(CascadeMode.Stop).NotEmpty().Barcode();
        RuleFor(x => x.SetName).NotEmpty().SafeText(200);
        RuleFor(x => x.Method).SafeText(40);
    }
}

public sealed class AssetTagLabelValidator : AbstractValidator<AssetTagLabel>
{
    public AssetTagLabelValidator()
    {
        RuleFor(x => x.AssetTag).Cascade(CascadeMode.Stop).NotEmpty().Barcode();
        RuleFor(x => x.AssetName).NotEmpty().SafeText(200);
        RuleFor(x => x.Hospital).SafeText(200);
    }
}

public sealed class LabTubeLabelsRequestValidator : AbstractValidator<LabTubeLabelsRequest>
{
    public LabTubeLabelsRequestValidator()
    {
        RuleFor(x => x.Printer).NotNull().ChildRules(p => LabelRules.Printer(p, LabelStock.Label));
        RuleFor(x => x.Labels).NotEmpty().Must(l => l is null || l.Count <= LabelRules.MaxLabels).WithMessage(LabelRules.TooMany(LabelRules.MaxLabels));
        RuleForEach(x => x.Labels).NotNull().SetValidator(new LabTubeLabelValidator());
        RuleFor(x => x.Copies).InclusiveBetween(1, LabelRules.MaxCopies);
    }
}

public sealed class WristbandLabelsRequestValidator : AbstractValidator<WristbandLabelsRequest>
{
    public WristbandLabelsRequestValidator()
    {
        RuleFor(x => x.Printer).NotNull().ChildRules(p => LabelRules.Printer(p, LabelStock.Wristband));
        RuleFor(x => x.Labels).NotEmpty().Must(l => l is null || l.Count <= LabelRules.MaxWristbands).WithMessage(LabelRules.TooMany(LabelRules.MaxWristbands));
        RuleForEach(x => x.Labels).NotNull().SetValidator(new WristbandLabelValidator());
        RuleFor(x => x.Code).IsInEnum();
        RuleFor(x => x.Copies).InclusiveBetween(1, LabelRules.MaxCopies);
    }
}

public sealed class DispensingLabelsRequestValidator : AbstractValidator<DispensingLabelsRequest>
{
    public DispensingLabelsRequestValidator()
    {
        RuleFor(x => x.Printer).NotNull().ChildRules(p => LabelRules.Printer(p, LabelStock.Label));
        RuleFor(x => x.Labels).NotEmpty().Must(l => l is null || l.Count <= LabelRules.MaxLabels).WithMessage(LabelRules.TooMany(LabelRules.MaxLabels));
        RuleForEach(x => x.Labels).NotNull().SetValidator(new DispensingLabelValidator());
        RuleFor(x => x.Copies).InclusiveBetween(1, LabelRules.MaxCopies);
    }
}

public sealed class BloodUnitLabelsRequestValidator : AbstractValidator<BloodUnitLabelsRequest>
{
    public BloodUnitLabelsRequestValidator()
    {
        RuleFor(x => x.Printer).NotNull().ChildRules(p => LabelRules.Printer(p, LabelStock.Label));
        RuleFor(x => x.Labels).NotEmpty().Must(l => l is null || l.Count <= LabelRules.MaxLabels).WithMessage(LabelRules.TooMany(LabelRules.MaxLabels));
        RuleForEach(x => x.Labels).NotNull().SetValidator(new BloodUnitLabelValidator());
        RuleFor(x => x.Copies).InclusiveBetween(1, LabelRules.MaxCopies);
    }
}

public sealed class CssdPackLabelsRequestValidator : AbstractValidator<CssdPackLabelsRequest>
{
    public CssdPackLabelsRequestValidator()
    {
        RuleFor(x => x.Printer).NotNull().ChildRules(p => LabelRules.Printer(p, LabelStock.Label));
        RuleFor(x => x.Labels).NotEmpty().Must(l => l is null || l.Count <= LabelRules.MaxLabels).WithMessage(LabelRules.TooMany(LabelRules.MaxLabels));
        RuleForEach(x => x.Labels).NotNull().SetValidator(new CssdPackLabelValidator());
        RuleFor(x => x.Copies).InclusiveBetween(1, LabelRules.MaxCopies);
    }
}

public sealed class AssetTagLabelsRequestValidator : AbstractValidator<AssetTagLabelsRequest>
{
    public AssetTagLabelsRequestValidator()
    {
        RuleFor(x => x.Printer).NotNull().ChildRules(p => LabelRules.Printer(p, LabelStock.Label));
        RuleFor(x => x.Labels).NotEmpty().Must(l => l is null || l.Count <= LabelRules.MaxLabels).WithMessage(LabelRules.TooMany(LabelRules.MaxLabels));
        RuleForEach(x => x.Labels).NotNull().SetValidator(new AssetTagLabelValidator());
        RuleFor(x => x.Copies).InclusiveBetween(1, LabelRules.MaxCopies);
    }
}

public sealed class TestLabelRequestValidator : AbstractValidator<TestLabelRequest>
{
    public TestLabelRequestValidator()
    {
        RuleFor(x => x.Stock).IsInEnum();
        RuleFor(x => x.Printer).NotNull();
        RuleFor(x => x.Printer).ChildRules(p => LabelRules.Printer(p, LabelStock.Label)).When(x => x.Stock == LabelStock.Label);
        RuleFor(x => x.Printer).ChildRules(p => LabelRules.Printer(p, LabelStock.Wristband)).When(x => x.Stock == LabelStock.Wristband);
        RuleFor(x => x.Computer).SafeText(64);
    }
}

public sealed class PrinterSetupChangeValidator : AbstractValidator<PrinterSetupChange>
{
    public PrinterSetupChangeValidator()
    {
        RuleFor(x => x.Computer).NotEmpty().SafeText(64);
        RuleFor(x => x.Roles).NotEmpty().Must(roles => roles is null || roles.Count <= LabelRules.Roles.Count)
            .WithMessage("A computer has four kinds of printer at most.");
        RuleFor(x => x.Roles).Must(roles => roles is null || roles.Select(r => r?.Role).Distinct(StringComparer.Ordinal).Count() == roles.Count)
            .WithMessage("List each kind of printer once.");
        RuleForEach(x => x.Roles).NotNull().ChildRules(role =>
        {
            role.RuleFor(r => r.Role).Must(r => r is not null && LabelRules.Roles.Contains(r)).WithMessage("Use labels, wristbands, receipts or documents.");
            role.RuleFor(r => r.Printer).SafeText(260);
            role.RuleFor(r => r.Language).Must(l => l is null || LabelRules.RoleLanguages.Contains(l)).WithMessage("Use zpl, tspl or driver.");
            role.RuleFor(r => r.Dpi).Must(d => d is null or LabelMedia.LowResolution or LabelMedia.HighResolution).WithMessage("Use 203 or 300 dpi.");
            role.RuleFor(r => r.WidthMm).InclusiveBetween(15m, 110m).When(r => r.WidthMm is not null);
            role.RuleFor(r => r.HeightMm).InclusiveBetween(15m, 350m).When(r => r.HeightMm is not null);
        });
    }
}
