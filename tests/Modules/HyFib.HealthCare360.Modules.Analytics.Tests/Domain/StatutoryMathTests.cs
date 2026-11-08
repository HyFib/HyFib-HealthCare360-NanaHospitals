using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;
using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain;

/// <summary>The arithmetic and row building of the GST, TDS, payroll cost and claims reports, with worked examples.</summary>
public sealed class StatutoryMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    [InlineData(90, 2)]
    [InlineData(91, 3)]
    [InlineData(180, 3)]
    [InlineData(181, 4)]
    [InlineData(2000, 4)]
    public void Ages_fall_in_the_receivables_buckets(int days, int bucket) => StatutoryMath.AgeBucket(days).ShouldBe(bucket);

    [Fact]
    public void An_open_claim_waits_for_what_is_neither_settled_nor_deducted()
    {
        StatutoryMath.ClaimOutstanding(100_000m, 60_000m, 5_000m).ShouldBe(35_000m);
        StatutoryMath.ClaimOutstanding(100_000m, 95_000m, 10_000m).ShouldBe(0m, "never below nothing");
    }

    [Fact]
    public void The_settlement_ratio_counts_decided_claims_by_number_and_by_amount()
    {
        // 45 settled (₹9,00,000 claimed, ₹8,10,000 paid) and 5 rejected (₹1,00,000 claimed); 12 still open are left out.
        var outcomes = new ClaimOutcomes(45, 5, 12, 900_000m, 810_000m, 100_000m);

        StatutoryMath.SettlementRatioByCount(outcomes).ShouldBe(90.0m);
        StatutoryMath.SettlementRatioByAmount(outcomes).ShouldBe(81.0m);
        StatutoryMath.SettlementRatioByCount(new ClaimOutcomes(0, 0, 3, 0m, 0m, 0m)).ShouldBeNull("none decided yet");
        StatutoryMath.SettlementRatioByCount(new ClaimOutcomes(1, 2, 0, 1m, 1m, 2m)).ShouldBe(33.3m);
    }

    [Fact]
    public void Tds_rates_employer_cost_and_return_shares()
    {
        StatutoryMath.EffectiveRate(10_000m, 100_000m).ShouldBe(10.00m);
        StatutoryMath.EffectiveRate(1_000m, 45_000m).ShouldBe(2.22m);
        StatutoryMath.EffectiveRate(0m, 0m).ShouldBeNull();
        StatutoryMath.EmployerCost(30_000m, 1_800m, 0m).ShouldBe(31_800m);
        StatutoryMath.EmployerCost(15_000m, 1_800m, 487.50m).ShouldBe(17_287.50m);
        StatutoryMath.ReturnShare(112m, 200m, 224m).ShouldBe(100m);
        StatutoryMath.ReturnShare(112m, 12m, 224m).ShouldBe(6m);
        StatutoryMath.ReturnShare(50m, 10m, 0m).ShouldBe(0m);
        StatutoryMath.MonthNumber(2026, 4).ShouldBe((2026 * 12) + 3);
    }

    [Fact]
    public void The_gst_summary_lists_invoices_and_negates_credit_notes_and_returns_so_columns_add_to_the_net()
    {
        GstSection[] sections =
        [
            new("Hospital services", "Invoices (bills)", 1,
            [
                new(18m, "999312", 3, 2_000m, 180m, 180m, 0m, 2_360m),
                new(0m, "999311", 5, 1_000m, 0m, 0m, 0m, 1_000m),
                new(18m, " 999312 ", 1, 500m, 45m, 45m, 0m, 590m),
                new(5m, null, 1, 100m, 0m, 0m, 5m, 105m),
            ]),
            new("Hospital services", "Credit notes", -1, [new(18m, "999312", 1, 500m, 45m, 45m, 0m, 590m)]),
        ];

        var rows = GstSummary.Rows(sections, byCode: true);

        rows.Select(r => (string)r.Cells[1]! + "|" + r.Cells[2] + "|" + r.Cells[3]).ShouldBe(
            ["Invoices (bills)|0|999311", "Invoices (bills)|5|Not recorded", "Invoices (bills)|18|999312", "Credit notes|18|999312"]);
        rows[2].Cells[4..].ShouldBe(new object?[] { 4, 2_500m, 225m, 225m, 0m, 450m, 2_950m }, "the same code written with spaces is one code");
        rows[3].Cells[5..].ShouldBe(new object?[] { -500m, -45m, -45m, 0m, -90m, -590m });
        rows[1].Cells[9].ShouldBe(5m, "IGST counts in the total GST");

        var byRate = GstSummary.Rows(sections, byCode: false);
        byRate.Count.ShouldBe(4);
        byRate.ShouldAllBe(r => (string)r.Cells[3]! == "All");
    }

    [Fact]
    public void A_gst_reconciliation_allows_a_paisa_of_rounding_and_no_more()
    {
        var lines = new GstAmounts(3_000m, 180m, 180m, 0m);
        new GstReconciliation("Hospital bills", lines, new GstAmounts(3_000m, 180m, 180m, 0m)).Reconciles.ShouldBeTrue();
        new GstReconciliation("Hospital bills", lines, new GstAmounts(3_000.01m, 180m, 180.01m, 0m)).Reconciles.ShouldBeTrue();
        var off = new GstReconciliation("Hospital bills", lines, new GstAmounts(2_900m, 170m, 170m, 0m));
        off.Reconciles.ShouldBeFalse();
        off.TaxableDifference.ShouldBe(100m);
        off.GstDifference.ShouldBe(20m);
        lines.Plus(lines.Negate()).ShouldBe(GstAmounts.Zero);
    }

    [Fact]
    public void Tds_rows_are_by_section_or_by_deductee_with_192_before_194J()
    {
        var doctor = Guid.NewGuid();
        var nurse = Guid.NewGuid();
        var clerk = Guid.NewGuid();
        TdsLine[] lines =
        [
            new("194J", "Fees for professional services (doctors)", doctor, "Dr Rao", 2, 200_000m, 20_000m),
            new("192", "Salaries", nurse, "Meena", 1, 40_000m, 1_000m),
            new("192", "Salaries", clerk, "Arun", 1, 20_000m, 0m),
        ];

        var bySection = TdsSummary.Rows(lines, byDeductee: false);
        bySection.Select(r => r[0]).ShouldBe(["192", "194J"]);
        bySection[0].ShouldBe(new object?[] { "192", "Salaries", null, 2, 2, 60_000m, 1_000m, 1.67m });
        bySection[1].ShouldBe(new object?[] { "194J", "Fees for professional services (doctors)", null, 1, 2, 200_000m, 20_000m, 10.00m });

        var byDeductee = TdsSummary.Rows(lines, byDeductee: true);
        byDeductee.Select(r => r[2]).ShouldBe(["Arun", "Meena", "Dr Rao"]);
        byDeductee[2][7].ShouldBe(10.00m);
    }

    [Fact]
    public void Payroll_cost_by_component_adds_the_employers_contributions_as_rows_of_their_own()
    {
        var groups = PayrollCost.ByComponent([("Basic (BASIC)", 2, 35_000m), ("House rent allowance (HRA)", 1, 10_000m)], 2, 3_600m, 1, 487.50m);

        groups.Select(g => g.Label).ShouldBe(["Basic (BASIC)", "House rent allowance (HRA)", "Employer PF contribution", "Employer ESI contribution"]);
        groups.Sum(g => g.Cost).ShouldBe(49_087.50m);
        groups[3].Employees.ShouldBe(1);
        PayrollCost.ByComponent([("Basic (BASIC)", 1, 10_000m)], 0, 0m, 0, 0m).Count.ShouldBe(1, "no employer rows when none was paid");
        PayrollCost.CostCentre(DepartmentKind.Clinical).ShouldBe("Clinical (patient care)");
        PayrollCost.CostCentre(null).ShouldBe("Not assigned");
    }

    [Fact]
    public void Claim_reasons_group_regardless_of_capitals_and_spaces_in_the_spelling_used_most()
    {
        var star = Guid.NewGuid();
        var care = Guid.NewGuid();
        ReasonTotal[] totals =
        [
            new(star, ClaimReasons.Deduction, "Non-medical items", 3, 9_000m),
            new(care, ClaimReasons.Deduction, "  non-medical   ITEMS", 1, 1_000m),
            new(star, ClaimReasons.Deduction, "Room rent capping", 1, 4_000m),
            new(care, ClaimReasons.Rejection, null, 1, 50_000m),
        ];
        string Name(Guid id) => id == star ? "Star" : "Care";

        var all = ClaimReasons.Group(totals, byInsurer: false, Name);
        all.ShouldBe(
        [
            new ReasonLine(ClaimReasons.AllInsurers, ClaimReasons.Rejection, ClaimReasons.NoReason, 1, 50_000m),
            new ReasonLine(ClaimReasons.AllInsurers, ClaimReasons.Deduction, "Non-medical items", 4, 10_000m),
            new ReasonLine(ClaimReasons.AllInsurers, ClaimReasons.Deduction, "Room rent capping", 1, 4_000m),
        ]);

        var byInsurer = ClaimReasons.Group(totals, byInsurer: true, Name);
        byInsurer.Count.ShouldBe(4);
        byInsurer.ShouldContain(new ReasonLine("Care", ClaimReasons.Deduction, "non-medical ITEMS", 1, 1_000m));
    }

    [Fact]
    public void System_health_names_failed_reports_and_unsent_emails_and_says_nothing_when_all_went()
    {
        ReportEmailHealthWarnings.Describe([], []).ShouldBeNull();
        ReportEmailHealthWarnings.Describe(["Revenue by department"], [("Revenue by department", 3, "SMTP refused the sign-in")]).ShouldBe(
            "Scheduled report e-mails in the last 24 hours: 1 report(s) could not be generated (Revenue by department); " +
            "1 e-mail(s) are not sent yet after 3 attempt(s) (last error: SMTP refused the sign-in). " +
            "Failed sends are retried; check the e-mail settings and Analytics › Scheduled e-mails › Delivery history.");
        ReportEmailHealthWarnings.Describe(["A", "B", "C", "D", "A"], []).ShouldNotBeNull().ShouldContain("5 report(s) could not be generated (A, B, C, …)");
    }
}
