using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class AstmMessageMapperTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static IReadOnlyList<InstrumentResultMessage> Map(params string[] records)
    {
        var raw = string.Join("\r", records);
        return AstmMessageMapper.MapResults(AstmRecords.Parse(raw), "C311", raw, LabSamples.ReceivedAt, LabSamples.India);
    }

    [Fact]
    public void Maps_result_records_to_the_sample_of_their_order()
    {
        var message = Map(LabSamples.AstmResultRecords).ShouldHaveSingleItem();

        message.InstrumentCode.ShouldBe("C311");
        message.SampleBarcode.ShouldBe("SMP-26090042");
        message.RawMessage.ShouldBe(string.Join("\r", LabSamples.AstmResultRecords));
        message.Results.ShouldBe(
        [
            new InstrumentResult("GLU", "98", "mg/dL", "N", new DateTimeOffset(2026, 9, 25, 10, 35, 0, Ist)),
            // No completion time on the record: the header's message time applies.
            new InstrumentResult("CREA", "1.4", "mg/dL", "H", new DateTimeOffset(2026, 9, 25, 10, 30, 0, Ist)),
        ]);
    }

    [Fact]
    public void Groups_results_per_sample_and_ignores_results_without_an_order()
    {
        var messages = Map(
            @"H|\^&",
            "P|1",
            "O|1|S1||^^^GLU",
            "R|1|^^^GLU|98|mg/dL",
            "P|2",
            "R|1|^^^GLU|120|mg/dL",
            "O|1|S2||^^^GLU",
            "R|1|^^^GLU|110|mg/dL",
            "O|2|S1||^^^CREA",
            "R|1|^^^CREA|1.0|mg/dL",
            "L|1|N");

        messages.Select(m => m.SampleBarcode).ShouldBe(["S1", "S2"]);
        messages[0].Results.Select(r => r.ChannelCode).ShouldBe(["GLU", "CREA"]);
        messages[1].Results.ShouldHaveSingleItem().Value.ShouldBe("110");
    }

    [Fact]
    public void Falls_back_to_the_instrument_specimen_id()
    {
        var order = AstmRecords.Parse("O|1||INST-77^5^2|^^^GLU")[0];

        AstmMessageMapper.SampleId(order).ShouldBe("INST-77");
        AstmMessageMapper.SampleId(AstmRecords.Parse("O|1|||^^^GLU")[0]).ShouldBeNull();
    }

    [Theory]
    [InlineData("R|1|^^^GLU|98|mg/dL||||X")]
    [InlineData("R|1|^^^GLU|98|mg/dL||||I")]
    [InlineData("R|1|^^^GLU||mg/dL||||F")]
    [InlineData("R|1|||mg/dL||||F")]
    public void Skips_results_that_carry_nothing_to_store(string record) =>
        AstmMessageMapper.Result(AstmRecords.Parse(record)[0], LabSamples.India, null).ShouldBeNull();

    [Fact]
    public void Uses_the_first_non_empty_test_id_component_when_the_manufacturer_code_is_missing() =>
        AstmMessageMapper.Result(AstmRecords.Parse("R|1|GLU|98|mg/dL||N||F")[0], LabSamples.India, null)!.ChannelCode.ShouldBe("GLU");

    [Fact]
    public void Reads_queried_sample_ids_and_ignores_the_all_wildcard()
    {
        var records = AstmRecords.Parse(string.Join("\r",
            @"H|\^&",
            @"Q|1|^SMP-1\^SMP-2||^^^ALL||||||||O",
            "Q|2|UHID1^SMP-3",
            "Q|3|ALL",
            "Q|4|^SMP-1",
            "L|1|N"));

        AstmMessageMapper.QueriedSampleIds(records).ShouldBe(["SMP-1", "SMP-2", "SMP-3"]);
    }

    [Fact]
    public void Answers_a_host_query_with_patient_and_order_records()
    {
        var orders = new List<InstrumentOrder>
        {
            new("SMP-1", "SHARMA^RAVI", "Male", new DateOnly(1980, 1, 15), ["GLU", "CREA"]),
            new("SMP-2", "No Tests", null, null, []),
        };

        var records = AstmMessageMapper.BuildOrderResponse(orders, AstmDelimiters.Default, new DateTimeOffset(2026, 9, 25, 10, 30, 0, Ist), LabSamples.India);

        records.Count.ShouldBe(4);
        records[0].ShouldBe(@"H|\^&|||HC360^1.0|||||||P|1|20260925103000");
        records[1].ShouldBe("P|1||||SHARMA&S&RAVI||19800115|M");
        records[3].ShouldBe("L|1|F");
        var order = AstmRecords.Parse(string.Join("\r", records))[2];
        order.Type.ShouldBe('O');
        order.Field(3).ShouldBe("SMP-1");
        order.Field(5).ShouldBe(@"^^^GLU\^^^CREA");
        order.Field(6).ShouldBe("R");
        order.Field(12).ShouldBe("N");
        order.Field(26).ShouldBe("Q");
    }

    [Fact]
    public void Reports_no_information_when_no_sample_has_tests() =>
        AstmMessageMapper.BuildOrderResponse([], AstmDelimiters.Default, DateTimeOffset.UnixEpoch, LabSamples.India)[^1].ShouldBe("L|1|I");

    [Fact]
    public void Sends_orders_unasked_with_the_action_priority_collection_time_and_hospital_id()
    {
        var order = new InstrumentOrder("000000123", "Ravi Sharma", "M", new DateOnly(1980, 1, 15), ["GLU", "CREA"])
        {
            PatientId = "NH-UH-26001",
            Priority = OrderPriority.Stat,
            CollectedAt = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero),
            SpecimenType = "Serum",
        };
        IReadOnlyList<LabDownload> downloads =
        [
            new(Guid.CreateVersion7(), InstrumentOrderAction.New, order),
            new(Guid.CreateVersion7(), InstrumentOrderAction.Cancel, new InstrumentOrder("000000124", string.Empty, null, null, ["TSH"])),
            new(Guid.CreateVersion7(), InstrumentOrderAction.Rerun, new InstrumentOrder("000000125", "Asha", "F", null, ["K"])),
        ];

        var records = AstmMessageMapper.BuildOrderDownload(downloads, AstmDelimiters.Default, new DateTimeOffset(2026, 9, 25, 10, 30, 0, Ist), LabSamples.India);

        records.Count.ShouldBe(8);
        records[1].ShouldBe("P|1|NH-UH-26001|||Ravi Sharma||19800115|M");
        var parsed = AstmRecords.Parse(string.Join("\r", records));
        var first = parsed[2];
        first.Field(3).ShouldBe("000000123");
        first.Field(5).ShouldBe(@"^^^GLU\^^^CREA");
        first.Field(6).ShouldBe("S");
        first.Field(8).ShouldBe("20260925093000");
        first.Field(12).ShouldBe("N");
        first.Field(16).ShouldBe("Serum");
        first.Field(26).ShouldBe("O");
        parsed[4].Field(12).ShouldBe("C");
        parsed[6].Field(12).ShouldBe("A");
        records[^1].ShouldBe("L|1|N");
    }

    [Theory]
    [InlineData(OrderPriority.Routine, "R")]
    [InlineData(OrderPriority.Urgent, "A")]
    [InlineData(OrderPriority.Stat, "S")]
    public void Maps_priorities_to_astm(OrderPriority priority, string expected) => AstmMessageMapper.Priority(priority).ShouldBe(expected);
}
