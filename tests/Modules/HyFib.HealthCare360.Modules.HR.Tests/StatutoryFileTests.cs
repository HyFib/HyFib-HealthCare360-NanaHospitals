using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;

namespace HyFib.HealthCare360.Modules.HR.Tests;

/// <summary>ECR 2.0 examples as EPFO's ECR help sets them out: ₹15,000 of EPF wages is ₹1,800 from each side, ₹1,250 to EPS and ₹550 to EPF.</summary>
public sealed class EcrFileTests
{
    private static EcrMemberMonth Member(decimal gross = 20_000, decimal epf = 15_000, decimal ee = 1_800, decimal er = 1_800, DateOnly? dob = null, decimal lop = 2,
        string? uan = "100000000001") =>
        new(uan, "NHEMP00001", "Kavya R. Rao", dob ?? new DateOnly(1990, 5, 10), gross, epf, ee, er, lop);

    [Fact]
    public void A_member_at_the_ceiling_splits_the_employer_share_into_eps_and_epf()
    {
        var ecr = EcrFile.Build([Member()], 2026, 6);
        ecr.Text.ShouldBe("100000000001#~#KAVYA R. RAO#~#20000#~#15000#~#15000#~#15000#~#1800#~#1250#~#550#~#2#~#0\n");
        (ecr.TotalEpf, ecr.TotalEps, ecr.TotalDifference).ShouldBe((1800L, 1250L, 550L));
    }

    [Fact]
    public void Eps_stays_on_fifteen_thousand_when_pf_is_on_actual_wages()
    {
        var line = EcrFile.Build([Member(30_000, 25_000, 3_000, 3_000)], 2026, 6).Lines.Single();
        (line.Epf, line.Eps, line.Edli, line.EpsContribution, line.Difference).ShouldBe((25_000L, 15_000L, 15_000L, 1_250L, 1_750L));
    }

    [Fact]
    public void A_member_over_fifty_eight_pays_no_eps()
    {
        var line = EcrFile.Build([Member(dob: new DateOnly(1968, 1, 15))], 2026, 6).Lines.Single();
        (line.Eps, line.EpsContribution, line.Difference).ShouldBe((0L, 0L, 1_800L));
    }

    [Fact]
    public void Below_the_ceiling_eps_is_eight_point_three_three_percent()
    {
        var line = EcrFile.Build([Member(12_000, 10_000, 1_200, 1_200)], 2026, 6).Lines.Single();
        (line.EpsContribution, line.Difference).ShouldBe((833L, 367L));
    }

    [Fact]
    public void A_member_without_a_uan_is_a_problem_not_a_line()
    {
        var ecr = EcrFile.Build([Member(uan: null), Member(uan: "12345")], 2026, 6);
        ecr.Lines.ShouldBeEmpty();
        ecr.Problems.Count.ShouldBe(2);
    }

    [Fact]
    public void Every_line_has_the_eleven_fields_of_the_layout() =>
        EcrFile.Build([Member(), Member(lop: 0.5m)], 2026, 6).Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ShouldAllBe(l => l.Split("#~#", StringSplitOptions.None).Length == StatutoryLayouts.Ecr.Fields.Count);
}

public sealed class EsicContributionFileTests
{
    [Fact]
    public void Insured_persons_with_days_wages_and_reasons()
    {
        var esic = EsicContributionFile.Build(
        [
            new EsicMemberMonth("1234567890", "NHEMP00001", "Arun Kumar", 30, 18_000, 135, 585, null, null),
            new EsicMemberMonth("1234567891", "NHEMP00002", "Meena S", 15, 9_000, 68, 293, new DateOnly(2026, 6, 15), SeparationReason.Resignation),
            new EsicMemberMonth("1234567892", "NHEMP00003", "Ravi", 0, 0, 0, 0, null, null),
            new EsicMemberMonth(null, "NHEMP00004", "No Number", 30, 15_000, 113, 488, null, null),
        ], 2026, 6);

        esic.Problems.ShouldHaveSingleItem().ShouldContain("NHEMP00004");
        esic.Csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ShouldBe(
        [
            "IP Number,IP Name,No of Days for which wages paid/payable during the month,Total Monthly Wages,Reason Code for Zero workings days,Last Working Day",
            "1234567890,ARUN KUMAR,30,18000,0,",
            "1234567891,MEENA S,15,9000,2,15/06/2026",
            "1234567892,RAVI,0,0,1,",
        ]);
        (esic.TotalWages, esic.TotalEmployee, esic.TotalEmployer).ShouldBe((27_000L, 203m, 878m));
    }

