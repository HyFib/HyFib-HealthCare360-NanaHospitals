using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>
/// The HORIBA Yumizen H500 / H500E dialect against HORIBA's "Output Format for Host Connection" (RAA085BEN). The frames
/// used here are the manual's examples whose printed checksums match their bytes, so they are what the analyser sends
/// and expects.
/// </summary>
public sealed class YumizenH500Tests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static readonly AstmDialect H500 = AstmDialect.YumizenH500;

    /// <summary>§3.6.1: the analyser asks for sample 0124.</summary>
    private static readonly string[] Query =
    [
        @"H|\^&|||H500/H500E^112YADH47745^3.0.0.3a|||||||P|LIS2-A2|20210709175737",
        "Q|1|^0124||ALL||||||||O",
        "L|1|N",
    ];

    /// <summary>§3.6.3: a patient's DIF results (trimmed to three parameters), with its alarms, curves and settings.</summary>
    private static readonly string[] DifResults =
    [
        @"H|\^&|||H500/H500E^112YADH47745^3.0.0.3a|||||||P|LIS2-A2|20210709175022",
        "P|1||||||^31^Y|M|||||||||||||||||echotomogr|||||||||MAN",
        "O|1|0566||^^^DIF|R|20210707172907|||||||||BLOOD||||||||||",
        @"C|1|I|CONDITIONS^^REAGENT_EXPIRED\S^PLT^PLT_ABN_HIST^SEP_RBC_PLT\SUSPECTED_PATHOLOGY^^DENGUE|I",
        "C|2|I|This is a comment 567 ?|G",
        "M|5|SETTING|RUO\\WBCDIFF|TRUE\\5",
        "R|1|^^^WBC^6690-2|9.45|1E03/mm3|3.50 - 10.00^REFERENCE_RANGE|N||F||LabMan_111^^LABMANAGER|20210707172907|20210707172907|112YADH47745",
        "R|3|^^^HGB^718-7|10.9|g/dL|13.0 - 17.0^REFERENCE_RANGE|L||F||LabMan_111^^LABMANAGER|20210707172907|20210707172907|112YADH47745",
        "C|1|I|S^RBC_OTH^OOR_HGB^LOQ|I",
        "R|27|^^^LIC%^55433-7|3.2|%|0.0 - 3.0^REFERENCE_RANGE|HH||F||LabMan_111^^LABMANAGER|20210707172907|20210707172907|112YADH47745",
        "L|1|N",
    ];

    private static readonly InstrumentOrder Order = new("0124", "NAME FIRSTNAME OF A LONG FAMILY", "M", new DateOnly(1990, 5, 22), ["WBC", "HGB", "LYM#"])
    {
        PatientId = "NH-UH-1",
        Priority = OrderPriority.Routine,
        CollectedAt = new DateTimeOffset(1990, 5, 22, 3, 50, 0, Ist),
        SpecimenType = "WholeBlood",
    };

    private static readonly DateTimeOffset AnsweredAt = new(2021, 7, 9, 17, 57, 57, Ist);

    private static IReadOnlyList<AstmRecord> Parse(params string[] records) => AstmRecords.Parse(string.Join("\r", records), H500);

    private static IReadOnlyList<InstrumentResultMessage> Map(params string[] records)
    {
        var raw = string.Join("\r", records);
        return AstmMessageMapper.MapResults(AstmRecords.Parse(raw, H500), "H500", raw, LabSamples.ReceivedAt, LabSamples.India, H500);
    }

    private static IReadOnlyList<string> Answer(IReadOnlyList<InstrumentOrder> orders, string? receiver = null) =>
        AstmMessageMapper.BuildOrderResponse(orders, AstmDelimiters.Default, AnsweredAt, LabSamples.India, H500, ["0124"], receiver);

    private static byte[] Frame(string text, string checksum) =>
        [AstmControl.Stx, .. Encoding.ASCII.GetBytes(text), AstmControl.Etx, .. Encoding.ASCII.GetBytes(checksum), AstmControl.Cr, AstmControl.Lf];

    [Theory]
    [InlineData("2Q|1|^0124||ALL||||||||O\r", "E1")]
    [InlineData("4O|1|0124||^^^DIF|R||19900522035000||||N||||BLOOD||||||||||Q\r", "B9")]
    [InlineData("2R|1|^^^WBC^6690-2|9.45|1E03/mm3|3.50 - 10.00^REFERENCE_RANGE|N||F||LabMan_111^^LABMANAGER|20210707172907|20210707172907|112YADH47745\r", "EF")]
    [InlineData("3O|1|QCL4||^^^DIF|R|20210706094947|||||||||CONTROL^^CTRL LOW||||||||||F\r", "6F")]
    public void The_manuals_frames_pass_the_checksum(string text, string checksum) =>
        AstmFrames.TryParse(Frame(text, checksum), Encoding.UTF8, out _, out var error).ShouldBeTrue(error);

    [Fact]
    public void Reads_the_queried_sample() =>
        AstmMessageMapper.QueriedSampleIds(Parse(Query)).ShouldBe(["0124"]);

    [Fact]
    public void Answers_a_query_with_the_panel_the_pending_parameters_need()
    {
        var records = Answer([Order]);

        records.Count.ShouldBe(4);
        records[0].ShouldBe(@"H|\^&||||||||||P|LIS2-A2|20210709175757");
        // The name component is cut to the analyser's 20 characters.
        records[1].ShouldBe("P|1|NH-UH-1|||NAME FIRSTNAME OF A||19900522|M");
        // Byte for byte the manual's order: DIF (LYM# is pending), routine, collected at, new, BLOOD, answer to a query.
        records[2].ShouldBe("O|1|0124||^^^DIF|R||19900522035000||||N||||BLOOD||||||||||Q");
        records[3].ShouldBe("L|1|N");
    }

    [Fact]
    public void Answers_a_sample_it_has_nothing_for_exactly_as_the_manual_does()
    {
        // §3.6.2, "Example of a Query Without Response", frame for frame with the manual's checksums.
        var frames = AstmFrames.BuildMessage(Answer([]), Encoding.UTF8);

        frames.ShouldBe(
        [
            Frame(@"1H|\^&||||||||||P|LIS2-A2|20210709175757" + "\r", "94"),
            Frame("2P|1|\r", "BB"),
            Frame("3O|1|0124||^^^|||||||N||||||||||||||Y\r", "67"),
            Frame("4L|1|N\r", "07"),
        ]);
    }

    [Fact]
    public void Copies_the_querys_receiver_into_the_answers_sender() =>
        Answer([], receiver: "LIS-NANA").First().ShouldStartWith(@"H|\^&|||LIS-NANA|");

    [Theory]
    [InlineData(new[] { "WBC", "HGB", "PLT" }, new[] { "CBC" })]
    [InlineData(new[] { "HGB", "NEU%" }, new[] { "DIF" })]
    [InlineData(new[] { "ESR" }, new[] { "ESR" })]
    [InlineData(new[] { "HGB", "esr" }, new[] { "CBC", "ESR" })]
    [InlineData(new[] { "LYM#", "ESR" }, new[] { "DIF", "ESR" })]
    [InlineData(new[] { "DIF" }, new[] { "DIF" })]
    [InlineData(new[] { "CBC" }, new[] { "CBC" })]
    public void Orders_by_panel(string[] channels, string[] panels) =>
        H500.OrderedTests(channels).ShouldBe(panels);

    [Fact]
    public void Other_analysers_are_ordered_by_channel() =>
        AstmDialect.Standard.OrderedTests(["HGB", "LYM#"]).ShouldBe(["HGB", "LYM#"]);

    [Fact]
    public void Several_panels_repeat_in_one_order_as_in_the_manuals_multitest_example()
    {
        var order = Order with { ChannelCodes = ["NEU#", "ESR"] };

        Answer([order])[2].ShouldBe("O|1|0124||^^^DIF\\^^^ESR|R||19900522035000||||N||||BLOOD||||||||||Q");
    }

    [Fact]
    public void An_urgent_sample_goes_as_stat_since_the_analyser_knows_no_asap()
    {
        H500.Priority(OrderPriority.Urgent).ShouldBe("S");
        AstmDialect.Standard.Priority(OrderPriority.Urgent).ShouldBe("A");
    }

    [Fact]
    public void Maps_a_patients_results_with_flags_and_alarms()
    {
        var message = Map(DifResults).ShouldHaveSingleItem();

        message.SampleBarcode.ShouldBe("0566");
        var at = new DateTimeOffset(2021, 7, 7, 17, 29, 7, Ist);
        message.Results.ShouldBe(
        [
            new InstrumentResult("WBC", "9.45", "1E03/mm3", "N", at) { Status = "F" },
            new InstrumentResult("HGB", "10.9", "g/dL", "L", at) { Status = "F", Comment = "S RBC_OTH OOR_HGB LOQ" },
            new InstrumentResult("LIC%", "3.2", "%", "HH", at) { Status = "F" },
        ]);
    }

    [Fact]
    public void Takes_the_sample_id_before_its_rack_components()
    {
        Map(
                @"H|\^&|||H500/H500E^110YOEH04272^4.0.0.5|||||||P|LIS2-A2|20240328163932",
                "P|1|||||||M",
                "O|1|SID-392180515^^00000000^1||^^^ESR|R|20240302010908|||||X||||BLOOD|||||AP^CHILD1|||||F",
                "R|1|^^^ESR^82477-1|10|mm/h|0 - 2^REFERENCE_RANGE|H||F||LabManager^^LABMANAGER|20240302010908|20240302010908|110YOEH04272",
                "L|1|N")
            .ShouldHaveSingleItem().SampleBarcode.ShouldBe("SID-392180515");
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("D")]
    public void Stores_no_qc_or_technician_run_as_patient_results(string processingId)
    {
        Map(
                $@"H|\^&|||H500/H500E^112YADH47745^3.0.0.3a|||||||{processingId}|LIS2-A2|20210709175358",
                "P|1",
                "O|1|QCL4||^^^DIF|R|20210706094947|||||||||CONTROL^^CTRL LOW||||||||||F",
                "R|1|^^^WBC^6690-2|9.68|1E03/mm3|0.00 - 2.00^REFERENCE_RANGE|N||F",
                "L|1|N")
            .ShouldBeEmpty();
    }

    [Fact]
    public void Skips_a_control_sample_but_keeps_the_patients_in_the_same_message()
    {
        var message = Map(
            @"H|\^&|||H500/H500E|||||||P|LIS2-A2|20210709175358",
            "P|1",
            "O|1|QCL4||^^^DIF|R|20210706094947|||||||||CONTROL^^CTRL LOW",
            "R|1|^^^WBC^6690-2|9.68|1E03/mm3||N||F",
            "P|2",
            "O|1|0566||^^^CBC|R|20210707172907|||||||||BLOOD",
            "R|1|^^^WBC^6690-2|7.10|1E03/mm3||N||F",
            "L|1|N").ShouldHaveSingleItem();

        message.SampleBarcode.ShouldBe("0566");
        message.Results.ShouldHaveSingleItem().Value.ShouldBe("7.10");
    }

    [Theory]
    [InlineData("---", "No result from the analyser (---)")]
    [InlineData("+++", "Above the analyser's reportable range (+++)")]
    public void A_dash_value_is_a_result_not_obtained(string value, string note)
    {
        var result = Map(@"H|\^&||||||||||P|LIS2-A2", "P|1", "O|1|0566||^^^CBC", $"R|1|^^^PLT^777-3|{value}|1E03/mm3||A||W", "L|1|N")
            .ShouldHaveSingleItem().Results.ShouldHaveSingleItem();

        result.Kind.ShouldBe(InstrumentResultKind.NotObtained);
        result.Comment.ShouldBe(note);
        result.Flags.ShouldBe("A");
    }

    [Fact]
    public void Other_analysers_keep_a_dash_value_as_the_value() =>
        AstmMessageMapper.MapResults(AstmRecords.Parse("H|\\^&\rO|1|S1\rR|1|^^^PLT|---||||F\rL|1|N"), "X", "", LabSamples.ReceivedAt, LabSamples.India)
            .ShouldHaveSingleItem().Results.ShouldHaveSingleItem().Kind.ShouldBe(InstrumentResultKind.Value);

    [Fact]
    public void Decodes_hexadecimal_escapes() =>
        AstmDelimiters.Default.Unescape("Line&X000D&two &X41& &Xzz& &X12345&").ShouldBe("Line\rtwo A &Xzz& &X12345&");

    [Fact]
    public void Reads_utf8_unless_the_connection_names_an_encoding()
    {
        var h500 = LabSamples.AstmInstrument with { AstmDialect = AstmDialect.YumizenH500Name };

        LabRuntimeOptions.From(new LabGatewaySettings(), h500).Encoding.ShouldBeOfType<UTF8Encoding>();
        LabRuntimeOptions.From(new LabGatewaySettings(), h500 with { Encoding = "latin1" }).Encoding.ShouldBe(Encoding.Latin1);
        LabRuntimeOptions.From(new LabGatewaySettings(), LabSamples.AstmInstrument).Encoding.ShouldBe(Encoding.Latin1);
    }

    [Fact]
    public void Sends_no_orders_unasked_since_the_analyser_ignores_them()
    {
        LabOrderQueue.UnaskedOrdersReason(LabSamples.AstmInstrument with { AstmDialect = AstmDialect.YumizenH500Name })
            .ShouldBe("Analyser C311 takes orders only when it asks for a sample (host query), so none are sent unasked.");
        LabOrderQueue.UnaskedOrdersReason(LabSamples.AstmInstrument with { AstmDialect = AstmDialect.YumizenC150Name }).ShouldBeNull();
        LabOrderQueue.UnaskedOrdersReason(LabSamples.AstmInstrument).ShouldBeNull();
    }
}
