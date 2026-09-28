using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class PayrollRunTests
{
    private static readonly Guid Processor = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);

    private static PayrollRun Processed()
    {
        var run = new PayrollRun { Year = 2026, Month = 9 };
        run.MarkProcessed(Processor, Now, 120, 3_000_000, 400_000, 2_600_000).IsSuccess.ShouldBeTrue();
        return run;
    }

    [Fact]
    public void Processing_records_totals_and_the_processor()
    {
        var run = Processed();

        run.Status.ShouldBe(PayrollRunStatus.Processed);
        run.ProcessedByUserId.ShouldBe(Processor);
        run.EmployeeCount.ShouldBe(120);
        run.TotalNet.ShouldBe(2_600_000m);
        run.IsEditable.ShouldBeTrue();
    }

    [Fact]
    public void The_processor_cannot_approve_their_own_payroll()
    {
        var result = Processed().Approve(Processor, Now);

        result.Error!.Code.ShouldBe("payroll.maker_checker");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public void A_second_person_approves()
    {
        var run = Processed();

        run.Approve(Approver, Now).IsSuccess.ShouldBeTrue();

        run.Status.ShouldBe(PayrollRunStatus.Approved);
        run.ApprovedByUserId.ShouldBe(Approver);
        run.IsEditable.ShouldBeFalse();
        run.MarkProcessed(Processor, Now, 1, 1, 1, 1).Error!.Code.ShouldBe("payroll.not_editable");
    }

    [Fact]
    public void Only_processed_payroll_can_be_approved()
    {
        new PayrollRun { Year = 2026, Month = 9 }.Approve(Approver, Now).Error!.Code.ShouldBe("payroll.not_processed");
    }

    [Fact]
    public void Reopening_sends_an_approved_payroll_back_for_corrections()
    {
        var run = Processed();
        run.Approve(Approver, Now);

        run.Reopen("TDS for three staff was wrong").IsSuccess.ShouldBeTrue();

        run.Status.ShouldBe(PayrollRunStatus.Processed);
        run.ApprovedByUserId.ShouldBeNull();
        run.ReopenReason.ShouldBe("TDS for three staff was wrong");
    }

    [Fact]
    public void Paid_requires_approval_and_a_date_in_or_after_the_month()
    {
        var run = Processed();
        run.MarkPaid(new DateOnly(2026, 10, 1), "UTR123").Error!.Code.ShouldBe("payroll.not_approved");

        run.Approve(Approver, Now);
        run.MarkPaid(new DateOnly(2026, 8, 31), "UTR123").IsFailure.ShouldBeTrue();
        run.MarkPaid(new DateOnly(2026, 10, 1), " UTR123 ").IsSuccess.ShouldBeTrue();

        run.Status.ShouldBe(PayrollRunStatus.Paid);
        run.PaymentReference.ShouldBe("UTR123");
        run.Reopen("too late").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Only_paid_payroll_can_be_locked()
    {
        var run = Processed();
        run.Lock(Now).IsFailure.ShouldBeTrue();

        run.Approve(Approver, Now);
        run.MarkPaid(new DateOnly(2026, 10, 1), "UTR1");
        run.Lock(Now).IsSuccess.ShouldBeTrue();

        run.Status.ShouldBe(PayrollRunStatus.Locked);
        run.LockedAt.ShouldBe(Now);
    }

    [Fact]
    public void The_period_follows_the_calendar_month()
    {
        var run = new PayrollRun { Year = 2028, Month = 2 };

        run.PeriodStart.ShouldBe(new DateOnly(2028, 2, 1));
        run.PeriodEnd.ShouldBe(new DateOnly(2028, 2, 29));
    }
}