    [Theory]
    [InlineData(SeparationReason.Retirement, EsicReason.Retired)]
    [InlineData(SeparationReason.Death, EsicReason.Expired)]
    [InlineData(SeparationReason.Resignation, EsicReason.LeftService)]
    public void Leaving_reasons_map_to_esic_codes(SeparationReason reason, EsicReason expected) =>
        EsicContributionFile.ReasonForLeaving(reason).ShouldBe(expected);

    [Theory]
    [InlineData(2026, 4, 2026, 4)]
    [InlineData(2026, 9, 2026, 4)]
    [InlineData(2026, 10, 2026, 10)]
    [InlineData(2027, 3, 2026, 10)]
    public void Contribution_periods_are_april_and_october(int year, int month, int startYear, int startMonth) =>
        EsiContributionPeriod.Start(year, month).ShouldBe((startYear, startMonth));

    [Fact]
    public void Coverage_lasts_to_the_end_of_the_contribution_period()
    {
        // A raise to ₹25,000 in the middle of a period: still covered, so ESI is 0.75% of ₹25,000, rounded up.
        var input = new PayrollCalculationInput(2026, 7, 31, 31,
            [new SalaryLineInput("BASIC", "Basic", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 25_000, 1)], [],
            new StatutoryProfile(false, false, true, null, EsiCoveredInPeriod: true), 0);
        PayrollCalculator.Calculate(input).EmployeeEsi.ShouldBe(188m);
        PayrollCalculator.Calculate(input with { Statutory = input.Statutory with { EsiCoveredInPeriod = false } }).EmployeeEsi.ShouldBe(0m);
    }
}

public sealed class SeparationBenefitTests
{
    [Fact]
    public void Gratuity_is_fifteen_days_wages_for_each_year()
    {
        // Ten years on ₹30,000 basic, leaving before the Code: 30,000 × 15/26 × 10.
        var g = Gratuity.Compute(new DateOnly(2015, 4, 1), new DateOnly(2025, 3, 31), 30_000, SeparationReason.Resignation, false);
        (g.Eligible, g.CompletedYears, g.QualifyingYears, g.Amount).ShouldBe((true, 10, 10, 173_076.92m));
        g.Law.ShouldContain("Payment of Gratuity Act");
    }

    [Fact]
    public void More_than_six_months_counts_as_a_year()
    {
        var g = Gratuity.Compute(new DateOnly(2019, 1, 1), new DateOnly(2024, 7, 31), 26_000, SeparationReason.Resignation, false);
        (g.CompletedYears, g.QualifyingYears, g.Amount).ShouldBe((5, 6, 90_000m));
    }

    [Fact]
    public void Gratuity_needs_five_years_except_on_death()
    {
        Gratuity.Compute(new DateOnly(2021, 1, 1), new DateOnly(2025, 6, 30), 26_000, SeparationReason.Resignation, false).Eligible.ShouldBeFalse();
        var death = Gratuity.Compute(new DateOnly(2023, 1, 1), new DateOnly(2025, 9, 30), 26_000, SeparationReason.Death, false);
        (death.Eligible, death.QualifyingYears, death.Amount).ShouldBe((true, 3, 45_000m));
    }

    [Fact]
    public void Four_years_and_240_days_worked_is_five_years()
    {
        var g = Gratuity.Compute(new DateOnly(2020, 1, 1), new DateOnly(2024, 9, 30), 26_000, SeparationReason.Resignation, false, daysWorkedInFinalYear: 250);
        (g.Eligible, g.QualifyingYears).ShouldBe((true, 5));
        Gratuity.Compute(new DateOnly(2020, 1, 1), new DateOnly(2024, 9, 30), 26_000, SeparationReason.Resignation, false, daysWorkedInFinalYear: 200).Eligible
            .ShouldBeFalse();
    }

