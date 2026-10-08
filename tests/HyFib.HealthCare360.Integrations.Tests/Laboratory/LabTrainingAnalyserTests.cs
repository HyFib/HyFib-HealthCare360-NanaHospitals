using HyFib.HealthCare360.Integrations.Features.LabConnections;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Hl7;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>
/// The training analyser writes the message a real analyser would send, and the gateway's own mappers read it back:
/// the sample barcode where analysers put it, one result per channel, values and units as entered.
/// </summary>
public sealed class LabTrainingAnalyserTests
{
    private static readonly DateTimeOffset LocalNow = new(2026, 10, 8, 16, 30, 0, TimeSpan.FromHours(5.5));

    private static readonly TrainingResult[] Results = [new("GLU", "96", "mg/dL"), new("CREA", "Haemolysed", null), new("NA", "<120", "mmol/L")];

    [Theory]
    [InlineData(null)]
    [InlineData("OBR-3")]
    [InlineData("SPM-2")]
    [InlineData("OBR-2")]
    public void An_hl7_analysers_results_are_read_back_whichever_field_it_takes_the_barcode_from(string? field)
    {
        var raw = LabTrainingAnalyser.Hl7Oru("CL900I", "LS2600000123", Results, LocalNow);

        Hl7Message.TryParse(raw, out var message, out var error).ShouldBeTrue(error);
        Hl7ResultMapper.IsResultMessage(message!).ShouldBeTrue();
        var mapped = Hl7ResultMapper.Map(message!, "CL900I", field is null ? null : [field], raw, LabSamples.ReceivedAt, LabSamples.India);

        mapped.Error.ShouldBeNull();
        var sample = mapped.Messages.ShouldHaveSingleItem();
        sample.SampleBarcode.ShouldBe("LS2600000123");
        sample.Results.Select(r => (r.ChannelCode, r.Value, r.Units)).ShouldBe(
            [("GLU", "96", "mg/dL"), ("CREA", "Haemolysed", null), ("NA", "<120", "mmol/L")]);
        sample.Results[0].ResultedAt.ShouldBe(LocalNow);
    }

    [Fact]
    public void An_astm_analysers_results_are_read_back_from_its_records()
    {
        var raw = LabTrainingAnalyser.AstmResults("EM360", "LS2600000123", Results, LocalNow);

        var sample = AstmMessageMapper.MapResults(AstmRecords.Parse(raw), "EM360", raw, LabSamples.ReceivedAt, LabSamples.India).ShouldHaveSingleItem();

        sample.SampleBarcode.ShouldBe("LS2600000123");
        sample.Results.Select(r => (r.ChannelCode, r.Value, r.Units)).ShouldBe(
            [("GLU", "96", "mg/dL"), ("CREA", "Haemolysed", null), ("NA", "<120", "mmol/L")]);
        sample.Results[1].ResultedAt.ShouldBe(LocalNow);
    }

    [Theory]
    [InlineData("LS2600000123", "GLU", "96", null, true)]
    [InlineData("LS 26", "GLU", "96", null, false)]
    [InlineData("LS2600000123", "GLU^X", "96", null, false)]
    [InlineData("LS2600000123", "GLU", "9|6", null, false)]
    [InlineData("LS2600000123", "GLU", "96", "mg\\dL", false)]
    [InlineData("LS2600000123", "GLU", "9\r6", null, false)]
    public void Only_values_analyser_messages_can_carry_are_sent(string barcode, string channel, string value, string? units, bool valid) =>
        new TrainingResultsRequestValidator().Validate(new TrainingResultsRequest(barcode, [new TrainingResult(channel, value, units)]))
            .IsValid.ShouldBe(valid);

    [Fact]
    public void Each_test_is_sent_once()
    {
        var request = new TrainingResultsRequest("LS2600000123", [new("GLU", "96", null), new("glu", "97", null)]);

        new TrainingResultsRequestValidator().Validate(request).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_training_analyser_needs_no_port_or_agent_and_shows_as_training()
    {
        var training = new LabInstrumentSettings { Code = "EM360", Protocol = LabProtocol.Astm, Transport = LabTransport.Simulator };
        var state = new LabGatewayState(TimeProvider.System);

        training.Validate().ShouldBeNull();
        state.TrainingReady("EM360", LabProtocol.Astm);
        state.Snapshot().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe("training"), s => s.Transport.ShouldBe("simulator"));

        state.RetainTraining([]);
        state.Snapshot().ShouldBeEmpty();
    }
}
