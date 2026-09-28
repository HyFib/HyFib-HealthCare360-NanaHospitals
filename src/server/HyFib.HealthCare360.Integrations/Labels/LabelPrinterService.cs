namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// The platform's <see cref="ILabelPrinterService"/> (INT-BAR). The server only builds printer language; printing
/// happens on the workstation through the Desktop bridge (<c>printer.printRaw</c>), which owns the USB/network printer.
/// </summary>
internal sealed class LabelPrinterService : ILabelPrinterService
{
    public static IReadOnlyList<string> Languages { get; } = ["zpl", "tspl"];

    /// <exception cref="ArgumentException">Unsupported language or an invalid request (message is staff-facing).</exception>
    public string Build(LabelRequest request, string language) => language?.Trim().ToLowerInvariant() switch
    {
        "zpl" => ZplLabelBuilder.Build(request),
        "tspl" or "tspl2" => TsplLabelBuilder.Build(request),
        _ => throw new ArgumentException($"Unsupported printer language '{language}'. Use ZPL or TSPL.", nameof(language)),
    };
}
