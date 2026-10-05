using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>Which analysers are sent a sample's order unasked, and under which channel codes (INT-LAB order download).</summary>
public sealed class AnalyserRoutingTests
{
    private static readonly LabInstrument Chemistry = Analyser("EM360", LabSection.Biochemistry);
    private static readonly LabInstrument Haematology = Analyser("XN550", LabSection.Hematology);
    private static readonly LabInstrument Immunoassay = Analyser("CL900I", LabSection.Immunology);

    private static LabInstrument Analyser(string code, LabSection section, bool bidirectional = true, bool active = true) => new()
    {
        Code = code,
        Name = code,
        Section = section,
        Protocol = InstrumentProtocol.Astm,
        IsBidirectional = bidirectional,
        IsActive = active,
    };

    private static LabTest Analyte(string code, params string[] channels) => new() { Code = code, Name = code, InstrumentChannelCodes = [.. channels] };

    [Fact]
    public void Plain_codes_go_only_to_analysers_on_the_tubes_bench()
    {
        var glucose = Analyte("GLU", "GLU");
        var creatinine = Analyte("CREA", "CREA");

        var targets = AnalyserRouting.Targets([Chemistry, Haematology, Immunoassay], LabSection.Biochemistry, [glucose, creatinine]);

        var target = targets.ShouldHaveSingleItem();
        target.InstrumentCode.ShouldBe("EM360");
        target.ChannelCodes.ShouldBe(["GLU", "CREA"]);
    }

    [Fact]
    public void An_analysers_own_code_reaches_it_from_another_bench()
    {
        // HbA1c is a chemistry test run on the haematology analyser here; glucose stays with chemistry.
        var hba1c = Analyte("HBA1C", "HBA1C", "XN550:A1C");
        var glucose = Analyte("GLU", "GLU");

        var targets = AnalyserRouting.Targets([Haematology, Chemistry], LabSection.Biochemistry, [hba1c, glucose]);

        targets.Select(t => t.InstrumentCode).ShouldBe(["EM360", "XN550"]);
        targets[0].ChannelCodes.ShouldBe(["HBA1C", "GLU"]);
        targets[1].ChannelCodes.ShouldBe(["A1C"]);
    }

    [Fact]
    public void Analysers_that_are_off_unidirectional_or_already_have_the_sample_are_left_out()
    {
        var glucose = Analyte("GLU", "GLU");
        LabInstrument[] analysers =
        [
            Analyser("C311", LabSection.Biochemistry, bidirectional: false),
            Analyser("C501", LabSection.Biochemistry, active: false),
            Chemistry,
            Analyser("AU480", LabSection.Biochemistry),
        ];

        var targets = AnalyserRouting.Targets(analysers, LabSection.Biochemistry, [glucose], except: "em360");

        targets.Select(t => t.InstrumentCode).ShouldBe(["AU480"]);
    }

    [Fact]
    public void No_target_when_no_analyser_runs_the_tests()
    {
        var culture = Analyte("CULT");

        AnalyserRouting.Targets([Chemistry, Haematology], LabSection.Microbiology, [culture]).ShouldBeEmpty();
    }

    [Fact]
    public void Channel_codes_are_listed_once_whatever_their_case()
    {
        var a = Analyte("NA", "EM360:ISE");
        var b = Analyte("K", "em360:ise");

        AnalyserRouting.Channels([a, b], Chemistry, LabSection.Biochemistry).ShouldBe(["ISE"]);
    }

    [Fact]
    public void Host_queries_still_accept_plain_codes_from_any_bench()
    {
        var tsh = Analyte("TSH", "TSH");

        tsh.ChannelFor("EM360").ShouldBe("TSH");
        tsh.ChannelFor("EM360", allowPlain: false).ShouldBeNull();
        Analyte("TSH", "TSH", "EM360:TSH3").ChannelFor("EM360", allowPlain: false).ShouldBe("TSH3");
    }
}
