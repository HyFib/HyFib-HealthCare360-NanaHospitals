using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// System health's warning about records the simulators made (audit P6): it stands while the latest sweep found records no
/// sign-off covers, as serious as the most serious kind not covered, and comes back when a simulator makes another record
/// after the sign-off, naming only that kind.
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

    private static SimulatorSweep Sweep(params SimulatorRecordFinding[] findings) => new(Now, "IT Officer", findings);

    private static SimulatorRecordFinding Found(string code, string severity, long count, DateTimeOffset? last) =>
        new(code, "Title " + code, severity, count, null, last?.AddDays(-1), last, [], "What to do.");

    private static SimulatorSignOff SignOff(SimulatorSweep sweep) =>
        new(Now.AddHours(1), "IT Officer", sweep.SweptAt, sweep.Newest, sweep.Total, "Dealt with each.");
}
