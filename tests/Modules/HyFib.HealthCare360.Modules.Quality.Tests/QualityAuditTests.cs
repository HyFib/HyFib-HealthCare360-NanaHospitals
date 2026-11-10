using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain.Audits;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

public sealed class QualityAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    private static QualityAudit Audit(int items = 3)
    {
        var audit = new QualityAudit { AuditNumber = "NHAUD260001", Status = AuditStatus.Scheduled, ScheduledOn = new DateOnly(2026, 10, 5) };
        audit.Findings = Enumerable.Range(1, items).Select(i => new AuditFinding { AuditId = audit.Id, Order = i, ItemKey = $"I{i:00}", ItemText = $"Item {i}" }).ToList();
        return audit;
    }

    [Fact]
    public void An_audit_completes_only_when_every_item_is_answered_and_every_non_compliance_has_a_capa()
    {
        var audit = Audit();
        audit.Record("I01", FindingResult.Compliant, null).IsSuccess.ShouldBeTrue();
        audit.Record("I02", FindingResult.NonCompliant, "Two crash carts unchecked").IsSuccess.ShouldBeTrue();

        audit.Complete(null, Now).Error!.Code.ShouldBe("audit.incomplete");
        audit.CompletionGaps().ShouldBe(["1 item is not answered", "1 non-compliance has no CAPA"]);

        audit.Record("I03", FindingResult.NotApplicable, null);
        audit.Findings[1].CapaId = Guid.NewGuid();
        audit.Complete("Good overall", Now).IsSuccess.ShouldBeTrue();
        audit.Status.ShouldBe(AuditStatus.Completed);
        audit.Record("I01", FindingResult.NonCompliant, "late").Error!.Code.ShouldBe("audit.not_open");
    }

    [Fact]
    public void A_non_compliance_needs_an_observation_and_a_finding_with_a_capa_cannot_be_turned_compliant()
    {
        var audit = Audit();
        audit.Record("I01", FindingResult.NonCompliant, " ").IsFailure.ShouldBeTrue();
        audit.Record("I01", FindingResult.NonCompliant, "Expired drugs on shelf").IsSuccess.ShouldBeTrue();
        audit.Findings[0].CapaId = Guid.NewGuid();
        audit.Record("I01", FindingResult.Compliant, null).Error!.Code.ShouldBe("audit.finding_has_capa");
        audit.Record("X99", FindingResult.Compliant, null).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void An_audit_closes_only_when_every_capa_is_verified_or_cancelled()
    {
        var audit = Audit(1);
        audit.Record("I01", FindingResult.NonCompliant, "Hand rub dispensers empty");
        audit.Findings[0].CapaId = Guid.NewGuid();
        audit.Complete(null, Now);
        var user = Guid.NewGuid();

        audit.Close([CapaStatus.Completed], user, Now).Error!.Code.ShouldBe("audit.capas_open");
        audit.Close([CapaStatus.Verified, CapaStatus.Cancelled], user, Now).IsSuccess.ShouldBeTrue();
        audit.Status.ShouldBe(AuditStatus.Closed);
        audit.ClosedByUserId.ShouldBe(user);
    }

    [Fact]
    public void The_capa_owner_cannot_verify_their_own_capa()
    {
        var owner = Guid.NewGuid();
        var capa = new Capa { OwnerUserId = owner, Status = CapaStatus.Completed };

        capa.Verify(true, "Checked 20 charts", owner, Now).Error!.Code.ShouldBe("capa.self_verification");
        capa.Verify(true, "Checked 20 charts", Guid.NewGuid(), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Compliance_is_compliant_items_over_items_that_apply()
    {
        var audit = Audit(4);
        audit.CompliancePercent().ShouldBeNull();
        audit.Record("I01", FindingResult.Compliant, null);
        audit.Record("I02", FindingResult.Compliant, null);
        audit.Record("I03", FindingResult.NonCompliant, "Not documented");
        audit.Record("I04", FindingResult.NotApplicable, null);

        audit.CompliancePercent().ShouldBe(66.7m);
    }

    [Theory]
    [InlineData(AuditRecurrence.Once, null)]
    [InlineData(AuditRecurrence.Monthly, "2026-11-05")]
    [InlineData(AuditRecurrence.Quarterly, "2027-01-05")]
    [InlineData(AuditRecurrence.HalfYearly, "2027-04-05")]
    [InlineData(AuditRecurrence.Yearly, "2027-10-05")]
    public void A_recurring_audit_schedules_the_next_one(AuditRecurrence recurrence, string? next) =>
        QualityAudit.Next(new DateOnly(2026, 10, 5), recurrence).ShouldBe(next is null ? null : DateOnly.Parse(next, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void An_audit_that_raised_capas_cannot_be_cancelled()
    {
        var audit = Audit(1);
        audit.Record("I01", FindingResult.NonCompliant, "x");
        audit.Findings[0].CapaId = Guid.NewGuid();
        audit.Cancel("duplicate").Error!.Code.ShouldBe("audit.has_capas");
    }
}