    [Fact]
    public void Under_the_code_a_fixed_term_ending_is_paid_pro_rata()
    {
        var g = Gratuity.Compute(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 31), 26_000, SeparationReason.EndOfContract, fixedTerm: true);
        (g.Eligible, g.QualifyingYears, g.Amount).ShouldBe((true, 1, 15_000m));
        g.Law.ShouldContain("Code on Social Security");
        Gratuity.Compute(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 31), 26_000, SeparationReason.EndOfContract, fixedTerm: true).Eligible.ShouldBeFalse();
    }

    [Fact]
    public void Gratuity_is_capped_at_twenty_lakh()
    {
        var g = Gratuity.Compute(new DateOnly(2005, 1, 1), new DateOnly(2025, 1, 31), 500_000, SeparationReason.Retirement, false);
        (g.Formula > Gratuity.Ceiling, g.Amount, g.Capped).ShouldBe((true, 2_000_000m, true));
    }

    [Theory]
    [InlineData(0, 6_997)] // each month counted up to ₹7,000: 84,000 × 8.33%
    [InlineData(12_000, 11_995)] // a minimum wage of ₹12,000 raises the cap: 1,44,000 × 8.33%
    public void Bonus_is_on_wages_up_to_the_calculation_ceiling(double minimumWage, double expected)
    {
        var months = Enumerable.Range(0, 12).Select(i => new BonusMonth(2025, ((i + 3) % 12) + 1, 15_000, 30)).ToList();
        var b = StatutoryBonus.Compute(months, 8.33m, (decimal)minimumWage);
        (b.Eligible, b.Amount).ShouldBe((true, (decimal)expected));
    }

    [Fact]
    public void Bonus_needs_thirty_days_on_wages_of_twenty_one_thousand_or_less()
    {
        StatutoryBonus.Compute([new BonusMonth(2025, 4, 15_000, 20)], 8.33m, 0).Eligible.ShouldBeFalse();
        StatutoryBonus.Compute([new BonusMonth(2025, 4, 22_000, 30), new BonusMonth(2025, 5, 22_000, 31)], 8.33m, 0).Eligible.ShouldBeFalse();
        StatutoryBonus.Compute([new BonusMonth(2025, 4, 15_000, 30), new BonusMonth(2025, 5, 15_000, 31)], 30m, 0).Rate.ShouldBe(20m);
        StatutoryBonus.Compute([new BonusMonth(2025, 4, 15_000, 30), new BonusMonth(2025, 5, 15_000, 31)], 8.33m, 0, disqualified: true).Amount.ShouldBe(0m);
    }

    [Fact]
    public void Leave_encashment_at_a_month_over_thirty_or_twenty_six()
    {
        LeaveEncashment.Compute(30, 30_000).Amount.ShouldBe(30_000m);
        LeaveEncashment.Compute(30, 30_000, 26).Amount.ShouldBe(34_615.50m);
        var big = LeaveEncashment.Compute(300, 300_000, 30, averageMonthlySalary: 300_000);
        (big.Amount, big.Exempt, big.Taxable).ShouldBe((3_000_000m, 2_500_000m, 500_000m));
    }

    [Fact]
    public void Full_and_final_adds_dues_and_recovers_notice_and_advances()
    {
        var gratuity = Gratuity.Compute(new DateOnly(2018, 1, 1), new DateOnly(2025, 12, 31), 26_000, SeparationReason.Resignation, false);
        var statement = FullAndFinalSettlement.Compute(new FullAndFinalInput(
            new DateOnly(2025, 12, 31), 60, 30, false, false, 60_000, 20_000, LeaveEncashment.Compute(10, 30_000), gratuity, 5_000,
            [new SettlementRecovery("ADVANCE", "Salary advance", 15_000)]));

        statement.Earnings.Select(e => (e.Code, e.Amount)).ShouldBe([("SALARY", 20_000m), ("LEAVE_ENC", 10_000m), ("GRATUITY", 120_000m), ("BONUS", 5_000m)]);
        statement.Recoveries.Select(r => (r.Code, r.Amount)).ShouldBe([("NOTICE_SHORT", 60_000m), ("ADVANCE", 15_000m)]);
        (statement.NetPayable, statement.NoticeShortfallDays).ShouldBe((80_000m, 30));
    }

    [Fact]
    public void Notice_waived_is_not_recovered_and_notice_withheld_by_the_employer_is_paid()
    {
        var none = Gratuity.Compute(new DateOnly(2024, 1, 1), new DateOnly(2025, 6, 30), 20_000, SeparationReason.Resignation, false);
        var waived = FullAndFinalSettlement.Compute(new(new DateOnly(2025, 6, 30), 30, 0, true, false, 30_000, 0, LeaveEncashment.Compute(0, 20_000), none, 0, []));
        waived.Recoveries.ShouldBeEmpty();
        var withheld = FullAndFinalSettlement.Compute(new(new DateOnly(2025, 6, 30), 30, 0, false, true, 30_000, 0, LeaveEncashment.Compute(0, 20_000), none, 0, []));
        withheld.Earnings.ShouldHaveSingleItem().Amount.ShouldBe(30_000m);
    }
}

public sealed class BankUploadFileTests
{
    private static readonly BankTransferRow[] Rows =
    [
        new("NHEMP00001", "Kavya Rao", "1234567890", "HDFC0001234", 25_000.5m, "SALARY JUN 2026"),
        new("NHEMP00002", "=Ravi, K", "50100012345678", "SBIN0004321", 18_000m, "SALARY JUN 2026"),
    ];

