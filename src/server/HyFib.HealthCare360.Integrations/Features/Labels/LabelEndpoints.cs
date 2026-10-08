using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Integrations.Labels.Templates;

namespace HyFib.HealthCare360.Integrations.Features.Labels;

/// <summary>
/// /api/v1/integrations/labels: ZPL or TSPL for the label printer of the workstation asking (INT-BAR). The staff app
/// sends the label's data and the printer's setup; the answer is the printer commands, which HC360 Desktop sends to
/// that printer. Nothing is printed, stored or logged here. /api/v1/integrations/printers/changes approves a change to a
/// workstation's printers before HC360 Desktop saves it, and records it in the audit trail after;
/// /api/v1/integrations/scanners/changes does the same for its serial barcode scanner.
/// </summary>
internal static class LabelEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var labels = app.MapGroup("/api/v1/integrations/labels")
            .WithTags("Integrations")
            .RequirePermission(IntegrationsPermissions.LabelsPrint);

        labels.MapPost("/", Build).WithValidation<PrintLabelRequest>();
        labels.MapPost("/lab-tubes", (LabTubeLabelsRequest r) =>
                Render(r.Printer, r.Labels, r.Copies, LabTubeTemplate.Draw))
            .WithValidation<LabTubeLabelsRequest>();
        labels.MapPost("/wristbands", (WristbandLabelsRequest r) =>
                Render(r.Printer, r.Labels, r.Copies, (canvas, band) => WristbandTemplate.Draw(canvas, band, r.Code)))
            .WithValidation<WristbandLabelsRequest>();
        labels.MapPost("/dispensing", (DispensingLabelsRequest r) =>
                Render(r.Printer, r.Labels, r.Copies, DispensingTemplate.Draw))
            .WithValidation<DispensingLabelsRequest>();
        labels.MapPost("/blood-units", (BloodUnitLabelsRequest r) =>
                Render(r.Printer, r.Labels, r.Copies, BloodUnitTemplate.Draw))
            .WithValidation<BloodUnitLabelsRequest>();
        labels.MapPost("/cssd-packs", (CssdPackLabelsRequest r) =>
                Render(r.Printer, r.Labels, r.Copies, CssdPackTemplate.Draw))
            .WithValidation<CssdPackLabelsRequest>();
        labels.MapPost("/asset-tags", (AssetTagLabelsRequest r) =>
                Render(r.Printer, r.Labels, r.Copies, AssetTagTemplate.Draw))
            .WithValidation<AssetTagLabelsRequest>();
        labels.MapPost("/pharmacy-tags", (PharmacyTagLabelsRequest r) =>
                Render(r.Printer, r.Labels, r.Copies, PharmacyTagTemplate.Draw))
            .WithValidation<PharmacyTagLabelsRequest>();
        labels.MapPost("/test", (TestLabelRequest r, TimeProvider time) =>
                Render(r.Printer, new[] { r }, 1, (canvas, test) => TestLabelTemplate.Draw(canvas, test.Stock, test.Computer, time.GetUtcNow())))
            .WithValidation<TestLabelRequest>();

        var printers = app.MapGroup("/api/v1/integrations/printers/changes")
            .WithTags("Integrations")
            .RequirePermission(IntegrationsPermissions.PrintersSetup);
        printers.MapPost("/approval", Approve).WithValidation<PrinterSetupChange>();
        printers.MapPost("/", RecordChange).WithValidation<PrinterSetupChange>();

        var scanners = app.MapGroup("/api/v1/integrations/scanners/changes")
            .WithTags("Integrations")
            .RequirePermission(IntegrationsPermissions.PrintersSetup);
        scanners.MapPost("/approval", (ScannerSetupChange _, ICurrentUser user) => Approval(user)).WithValidation<ScannerSetupChange>();
        scanners.MapPost("/", RecordScannerChange).WithValidation<ScannerSetupChange>();
    }

    private static IResult Build(PrintLabelRequest request, ILabelPrinterService labels)
    {
        try
        {
            var language = request.Language.Trim().ToLowerInvariant();
            var content = labels.Build(
                new LabelRequest(request.Value.Trim(), request.Symbology, request.TextLines ?? [], request.WidthMm, request.HeightMm, request.Copies, request.Dpi),
                language);
            return TypedResults.Ok(new PrintLabelResponse(language == "tspl2" ? "tspl" : language, content, request.Copies));
        }
        catch (ArgumentException ex)
        {
            // Layout limits the validator cannot know (for example a value too long for the chosen label width).
            return Error.Field("value", ex.Message.Split(" (Parameter", 2)[0]).ToProblem();
        }
    }

    /// <summary>
    /// The labels in the printer's language. A label that does not fit its media, or whose text the printer's fonts
    /// cannot print, is refused with a reason (422): <c>labels.does_not_fit</c> or <c>labels.text_not_printable</c>. On
    /// the second the staff app prints the label's page through the Windows driver instead.
    /// </summary>
    internal static IResult Render<T>(LabelPrinterSetup printer, IReadOnlyList<T> labels, int copies, Action<LabelCanvas, T> draw)
    {
        var media = printer.ToMedia();
        try
        {
            var content = LabelRenderer.Render(media, labels, copies, draw);
            return TypedResults.Ok(new LabelCommands(LabelMedia.Name(media.Language), content, labels.Count));
        }
        catch (LabelTextException ex)
        {
            return Error.Rule("labels.text_not_printable", ex.Message).ToProblem();
        }
        catch (LabelLayoutException ex)
        {
            return Error.Rule("labels.does_not_fit", ex.Message).ToProblem();
        }
    }

    /// <summary>
    /// Approves a change to a workstation's printers before HC360 Desktop saves it: only staff allowed to set up
    /// printers get an approval, so the staff app saves only what this accepted. The answer names the user the change is
    /// saved as; the change is recorded once it is saved (<see cref="RecordChange"/>).
    /// </summary>
    private static IResult Approve(PrinterSetupChange change, ICurrentUser user) => Approval(user);

    private static IResult Approval(ICurrentUser user) =>
        TypedResults.Ok(new PrinterChangeApproval(user.DisplayName ?? user.UserName ?? "Unknown user"));

    /// <summary>Records who changed which workstation's printers, and to what, after HC360 Desktop saved the change.</summary>
    private static async Task<IResult> RecordChange(PrinterSetupChange change, IAuditLogger audit, CancellationToken ct)
    {
        var assigned = string.Join("; ", change.Roles
            .Where(r => !string.IsNullOrWhiteSpace(r.Printer))
            .Select(r => r.Code is { } code
                ? $"{r.Role}: {r.Printer!.Trim()} ({(code == "qr" ? "QR code" : "Code 128")})"
                : $"{r.Role}: {r.Printer!.Trim()}"));
        var summary = $"Changed the printers on {change.Computer.Trim()}: {(assigned.Length > 0 ? assigned : "none set")}";
        await audit.WriteAsync(new AuditEntry(AuditCategories.Admin, "workstation.printers_changed", summary.Length > 1000 ? summary[..1000] : summary)
        {
            EntityType = "Workstation",
            EntityId = change.DeviceId?.ToString() ?? change.Computer.Trim(),
            Details = new
            {
                computer = change.Computer.Trim(),
                change.DeviceId,
                roles = change.Roles.Select(r => new { r.Role, r.Printer, r.Language, r.Dpi, r.WidthMm, r.HeightMm, r.Code }),
            },
        }, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Records who changed which workstation's serial barcode scanner, and to what, after HC360 Desktop saved it.</summary>
    private static async Task<IResult> RecordScannerChange(ScannerSetupChange change, IAuditLogger audit, CancellationToken ct)
    {
        var computer = change.Computer.Trim();
        var scanner = change.Port is { } port
            ? $"{port.Trim().ToUpperInvariant()} at {change.BaudRate} baud, {change.DataBits} data bits, {(change.Parity == "none" ? "no" : change.Parity)} parity, {(change.StopBits == "two" ? 2 : 1)} stop bit{(change.StopBits == "two" ? "s" : "")}"
            : "none (keyboard scanners only)";
        await audit.WriteAsync(new AuditEntry(AuditCategories.Admin, "workstation.scanner_changed", $"Changed the barcode scanner on {computer}: {scanner}")
        {
            EntityType = "Workstation",
            EntityId = change.DeviceId?.ToString() ?? computer,
            Details = new { computer, change.DeviceId, change.Port, change.BaudRate, change.DataBits, change.Parity, change.StopBits },
        }, ct);
        return TypedResults.NoContent();
    }
}
