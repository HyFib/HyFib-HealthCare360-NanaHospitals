using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Hl7;

namespace HyFib.HealthCare360.Integrations.Features.LabConnections;

/// <summary>A sample as the training analyser sees it: whose it is, and the analyser's tests it still needs.</summary>
public sealed record TrainingSampleDto(string SampleBarcode, string PatientName, string? PatientId, IReadOnlyList<string> Channels);

/// <summary>One result to send: the analyser's channel code, the value, and optionally its units.</summary>
public sealed record TrainingResult(string Channel, string Value, string? Units);

public sealed record TrainingResultsRequest(string SampleBarcode, IReadOnlyList<TrainingResult> Results);

/// <summary>What happened to the results: taken by the LIS, or refused with the reason; and the message that was sent.</summary>
public sealed record TrainingResultsOutcome(bool Accepted, string Message, string RawMessage);

public sealed partial class TrainingResultsRequestValidator : AbstractValidator<TrainingResultsRequest>
{
    public TrainingResultsRequestValidator()
    {
        RuleFor(r => r.SampleBarcode).NotEmpty().MaximumLength(64).Must(Plain).WithMessage("Use letters, digits and - or _ in the sample barcode.");
        RuleFor(r => r.Results).NotEmpty().WithMessage("Enter at least one result.")
            .Must(r => r is null || r.Count <= 100).WithMessage("Send at most 100 results at a time.")
            .Must(r => r is null || r.Select(x => x?.Channel?.Trim().ToUpperInvariant()).Distinct().Count() == r.Count)
            .WithMessage("Enter each test once.");
        RuleForEach(r => r.Results).NotNull().ChildRules(result =>
        {
            result.RuleFor(x => x.Channel).NotEmpty().MaximumLength(32).Must(Plain).WithMessage("A channel code uses letters, digits and - or _.");
            result.RuleFor(x => x.Value).NotEmpty().MaximumLength(64).Must(Safe)
                .WithMessage("A value cannot hold | ^ ~ \\ & or line breaks, which analyser messages reserve.");
            result.RuleFor(x => x.Units).MaximumLength(20).Must(u => u is null || Safe(u))
                .WithMessage("Units cannot hold | ^ ~ \\ & or line breaks, which analyser messages reserve.");
        });
    }

    internal static bool Plain(string? value) => value is not null && PlainPattern().IsMatch(value.Trim());

    internal static bool Safe(string? value) => value is not null && !value.Any(c => c is '|' or '^' or '~' or '\\' or '&' || char.IsControl(c));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex PlainPattern();
}

