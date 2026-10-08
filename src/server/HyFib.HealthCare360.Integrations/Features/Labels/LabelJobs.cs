using System.Globalization;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Integrations.Simulator;

namespace HyFib.HealthCare360.Integrations.Features.Labels;

/// <summary>
/// How a facility's labels are printed (<c>integrations.labels</c>): on each workstation's own printers (the default,
/// Printers on this computer), or by the simulator for training, where nothing is sent to a printer.
/// </summary>
internal sealed record LabelSettings : IIntegrationSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary><c>printers</c> (or none) for the workstations' printers, <c>simulator</c> for training.</summary>
    public string? Provider { get; init; }
}

/// <summary>Whether this facility's labels go to the simulator (the staff app then shows them instead of printing).</summary>
public sealed record LabelMode(bool Simulator);

/// <summary>
/// Writes label print jobs (INT-BAR) and keeps their record: every job is traced in the integration log (the kind of
/// label, how many, the media and the outcome; never what is on the labels), which gives Barcode labels its health on the
/// integrations dashboard. With the simulator the printer commands are also kept in the simulator inbox, as the
/// training printer received them.
/// </summary>
internal sealed class LabelJobs(IntegrationTrace trace, IntegrationSettingsReader settings, SimulatorInbox inbox)
{
    public async Task<bool> IsSimulatorAsync(CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<LabelSettings, NoSecrets>(IntegrationNames.Labels, null, requireEnabled: false, cancellationToken);
        return string.Equals(config.Settings?.Provider?.Trim(), ProviderKeys.Simulator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The labels in the printer's language. A label that does not fit its media, or whose text the printer's fonts
    /// cannot print, is refused with a reason (422): <c>labels.does_not_fit</c> or <c>labels.text_not_printable</c>. On
    /// the second the staff app prints the label's page through the Windows driver instead.
    /// </summary>
    public async Task<IResult> RenderAsync<T>(
        string kind, LabelPrinterSetup printer, IReadOnlyList<T> labels, int copies, Action<LabelCanvas, T> draw, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var media = printer.ToMedia();
        var language = LabelMedia.Name(media.Language);
        var job = string.Create(CultureInfo.InvariantCulture,
            $"{labels.Count} label(s) × {copies}, {LabelCanvas.Describe(media)} at {media.Dpi} dpi");
        string content;
        try
        {
            content = LabelRenderer.Render(media, labels, copies, draw);
        }
        catch (LabelTextException ex)
        {
            await trace.LogAsync(IntegrationNames.Labels, language, kind, false, started, request: job, error: ex.Message);
            return Error.Rule("labels.text_not_printable", ex.Message).ToProblem();
        }
        catch (LabelLayoutException ex)
        {
            await trace.LogAsync(IntegrationNames.Labels, language, kind, false, started, request: job, error: ex.Message);
            return Error.Rule("labels.does_not_fit", ex.Message).ToProblem();
        }

        await RecordAsync(kind, language, job, content, started, cancellationToken);
        return TypedResults.Ok(new LabelCommands(language, content, labels.Count));
    }

    /// <summary>Traces a job that was written (and keeps it in the simulator inbox when the facility trains).</summary>
    public async Task RecordAsync(string kind, string language, string job, string content, long started, CancellationToken cancellationToken)
    {
        var simulated = await IsSimulatorAsync(cancellationToken);
        string? reference = null;
        if (simulated)
        {
            reference = await inbox.RecordAsync(
                SimulatorChannel.Labels, recipient: null, subject: $"{kind}: {job}", body: content, details: language, reference: kind, cancellationToken);
        }

        await trace.LogAsync(IntegrationNames.Labels, simulated ? ProviderKeys.Simulator : language, kind, true, started,
            reference, job, string.Create(CultureInfo.InvariantCulture, $"{content.Length} characters of {language}"));
    }
}
