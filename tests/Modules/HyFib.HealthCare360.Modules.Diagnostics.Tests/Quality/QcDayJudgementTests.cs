using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Quality;

/// <summary>
/// Today's QC position (LIS-06): auto-validation trusts only QC run on the analyser that measured the result, while a
/// rejected run on any analyser still locks the assay's verification.
/// </summary>
public sealed class QcDayJudgementTests
{
    private static readonly Guid Fbs = Guid.CreateVersion7();
    private static readonly Guid Ppbs = Guid.CreateVersion7();
    private static readonly Guid Creatinine = Guid.CreateVersion7();

    /// <summary>Fasting and post-prandial glucose share the glucose assay's QC; creatinine has its own.</summary>
    private static readonly Dictionary<Guid, string> Assays = new() { [Fbs] = "GLU", [Ppbs] = "GLU", [Creatinine] = "CREA" };

    private static readonly QcAssay[] Asked = [new(Fbs, "Fasting blood sugar", "GLU"), new(Ppbs, "Post-prandial blood sugar", "GLU")];

    private static QcRunFact Run(Guid labTestId, string? analyser, QcOutcome outcome, params string[] violations) =>
        new(Guid.CreateVersion7(), labTestId, Guid.CreateVersion7(), analyser, Now, outcome, [.. violations]);

    private static QcDayState Judge(string? analyser, QcRunFact[] latestPerLot, Guid[]? overridden = null) =>
        QualityControlService.Judge(Asked, Assays, latestPerLot, overridden ?? [], analyser);

    [Fact]
    public void Qc_accepted_on_one_analyser_does_not_let_another_analysers_results_auto_validate()
    {
        // The morning's glucose QC was run on the EM 360 only; the back-up analyser ran none.
        QcRunFact[] runs = [Run(Fbs, "EM360", QcOutcome.Accepted), Run(Fbs, "EM360", QcOutcome.AcceptedWithWarning, "1-2s")];

        Judge("BACKUP", runs).AcceptedTestIds.ShouldBeEmpty();
        Judge("EM360", runs).AcceptedTestIds.ShouldBe([Fbs, Ppbs], ignoreOrder: true);
    }

    [Fact]
    public void An_analysers_own_accepted_qc_lets_its_results_auto_validate_whatever_the_case_of_its_code()
    {
        QcRunFact[] runs = [Run(Fbs, "EM360", QcOutcome.Accepted), Run(Ppbs, "BACKUP", QcOutcome.Accepted)];

        Judge("backup", runs).AcceptedTestIds.ShouldBe([Fbs, Ppbs], ignoreOrder: true);
    }

    [Fact]
    public void A_lot_tied_to_no_analyser_lets_no_analysers_results_auto_validate() =>
        Judge("EM360", [Run(Fbs, null, QcOutcome.Accepted)]).AcceptedTestIds.ShouldBeEmpty();

    [Fact]
    public void Another_assays_qc_on_the_analyser_counts_for_nothing() =>
        Judge("EM360", [Run(Creatinine, "EM360", QcOutcome.Accepted)]).AcceptedTestIds.ShouldBeEmpty();

    [Fact]
    public void A_rejected_run_on_another_analyser_locks_the_assay_and_holds_this_analysers_results()
    {
        var rejected = Run(Fbs, "BACKUP", QcOutcome.Rejected, "1-3s");

        var day = Judge("EM360", [Run(Fbs, "EM360", QcOutcome.Accepted), rejected]);

        // Verification of the assay is locked whichever analyser gave the result (or none, for a value typed in)...
        day.Lockouts.Select(l => l.LabTestId).ShouldBe([Fbs, Ppbs], ignoreOrder: true);
        day.Lockouts.ShouldAllBe(l => l.RejectedRunIds.Single() == rejected.Id && l.Violations.Single() == "1-3s");

        // ...so auto-validation, which may never verify what a pathologist could verify only with an override, holds too.
        day.AcceptedTestIds.ShouldBeEmpty();
    }

    [Fact]
    public void An_override_lifts_the_lockout_but_not_the_hold_on_auto_validation()
    {
        var rejected = Run(Fbs, "EM360", QcOutcome.Rejected, "2-2s");

        var day = Judge("EM360", [rejected, Run(Fbs, "EM360", QcOutcome.Accepted)], overridden: [rejected.Id]);

        day.Lockouts.ShouldBeEmpty();
        day.AcceptedTestIds.ShouldBeEmpty();
    }

    [Fact]
    public void No_qc_today_locks_nothing_and_allows_no_auto_validation()
    {
        var day = Judge("EM360", []);

        day.Lockouts.ShouldBeEmpty();
        day.AcceptedTestIds.ShouldBeEmpty();
    }
}