/// <summary>
/// The training analyser (INT-LAB simulator): an analyser in Lab setup whose connection is <c>Simulator</c>. Nothing
/// connects to it; someone enters a received sample's results on screen, and HC360 writes the message the analyser
/// would send in its protocol (HL7 ORU^R01 or ASTM E1394), decodes it with the same mappers as a real analyser's, and
/// hands it to the LIS through the same path. So results are matched to the sample's tests by the analyser's channel
/// codes, checked (auto-validation, delta checks, QC lockout) and logged as for a real analyser, and the lab can
/// practise without one. Results can be sent only to a training analyser, never to a real one.
/// </summary>
internal sealed class LabTrainingAnalyser(
    IntegrationSettingsReader settings, ILabMessageProcessor processor, IServiceProvider services, ICurrentUser currentUser, TimeProvider time)
{
    /// <summary>The tests the sample still needs on this analyser.</summary>
    public async Task<Result<TrainingSampleDto>> SampleAsync(string code, string barcode, CancellationToken cancellationToken)
    {
        var (instrument, problem) = await TrainingAnalyserAsync(code, cancellationToken);
        if (instrument is null)
        {
            return problem!;
        }

        var sink = services.GetRequiredService<IInstrumentResultSink>();
        var order = await sink.GetOrderForSampleAsync(instrument.Code, barcode.Trim(), cancellationToken);
        if (order is null || order.ChannelCodes.Count == 0)
        {
            return Error.Rule("lab.training_no_tests",
                $"Sample {barcode.Trim()} has no tests waiting for {instrument.Code}. Receive the sample in the lab first, and check that its tests have this analyser's channel codes.");
        }

        return new TrainingSampleDto(order.SampleBarcode, order.PatientName, order.PatientId, order.ChannelCodes);
    }

    /// <summary>Sends the results as the analyser would, and says whether the LIS took them.</summary>
    public async Task<Result<TrainingResultsOutcome>> SendAsync(string code, TrainingResultsRequest request, CancellationToken cancellationToken)
    {
        var (instrument, problem) = await TrainingAnalyserAsync(code, cancellationToken);
        if (instrument is null)
        {
            return problem!;
        }

        var gateway = await settings.ReadValueAsync<LabGatewaySettings>(LabSettingKeys.Gateway, null, cancellationToken) ?? new LabGatewaySettings();
        var zone = LabRuntimeOptions.From(gateway, instrument).Zone;
        var now = time.GetUtcNow();
        var results = request.Results.Select(r => r with { Channel = r.Channel.Trim(), Value = r.Value.Trim(), Units = r.Units?.Trim() }).ToList();
        var barcode = request.SampleBarcode.Trim();
        var local = TimeZoneInfo.ConvertTime(now, zone);
        string raw;
        IReadOnlyList<InstrumentResultMessage> messages;
        if (instrument.Protocol == LabProtocol.Hl7v2)
        {
            raw = Hl7Oru(instrument.Code, barcode, results, local);
            if (!Hl7Message.TryParse(raw, out var message, out var error))
            {
                return Error.Rule("lab.training_message", error);
            }

            var mapped = Hl7ResultMapper.Map(message, instrument.Code, instrument.SampleIdFields, raw, now, zone);
            if (mapped.Error is { } mapError)
            {
                return new TrainingResultsOutcome(false, $"The analyser's message could not be read: {mapError}", raw);
            }

            messages = mapped.Messages;
        }
        else
        {
            raw = AstmResults(instrument.Code, barcode, results, local);
            messages = AstmMessageMapper.MapResults(AstmRecords.Parse(raw), instrument.Code, raw, now, zone);
            if (messages.Count == 0)
            {
                return new TrainingResultsOutcome(false, "The analyser's message held no results the gateway could read.", raw);
            }
        }

        var outcome = await processor.SubmitResultsAsync(instrument, messages, raw, cancellationToken);
        return outcome.Accepted
            ? new TrainingResultsOutcome(true, $"{results.Count} result(s) for sample {barcode} were received from {instrument.Code}.", raw)
            : new TrainingResultsOutcome(false, outcome.Error ?? "The LIS refused the results.", raw);
    }

    /// <summary>The analyser's connection, when it is a training analyser this user may use; otherwise why not.</summary>
    private async Task<(LabInstrumentSettings? Instrument, Error? Problem)> TrainingAnalyserAsync(string code, CancellationToken cancellationToken)
    {
        if (services.GetService<IInstrumentResultSink>() is not { } sink)
        {
            return (null, Error.Rule("lab.training_unavailable", "The LIS is not available on this server."));
        }

        var profile = await sink.GetInstrumentAsync(code.Trim(), cancellationToken);
        if (profile is null || (currentUser.FacilityId is { } mine && profile.FacilityId != mine))
        {
            return (null, Error.NotFound("Analyser", code));
        }

        var gateway = await settings.ReadValueAsync<LabGatewaySettings>(LabSettingKeys.Gateway, null, cancellationToken);
        var instruments = await settings.ReadValueAsync<List<LabInstrumentSettings>>(LabSettingKeys.Instruments, null, cancellationToken) ?? [];
        var instrument = instruments.FirstOrDefault(i => string.Equals(i.Code, profile.Code, StringComparison.OrdinalIgnoreCase));
        if (instrument is not { Transport: LabTransport.Simulator })
        {
            return (null, Error.Rule("lab.not_training_analyser",
                $"{profile.Code} is not the training analyser. Set its connection to Training (simulator) in Lab setup › Analysers to send results from the screen."));
        }

        if (gateway?.Enabled != true)
        {
            return (null, Error.Rule("lab.gateway_off", "The laboratory gateway is off. Turn it on in Administration › Integrations › Laboratory equipment."));
        }

        if (!instrument.Enabled || !profile.IsActive)
        {
            return (null, Error.Rule("lab.training_off", $"{profile.Code} is switched off. Switch its connection and the analyser on in Lab setup."));
        }

        // The stored code's case, as the LIS registers it.
        return (instrument with { Code = profile.Code }, null);
    }

    /// <summary>An ORU^R01 (HL7 2.5.1) as an analyser sends it: the barcode in OBR-2, OBR-3 and SPM-2, one OBX per result.</summary>
    internal static string Hl7Oru(string code, string barcode, IReadOnlyList<TrainingResult> results, DateTimeOffset localNow)
    {
        var stamp = localNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $@"MSH|^~\&|{code}|HC360 training|HC360|HC360|{stamp}||ORU^R01^ORU_R01|TRN{localNow.ToUnixTimeMilliseconds()}|P|2.5.1",
            "PID|1",
            $"OBR|1|{barcode}|{barcode}|^Training results|||{stamp}",
        };
        lines.AddRange(results.Select((r, i) =>
            $"OBX|{i + 1}|{(IsNumber(r.Value) ? "NM" : "ST")}|{r.Channel}^{r.Channel}||{r.Value}|{r.Units}||||||F|||{stamp}"));
        lines.Add($"SPM|1|{barcode}");
        return string.Join('\r', lines) + "\r";
    }

    /// <summary>An ASTM E1394 result message: header, patient, the order with the barcode in O.3, one R record per result.</summary>
    internal static string AstmResults(string code, string barcode, IReadOnlyList<TrainingResult> results, DateTimeOffset localNow)
    {
        var stamp = localNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var tests = string.Join(@"\", results.Select(r => $"^^^{r.Channel}"));
        var records = new StringBuilder()
            .Append($@"H|\^&|||{code}^HC360 training|||||||P|1|{stamp}").Append('\r')
            .Append("P|1").Append('\r')
            .Append($"O|1|{barcode}||{tests}|R||||||N||||||||||||||F").Append('\r');
        for (var i = 0; i < results.Count; i++)
        {
            records.Append($"R|{i + 1}|^^^{results[i].Channel}|{results[i].Value}|{results[i].Units}||||F||||{stamp}").Append('\r');
        }

        return records.Append("L|1|N").Append('\r').ToString();
    }

    private static bool IsNumber(string value) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
}
