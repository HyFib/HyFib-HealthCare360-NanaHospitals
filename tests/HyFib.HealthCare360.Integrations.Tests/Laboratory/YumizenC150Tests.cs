using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>
/// The HORIBA Yumizen C150 dialect against the examples in HORIBA's "LIS Communication Protocol" for the C150. The
/// result frames of its "Result Packet" example carry checksums that match their bytes, so they are what the analyser
/// really sends; the field tables and the other examples are the manual's.
/// </summary>
public sealed class YumizenC150Tests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static readonly AstmDialect Yumizen = AstmDialect.YumizenC150;

    /// <summary>The manual's "Result Packet (Host to LIS)", frame by frame, as the analyser sent it.</summary>
    private static readonly string[] CapturedResultRecords =
    [
        @"H|\^&|||ABC^3.6^11052213|||||||E-1394-97|20131203141051",
        "P|1||||Chan Du|||M|||||25^Y",
        "R|1|^^^TP|10.00|g/dL|0^0|||N|F||||20131203141051",
        "R|2|^^^ALB|5.00|g/dL|0^0|||N|F||||20131203141051",
        "L|1|N",
    ];

    private static readonly InstrumentOrder Order = new("26090042", "SHARMA RAVI KUMAR WITH A VERY LONG NAME", "M", new DateOnly(1971, 7, 4), ["GLU", "UREA"])
    {
        PatientId = "NH-UH-1",
        Priority = OrderPriority.Routine,
        CollectedAt = new DateTimeOffset(2026, 9, 25, 9, 15, 0, Ist),
        SpecimenType = "Serum",
    };

    private static IReadOnlyList<InstrumentResultMessage> Map(AstmDialect dialect, params string[] records)
    {
        var raw = string.Join("\r", records);
        return AstmMessageMapper.MapResults(AstmRecords.Parse(raw, dialect), "C150", raw, LabSamples.ReceivedAt, LabSamples.India, dialect);
    }

    private static byte[] Frame(string text, string checksum) =>
        [AstmControl.Stx, .. Encoding.Latin1.GetBytes(text), AstmControl.Etx, .. Encoding.ASCII.GetBytes(checksum), AstmControl.Cr, AstmControl.Lf];

    [Theory]
    [InlineData("3R|1|^^^TP|10.00|g/dL|0^0|||N|F||||20131203141051\r", "0F")]
    [InlineData("4R|2|^^^ALB|5.00|g/dL|0^0|||N|F||||20131203141051\r", "10")]
    public void The_manuals_captured_result_frames_pass_the_checksum(string text, string checksum)
    {
        AstmFrames.TryParse(Frame(text, checksum), Encoding.Latin1, out var frame, out var error).ShouldBeTrue(error);

        frame.IsFinal.ShouldBeTrue();
        frame.Text.ShouldBe(text[1..]);
    }

    [Fact]
    public void Reads_the_shifted_result_records_the_analyser_sends()
    {
        var message = Map(Yumizen, CapturedResultRecords).ShouldHaveSingleItem();

        // No P.3 and no order record: kept for the lab to assign, with the name the operator typed.
        message.SampleBarcode.ShouldBe(string.Empty);
        message.Patient.ShouldBe(new InstrumentPatient(null, "Chan Du"));
        var completed = new DateTimeOffset(2013, 12, 3, 14, 10, 51, Ist);
        message.Results.ShouldBe(
        [
            new InstrumentResult("TP", "10.00", "g/dL", null, completed) { Status = "F" },
            new InstrumentResult("ALB", "5.00", "g/dL", null, completed) { Status = "F" },
        ]);
    }

    [Fact]
    public void Standard_astm_misreads_the_same_records()
    {
        // Why the dialect exists: E1394's places give status N (R.9, the nature of abnormality testing) instead of F,
        // and no sample at all.
        var message = Map(AstmDialect.Standard, CapturedResultRecords).ShouldHaveSingleItem();

        message.Results[0].Status.ShouldBe("N");
        message.SampleBarcode.ShouldBe(string.Empty);
    }

    [Fact]
    public void Reads_result_records_in_the_manuals_field_table_layout_too()
    {
        var message = Map(Yumizen,
            @"H|\^&|||||||||P|E 1394-97|20100705071134",
            "P|1|26090042||||||M",
            "R|1|^^^ALP|200|IU/L|DEFAULT|A|N|F||||20100513113450",
            "R|2|^^^AMY|93|U/L|DEFAULT|N|N|F||||20100513113535",
            "L|1|N").ShouldHaveSingleItem();

        message.SampleBarcode.ShouldBe("26090042");
        message.Results.Select(r => (r.ChannelCode, r.Value, r.Units, r.Flags, r.Status)).ShouldBe(
        [
            ("ALP", "200", "IU/L", "A", "F"),
            ("AMY", "93", "U/L", "N", "F"),
        ]);
        message.Results[0].ResultedAt.ShouldBe(new DateTimeOffset(2010, 5, 13, 11, 34, 50, Ist));
    }

    [Fact]
    public void Results_without_an_order_record_go_to_the_sample_in_the_patient_id()
    {
        var messages = Map(Yumizen,
            @"H|\^&|||ABC^3.6^11052213|||||||E-1394-97|20131203141051",
            "P|1|26090042|||SHARMA^RAVI|||M",
            "R|1|^^^GLU|98|mg/dL|0^0|||N|F||||20131203141051",
            "P|2|26090043",
            "R|1|^^^GLU|120|mg/dL|0^0|||N|F||||20131203141051",
            "L|1|N");

        messages.Select(m => m.SampleBarcode).ShouldBe(["26090042", "26090043"]);
        // P.3 is the tube, not the patient: the patient is named by P.6 alone.
        messages[0].Patient.ShouldBe(new InstrumentPatient(null, "SHARMA RAVI"));
        messages[1].Patient.ShouldBeNull();
    }

    [Fact]
    public void Standard_astm_keeps_results_without_an_order_under_no_sample_id()
    {
        Map(AstmDialect.Standard, @"H|\^&", "P|1|26090042", "R|1|^^^GLU|98|mg/dL||||F", "L|1|N")
            .ShouldHaveSingleItem().SampleBarcode.ShouldBe(string.Empty);
    }

    [Fact]
    public void A_query_for_several_ids_repeats_with_a_backtick_under_a_backslash_header()
    {
        var query = string.Join("\r",
            @"H|\^&|||ABC^3.6^11052213|||||||E-1394-97|20131205091027",
            "Q|1|^020100030279`020100030321`020100030304`020100030297|||S||||||O",
            "L|1|N");

        AstmMessageMapper.QueriedSampleIds(AstmRecords.Parse(query, Yumizen))
            .ShouldBe(["020100030279", "020100030321", "020100030304", "020100030297"]);
        // Read as E1394, the four ids would be one id the LIS cannot find.
        AstmMessageMapper.QueriedSampleIds(AstmRecords.Parse(query)).ShouldHaveSingleItem().ShouldContain('`');
    }

    [Fact]
    public void A_query_for_one_id_as_in_the_manual()
    {
        var query = string.Join("\r", @"H|\^&|||ABC^3.6^11052213|||||||E-1394-97|20131205091027", "Q|1|^pat1||||||||||0", "L|1|N");

        AstmMessageMapper.QueriedSampleIds(AstmRecords.Parse(query, Yumizen)).ShouldBe(["pat1"]);
    }

    [Fact]
    public void Answers_a_query_as_the_manuals_lis_example_does()
    {
        var delimiters = Yumizen.Incoming(AstmDelimiters.FromHeader(@"H|\^&|||ABC^3.6^11052213"));
        var now = new DateTimeOffset(2026, 9, 25, 10, 30, 0, Ist);

        var records = AstmMessageMapper.BuildOrderResponse([Order], delimiters, now, LabSamples.India, Yumizen);

        records.Count.ShouldBe(4);
        records[0].ShouldBe("H|`^&|||HC360^1.0|||||||P|E 1394-97|20260925103000");
        // P.3 the tube (the analyser's Patient ID), the name cut to 30, a 14-digit date of birth.
        records[1].ShouldBe("P|1|26090042|||SHARMA RAVI KUMAR WITH A VERY||19710704000000|M");
        var order = AstmRecords.Parse(string.Join("\r", records), Yumizen)[2];
        order.Field(3).ShouldBe("26090042");
        order.Field(5).ShouldBe("^^^GLU`^^^UREA");
        order.Repeats(5).Count.ShouldBe(2);
        order.Field(6).ShouldBe("R");
        order.Field(8).ShouldBe("20260925091500");
        order.Field(12).ShouldBe("N");
        order.Field(16).ShouldBe("SERUM");
        order.Field(26).ShouldBe("O");
        records[3].ShouldBe("L|1|N");
    }

    [Fact]
    public void An_unknown_sample_is_answered_with_a_header_and_a_normal_terminator()
    {
        var delimiters = Yumizen.Incoming(AstmDelimiters.Default);

        AstmMessageMapper.BuildOrderResponse([], delimiters, LabSamples.ReceivedAt, LabSamples.India, Yumizen)
            .ShouldBe([$"H|`^&|||HC360^1.0|||||||P|E 1394-97|{LabSamples.ReceivedAt.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}", "L|1|N"]);
    }

    [Theory]
    [InlineData(InstrumentOrderAction.New, "N")]
    [InlineData(InstrumentOrderAction.Rerun, "A")]
    [InlineData(InstrumentOrderAction.Cancel, "C")]
    public void Sends_orders_unasked_in_backtick_repeats(InstrumentOrderAction action, string actionCode)
    {
        var download = new LabDownload(Guid.CreateVersion7(), action, Order with { Priority = OrderPriority.Stat, SpecimenType = "WholeBlood" });

        var records = AstmMessageMapper.BuildOrderDownload([download], Yumizen.Outgoing, LabSamples.ReceivedAt, LabSamples.India, Yumizen);

        records[0].ShouldStartWith("H|`^&|");
        var order = AstmRecords.Parse(string.Join("\r", records), Yumizen)[2];
        order.Repeats(5).Select(r => r.Split('^')[3]).ShouldBe(["GLU", "UREA"]);
        order.Field(6).ShouldBe("S");
        order.Field(12).ShouldBe(actionCode);
        order.Field(16).ShouldBe("BLOOD");
        records[^1].ShouldBe("L|1|N");
    }

    [Fact]
    public void The_barcode_sent_in_an_order_comes_back_with_the_results()
    {
        var sent = AstmRecords.Parse(string.Join("\r",
            AstmMessageMapper.BuildOrderResponse([Order], Yumizen.Outgoing, LabSamples.ReceivedAt, LabSamples.India, Yumizen)), Yumizen);
        var patientId = sent.Single(r => r.Type == 'P').Value(3);

        Map(Yumizen,
                @"H|\^&|||ABC^3.6^11052213|||||||E-1394-97|20260925110000",
                $"P|1|{patientId}|||SHARMA RAVI|||M",
                "R|1|^^^GLU|98|mg/dL|0^0|||N|F||||20260925105900",
                "L|1|N")
            .ShouldHaveSingleItem().SampleBarcode.ShouldBe(Order.SampleBarcode);
    }

    [Theory]
    [InlineData("Serum", "SERUM")]
    [InlineData("Plasma", "PLASMA")]
    [InlineData("Urine", "URINE")]
    [InlineData("Csf", "CSF")]
    [InlineData("WholeBlood", "BLOOD")]
    [InlineData(null, "SERUM")]
    [InlineData("BodyFluid", "BODYFLUID")]
    public void Names_the_specimen_as_the_analyser_does(string? specimenType, string expected) =>
        Yumizen.Specimen(specimenType).ShouldBe(expected);

    [Fact]
    public void Standard_astm_sends_the_specimen_as_hc360_names_it()
    {
        AstmDialect.Standard.Specimen("WholeBlood").ShouldBe("WholeBlood");
        AstmDialect.Standard.Specimen(null).ShouldBeNull();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("YumizenC150", true)]
    [InlineData("yumizenc150", false)]
    [InlineData("Cobas", false)]
    public void A_connection_takes_only_a_known_dialect(string? dialect, bool valid) =>
        (LabSamples.AstmInstrument with { AstmDialect = dialect }).Validate().ShouldBe(valid ? null : "the ASTM dialect must be one of YumizenC150, or none");

    [Fact]
    public void An_unknown_stored_dialect_reads_as_standard_astm() =>
        AstmDialect.For("Cobas").ShouldBeSameAs(AstmDialect.Standard);
}