    [Fact]
    public void Hdfc_enet_pays_hdfc_accounts_internally_and_others_by_neft()
    {
        var lines = BankUploadFiles.Build(BankFileFormat.Hdfc, Rows, null, new DateOnly(2026, 6, 30)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        lines.ShouldAllBe(l => l.Split(',').Length == StatutoryLayouts.HdfcEnet.Fields.Count);
        lines[0].Split(',')[0].ShouldBe("I");
        lines[1].Split(',')[0].ShouldBe("N");
        lines[1].Split(',')[4].ShouldBe("Ravi  K"); // no formula character, no separator
        lines[0].Split(',')[3].ShouldBe("25000.50");
        lines[0].Split(',')[22].ShouldBe("30/06/2026");
    }

    [Fact]
    public void Sbi_and_icici_name_the_debit_account()
    {
        var sbi = BankUploadFiles.Build(BankFileFormat.Sbi, Rows, "00000012345678901", new DateOnly(2026, 6, 30)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        sbi[0].ShouldBe(string.Join('|', StatutoryLayouts.SbiCmp.Fields));
        sbi[1].ShouldBe("Kavya Rao|1234567890|HDFC0001234|NEFT|25000.50|00000012345678901|SALARY JUN 2026|30/06/2026");
        sbi[2].Split('|')[3].ShouldBe("DCR");

        var icici = BankUploadFiles.Build(BankFileFormat.Icici, Rows, "000401000123", new DateOnly(2026, 6, 30)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        icici[1].Split(',')[..7].ShouldBe(["000401000123", "1234567890", "Kavya Rao", "25000.50", "N", "30-Jun-2026", "HDFC0001234"]);
    }

    [Fact]
    public void Csv_stays_the_generic_file() =>
        BankUploadFiles.Build(BankFileFormat.Csv, Rows, null, new DateOnly(2026, 6, 30)).ShouldStartWith(BankTransferFile.Header);
}

public sealed class TdsReturnFileTests
{
    private static readonly TdsDeductor Deductor = new(
        "CHEN12345E", "AABCN1234F", "Nana Hospitals Pvt Ltd", null, ["12 Mount Road", "Chennai"], "22", "600002", "accounts@example.org", "04412345678", "K",
        "S Raman", "Finance Manager", "ABCPR1234K", "9876543210", "accounts@example.org");

    private static TdsChallanDeposit Challan(params TdsDeduction[] rows) =>
        new("0510308", new DateOnly(2026, 5, 7), "12345", rows.Sum(r => r.Tds), 0, 0, 0, 0, 0, "200", rows);

    [Fact]
    public void A_quarter_is_a_header_a_batch_and_each_challan_with_its_deductees()
    {
        var result = TdsReturnFile.Build("24Q", Deductor, 2026, 1,
        [
            Challan(
                new TdsDeduction("Kavya Rao", "ABCPK1234L", 150_000, new DateOnly(2026, 4, 30), new DateOnly(2026, 4, 30), 12_567, null, "92B", null),
                new TdsDeduction("Ravi", null, 90_000, new DateOnly(2026, 4, 30), new DateOnly(2026, 4, 30), 4_000, null, "92B", "C")),
        ], new DateOnly(2026, 7, 10));

        result.Problems.ShouldBeEmpty();
        var lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Select(l => l.Split('^')[1]).ShouldBe(["FH", "BH", "CD", "DD", "DD"]);
        lines.Select(l => l.Split('^')[0]).ShouldBe(["1", "2", "3", "4", "5"]);
        lines[0].Split('^').Length.ShouldBe(StatutoryLayouts.TdsFileHeader.Fields.Count);
        lines[1].Split('^').Length.ShouldBe(StatutoryLayouts.TdsBatchHeader.Fields.Count);
        lines[2].Split('^').Length.ShouldBe(StatutoryLayouts.TdsChallanDetail.Fields.Count);
        lines[3].Split('^').Length.ShouldBe(StatutoryLayouts.TdsDeducteeDetail.Fields.Count);

        var batch = lines[1].Split('^');
        (batch[StatutoryLayouts.TdsBatchHeader.IndexOf("Form Number")], batch[StatutoryLayouts.TdsBatchHeader.IndexOf("Financial Year")],
            batch[StatutoryLayouts.TdsBatchHeader.IndexOf("Assessment Year")], batch[StatutoryLayouts.TdsBatchHeader.IndexOf("Period")])
            .ShouldBe(("24Q", "202627", "202728", "Q1"));
        var challan = lines[2].Split('^');
        challan[StatutoryLayouts.TdsChallanDetail.IndexOf("Total Deposit Amount")].ShouldBe("16567.00");
        challan[StatutoryLayouts.TdsChallanDetail.IndexOf("Challan Date")].ShouldBe("07052026");
        var noPan = lines[4].Split('^');
        noPan[StatutoryLayouts.TdsDeducteeDetail.IndexOf("PAN")].ShouldBe(TdsReturnFile.PanNotAvailable);
        noPan[StatutoryLayouts.TdsDeducteeDetail.IndexOf("Remarks")].ShouldBe("C");
        (result.TotalDeducted, result.TotalDeposited, result.Deductees).ShouldBe((16_567m, 16_567m, 2));
    }

    [Fact]
    public void A_challan_paying_less_than_is_allocated_and_a_bad_tan_are_problems()
    {
        var short1 = Challan(new TdsDeduction("Kavya Rao", "ABCPK1234L", 150_000, new DateOnly(2026, 4, 30), new DateOnly(2026, 4, 30), 12_567, null, "92B", null))
            with
        { Tax = 10_000 };
        var result = TdsReturnFile.Build("24Q", Deductor with { Tan = "CHEN1234" }, 2026, 1, [short1], new DateOnly(2026, 7, 10));
        result.Problems.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(1, "2026-04-01", "2026-06-30")]
    [InlineData(4, "2027-01-01", "2027-03-31")]
    public void Quarters_of_the_financial_year(int quarter, string from, string to) =>
        TdsReturnFile.QuarterDates(2026, quarter).ShouldBe((DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Text_is_upper_case_without_the_separator() => TdsReturnFile.Text("a^b\nc", 10).ShouldBe("A B C");
}

/// <summary>s.194J: the year's threshold (₹30,000 to FY 2024-25, ₹50,000 from FY 2025-26), catch-up once crossed, 20% without a PAN.</summary>
public sealed class ProfessionalFeeTdsTests
{
    private static readonly ProfessionalFeeRates Fy2025 = ProfessionalFeeTds.Find(2025)!;
    private const string Pan = "ABCPD1234E";

    [Fact]
    public void Nothing_within_the_threshold()
    {
        var r = ProfessionalFeeTds.Compute(Fy2025, 30_000, 0, 15_000, Pan);
        (r.Tds, r.ThresholdCrossed).ShouldBe((0m, false));
    }

    [Fact]
    public void Crossing_the_threshold_taxes_the_whole_year_so_far()
    {
        var crossing = ProfessionalFeeTds.Compute(Fy2025, 40_000, 0, 20_000, Pan);
        (crossing.Basis, crossing.Tds, crossing.RatePercent).ShouldBe((60_000m, 6_000m, 10m));

        var after = ProfessionalFeeTds.Compute(Fy2025, 60_000, 60_000, 10_000, Pan);
        (after.Basis, after.Tds).ShouldBe((10_000m, 1_000m));
    }

    [Fact]
    public void The_threshold_is_the_years()
    {
        var r = ProfessionalFeeTds.Compute(ProfessionalFeeTds.Find(2024)!, 25_000, 0, 10_000, Pan);
        (r.Basis, r.Tds).ShouldBe((35_000m, 3_500m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ABCD1234E")]
    [InlineData("1234567890")]
    public void Without_a_valid_pan_it_is_twenty_percent(string? pan)
    {
        var r = ProfessionalFeeTds.Compute(Fy2025, 60_000, 60_000, 10_000, pan);
        (r.RatePercent, r.Tds, r.Remark, r.WithoutPan).ShouldBe((20m, 2_000m, "C", true));
    }

    [Fact]
    public void A_lower_deduction_certificate_lowers_the_rate_only_with_a_pan()
    {
        var r = ProfessionalFeeTds.Compute(Fy2025, 60_000, 60_000, 10_000, Pan, 2m);
        (r.RatePercent, r.Tds, r.Remark).ShouldBe((2m, 200m, "A"));
        ProfessionalFeeTds.Compute(Fy2025, 60_000, 60_000, 10_000, null, 2m).RatePercent.ShouldBe(20m);
        ProfessionalFeeTds.Compute(Fy2025, 60_000, 60_000, 10_000, Pan, 15m).RatePercent.ShouldBe(10m);
    }

    [Theory]
    [InlineData("CHEN12345E", true)]
    [InlineData("CHEN1234E", false)]
    [InlineData("ABCPD1234E", false)]
    public void Tan_format(string tan, bool valid) => HrIdentifiers.IsValidTan(tan).ShouldBe(valid);
}
