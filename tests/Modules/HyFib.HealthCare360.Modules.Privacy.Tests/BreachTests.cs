using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Tests;

public sealed class BreachTests
{
    private static readonly DateTimeOffset Aware = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    private static Breach Recorded(bool complete = false) => new()
    {
        BreachNumber = "DPB260001",
        Title = "Laptop with discharge summaries stolen",
        Description = "An unencrypted laptop holding 120 discharge summaries was stolen from the ward office.",
        AwareAt = Aware,
        Consequences = complete ? "Someone could learn about your treatment." : null,
        Mitigation = complete ? "We reported the theft to the police and changed passwords." : null,
        PrincipalSteps = complete ? "Be careful of calls asking about your treatment." : null,
        Cause = complete ? "The laptop was left unlocked." : null,
        Remediation = complete ? "All laptops are now encrypted." : null,
        PrincipalNotice = complete ? "A laptop with your discharge summary was stolen on 27 Sep." : null,
    };

    [Fact]
    public void The_detailed_report_is_due_72_hours_after_the_hospital_became_aware() =>
        Recorded().ReportDueAt.ShouldBe(Aware.AddHours(72));

    [Fact]
    public void An_extension_from_the_Board_moves_the_deadline()
    {
        var breach = Recorded();

        breach.RecordExtension(Aware.AddHours(48), null).Error!.Fields!.ShouldContainKey("until");
        breach.RecordExtension(Aware.AddDays(7), "DPB/EXT/42").IsSuccess.ShouldBeTrue();

        breach.ReportDueAt.ShouldBe(Aware.AddDays(7));
        breach.BoardReference.ShouldBe("DPB/EXT/42");
    }

    [Fact]
    public void The_report_is_overdue_until_it_is_sent()
    {
        var breach = Recorded(complete: true);
        var late = Aware.AddHours(80);

        breach.IsReportOverdue(late).ShouldBeTrue();
        breach.RecordBoardInformed(Aware.AddHours(2), null, late).IsSuccess.ShouldBeTrue();
        breach.IsReportOverdue(late).ShouldBeTrue();
        breach.RecordReportSubmitted(Aware.AddHours(79), "DPB/2026/77", late).IsSuccess.ShouldBeTrue();
        breach.IsReportOverdue(late).ShouldBeFalse();
    }

    [Fact]
    public void The_Board_is_told_once_at_a_time_that_makes_sense()
    {
        var breach = Recorded();
        var now = Aware.AddHours(5);

        breach.RecordBoardInformed(Aware.AddHours(-1), null, now).Error!.Fields!.ShouldContainKey("at");
        breach.RecordBoardInformed(now.AddHours(1), null, now).Error!.Fields!.ShouldContainKey("at");
        breach.RecordBoardInformed(Aware.AddHours(3), null, now).IsSuccess.ShouldBeTrue();
        breach.RecordBoardInformed(Aware.AddHours(4), null, now).Error!.Code.ShouldBe("privacy.breach_not_open");
    }

    [Fact]
    public void The_detailed_report_needs_what_rule_7_2_asks_for()
    {
        var breach = Recorded();
        var now = Aware.AddHours(10);
        breach.RecordBoardInformed(Aware.AddHours(1), null, now);

        var refused = breach.RecordReportSubmitted(Aware.AddHours(9), null, now);

        refused.Error!.Code.ShouldBe("privacy.breach_report_incomplete");
        refused.Error.Message.ShouldContain("the likely consequences");
        breach.MissingForReport().ShouldBe(["the likely consequences", "what is being done to reduce the risk", "the cause", "what stops it happening again"]);
    }

    [Fact]
    public void The_report_follows_the_first_intimation() =>
        Recorded(complete: true).RecordReportSubmitted(Aware.AddHours(5), null, Aware.AddHours(6)).Error!.Code.ShouldBe("privacy.breach_not_informed");

    [Fact]
    public void A_breach_that_was_never_reported_closes_only_with_a_reason()
    {
        var breach = Recorded();

        breach.Close(null, Aware.AddDays(1)).Error!.Fields!.ShouldContainKey("note");
        breach.Close("No personal data was on the laptop after all.", Aware.AddDays(1)).IsSuccess.ShouldBeTrue();
        breach.Close("Again", Aware.AddDays(2)).Error!.Code.ShouldBe("privacy.breach_closed");
    }

    [Fact]
    public void A_reported_breach_closes_without_a_note()
    {
        var breach = Recorded(complete: true);
        var now = Aware.AddHours(30);
        breach.RecordBoardInformed(Aware.AddHours(1), null, now);
        breach.RecordReportSubmitted(Aware.AddHours(20), null, now);

        breach.Close(null, now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Notices_carry_what_rule_7_1_asks_for()
    {
        Recorded().MissingForNotice().ShouldBe(
            ["the likely consequences", "what is being done to reduce the risk", "what people can do to protect themselves", "the short notice for SMS"]);
        Recorded(complete: true).MissingForNotice().ShouldBeEmpty();
    }

    [Fact]
    public void The_notice_describes_when_and_what_in_words()
    {
        var breach = Recorded(complete: true);
        breach.OccurredFrom = new DateTimeOffset(2026, 9, 27, 13, 30, 0, TimeSpan.Zero);
        breach.DataCategories = [nameof(BreachDataCategory.Health), nameof(BreachDataCategory.Contact)];

        var values = BreachService.NoticeValues(breach, "Grievance Officer, privacy@hospital.in", "https://care.example.in/portal/my-privacy/notices/1");

        values["when"].ShouldBe("27 Sep 2026 19:00 IST");
        values["data"].ShouldBe("health information, contact details");
        values["summary"].ShouldBe(breach.PrincipalNotice);
        values["contact"].ShouldBe("Grievance Officer, privacy@hospital.in");
    }
}
