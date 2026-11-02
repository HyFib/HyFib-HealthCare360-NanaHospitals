using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// System health's warning about records the simulators made (audit P6): it stands while the latest sweep found records no
/// sign-off covers, as serious as the most serious kind not covered, and comes back when a simulator makes another record
/// after the sign-off, naming only that kind. A critical record still open keeps it critical whatever was signed off, and
/// no sign-off is taken while one is open.
/// </summary>
public sealed class SimulatorRecordWarningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_swept_or_nothing_found_raises_nothing()
    {
        SimulatorRecordSweep.Warning(null, null).ShouldBeNull();
        SimulatorRecordSweep.Warning(Sweep(Found("payments", "critical", 0, null)), null).ShouldBeNull();
    }

    [Fact]
    public void Records_not_signed_off_warn_as_the_most_serious_kind_found()
    {
        var sweep = Sweep(Found("payments", "critical", 3, Now.AddDays(-20)), Found("messages", "warning", 40, Now.AddDays(-19)), Found("labels", "info", 0, null));

        var warning = SimulatorRecordSweep.Warning(sweep, null).ShouldNotBeNull();

        (warning.Code, warning.Severity).ShouldBe(("simulators.records", "critical"));
        warning.Message.ShouldContain("title payments (3); title messages (40)");
        warning.Message.ShouldNotContain("labels");
        warning.Message.ShouldContain("Administration › Simulator records");
    }

    [Fact]
    public void Messages_and_labels_alone_are_a_warning()
    {
        var sweep = Sweep(Found("messages", "warning", 2, Now.AddDays(-5)), Found("labels", "info", 7, Now.AddDays(-4)));

        SimulatorRecordSweep.Warning(sweep, null).ShouldNotBeNull().Severity.ShouldBe("warning");
        SimulatorRecordSweep.Warning(Sweep(Found("labels", "info", 7, Now.AddDays(-4))), null).ShouldNotBeNull().Severity.ShouldBe("warning", "a warning, never only information, until signed off");
    }

    [Fact]
    public void A_sign_off_covers_the_records_its_sweep_found()
    {
        var sweep = Sweep(Found("payments", "critical", 3, Now.AddDays(-20)), Found("messages", "warning", 40, Now.AddDays(-19)));

        SimulatorRecordSweep.Warning(sweep, SignOff(sweep)).ShouldBeNull();
    }

    [Fact]
    public void A_record_made_after_the_sign_off_brings_the_warning_back_naming_its_kind_alone()
    {
        var reviewed = Sweep(Found("payments", "critical", 3, Now.AddDays(-20)), Found("messages", "warning", 40, Now.AddDays(-19)));
        var later = Sweep(Found("payments", "critical", 3, Now.AddDays(-20)), Found("messages", "warning", 41, Now.AddHours(-1)));

        var warning = SimulatorRecordSweep.Warning(later, SignOff(reviewed)).ShouldNotBeNull();

        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldContain("title messages (41)");
        warning.Message.ShouldNotContain("payments");
    }

    [Fact]
    public void A_critical_record_still_open_keeps_the_warning_critical_after_a_sign_off_and_says_how_many()
    {
        var sweep = Sweep(Found("payments", "critical", 3, Now.AddDays(-20), open: 2), Found("messages", "warning", 40, Now.AddDays(-19)));

        var warning = SimulatorRecordSweep.Warning(sweep, SignOff(sweep)).ShouldNotBeNull();

        warning.Severity.ShouldBe("critical");
        warning.Message.ShouldContain("not corrected yet: title payments (2 of 3 still open)");
        warning.Message.ShouldNotContain("messages", Case.Insensitive, "only what is still open, not what the sign-off covers");
    }

    [Fact]
    public void Critical_records_all_corrected_wait_only_for_the_sign_off_as_a_warning()
    {
        var sweep = Sweep(Found("payments", "critical", 3, Now.AddDays(-20), open: 0), Found("refunds", "critical", 0, null, open: 0));

        var warning = SimulatorRecordSweep.Warning(sweep, null).ShouldNotBeNull();

        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldContain("title payments (3)");
        SimulatorRecordSweep.Warning(sweep, SignOff(sweep)).ShouldBeNull();
    }

    [Fact]
    public void No_sign_off_while_a_critical_record_is_open_now_or_in_the_sweep_signed_off()
    {
        var corrected = Sweep(Found("payments", "critical", 3, Now.AddDays(-20), open: 0), Found("messages", "warning", 40, Now.AddDays(-19)));

        // Open now: named by kind, with how many.
        var now = SimulatorRecordSweep.SignOffRefusal(corrected, new Dictionary<string, long> { ["payments"] = 0, ["aadhaar-links"] = 2 }).ShouldNotBeNull();
        now.Code.ShouldBe("simulators.critical_open");
        now.Message.ShouldContain("2 record(s)");
        now.Message.ShouldContain("patients linked to an aadhaar number by the aadhaar simulator (2)");

        // Corrected since the sweep: that sweep still shows them open, so it is swept again before the sign-off.
        var stale = Sweep(Found("payments", "critical", 3, Now.AddDays(-20), open: 1));
        SimulatorRecordSweep.SignOffRefusal(stale, new Dictionary<string, long> { ["payments"] = 0 }).ShouldNotBeNull().ShouldSatisfyAllConditions(
            e => e.Code.ShouldBe("simulators.critical_open"),
            e => e.Message.ShouldContain("sweep again"));

        // Every critical record corrected: messages and labels are for the IT officer to acknowledge.
        SimulatorRecordSweep.SignOffRefusal(corrected, new Dictionary<string, long> { ["payments"] = 0 }).ShouldBeNull();
    }

    private static SimulatorSweep Sweep(params SimulatorRecordFinding[] findings) => new(Now, "IT Officer", findings);

    private static SimulatorRecordFinding Found(string code, string severity, long count, DateTimeOffset? last, long? open = null) =>
        new(code, "Title " + code, severity, count, null, last?.AddDays(-1), last, [], "What to do.", open);

    private static SimulatorSignOff SignOff(SimulatorSweep sweep) =>
        new(Now.AddHours(1), "IT Officer", sweep.SweptAt, sweep.Newest, sweep.Total, "Dealt with each.");
}
