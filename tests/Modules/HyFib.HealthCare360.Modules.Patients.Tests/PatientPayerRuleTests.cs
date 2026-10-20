using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>The patient banner's payer (UX-04): the open visit's, else the last visit's that was not cancelled.</summary>
public sealed class PatientPayerRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 30, 0, TimeSpan.FromHours(5.5));

    private static PayerVisit Visit(VisitStatus status, int daysAgo = 0, PayerType payer = PayerType.Self) =>
        new(Guid.NewGuid(), status, Now.AddDays(-daysAgo), payer, payer == PayerType.Self ? null : Guid.NewGuid());

    [Fact]
    public void Prefers_the_open_visit_then_the_latest_one_that_was_not_cancelled()
    {
        var done = Visit(VisitStatus.Completed, daysAgo: 1);
        var open = Visit(VisitStatus.InProgress, daysAgo: 2);
        var cancelled = Visit(VisitStatus.Cancelled);

        PatientPayerRule.Choose([cancelled, done, open]).ShouldBe((open, true));
        PatientPayerRule.Choose([cancelled, done]).ShouldBe((done, false));
        PatientPayerRule.Choose([cancelled]).ShouldBeNull();
        PatientPayerRule.Choose([]).ShouldBeNull();
    }

    [Fact]
    public void A_checked_in_visit_is_open_too_and_the_newest_open_one_wins()
    {
        var older = Visit(VisitStatus.Open, daysAgo: 3, PayerType.Corporate);
        var newer = Visit(VisitStatus.Open, daysAgo: 1, PayerType.Insurance);

        PatientPayerRule.Choose([older, newer]).ShouldBe((newer, true));
    }

    [Fact]
    public void The_latest_finished_visit_wins_whatever_order_the_visits_come_in()
    {
        var latest = Visit(VisitStatus.Completed, daysAgo: 1, PayerType.GovernmentScheme);
        var earlier = Visit(VisitStatus.Completed, daysAgo: 5, PayerType.Insurance);

        PatientPayerRule.Choose([earlier, latest]).ShouldBe((latest, false));
    }
}
