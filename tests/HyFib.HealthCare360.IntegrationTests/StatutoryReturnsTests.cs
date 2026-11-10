using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Modules.HR.Features.Statutory;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A month's payroll, processed by the hospital administrator and approved by the HR manager, gives every statutory file
/// from its own payslips: TDS computed from the year's salary and the declaration (s.192), the EPFO ECR, the ESIC file, the
/// banks' upload files, Form 24Q once the month's challan is recorded, Form 16 Part B, and the bonus, gratuity and full and
/// final figures.
/// </summary>
public sealed class StatutoryReturnsTests(HospitalServer server)
{
    private const string Statutory = "/api/v1/hr/statutory";

    [Fact]
    public async Task An_approved_month_gives_the_ecr_esic_bank_files_24q_and_form_16_from_its_payslips()
    {
        var hr = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var manager = await server.SignInAsync(HospitalServer.Users.HrManager);
        var components = (await hr.GetAsync<List<SalaryComponentDto>>("/api/v1/hr/payroll/components")).ToDictionary(c => c.Code);
        await ProfileAsync(manager);

        // A consultant on ₹1,32,000 a month (₹80,000 basic, house rent 40 %, ₹20,000 special) who joined in November from an
        // employer that paid ₹12 lakh and deducted ₹60,000; and an attendant on ₹16,800, in ESI, with no PAN.
        var uanA = "1" + Digits(11);
        var uanB = "1" + Digits(11);
        var ipB = "3" + Digits(9);
        var consultant = await hr.PostAsync<EmployeeCreated>("/api/v1/hr/employees/", Employee("Lakshmi", "ABCPL" + Digits(4) + "K", uanA, null,
            new BankDetailsInput("HDFC Bank", "5010" + Digits(10), "HDFC0001234")));
        var attendant = await hr.PostAsync<EmployeeCreated>("/api/v1/hr/employees/", Employee("Murugan", null, uanB, ipB,
            new BankDetailsInput("State Bank of India", "3" + Digits(10), "SBIN0004321")));
        // Each first account and salary structure waits for a second person (maker-checker slice 11): the HR manager approves.
        await server.ApproveHrChangeAsync(consultant.BankApprovalId!.Value);
        await server.ApproveHrChangeAsync(attendant.BankApprovalId!.Value);
        await StructureAsync(hr, consultant.Id, components, 80_000, 20_000, esi: false);
        await StructureAsync(hr, attendant.Id, components, 12_000, 0, esi: true);
        await hr.PutAsync($"{Statutory}/employees/{consultant.Id}/tax-declarations/2025", new SaveTaxDeclarationRequest(
            TaxRegime.New, 0, false, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, PreviousEmployerSalary: 1_200_000, PreviousEmployerTds: 60_000, null, null));

        // November 2025: four months left in the year after it. Approving a month closes salary revisions dated in it or before
        // for the whole facility, so this test uses a month earlier than any other test's salary structures, and reopens it at
        // the end.
        var run = await RunAsync(hr, 2025, 11);
        (await hr.PostAsync<PayrollProcessResult>($"/api/v1/hr/payroll/runs/{run}/process")).PayslipsWithErrors.ShouldBe(0);
        var a = await PayslipAsync(hr, run, consultant);
        var b = await PayslipAsync(hr, run, attendant);

        // ₹6,60,000 here and ₹12 lakh before: ₹17,85,000 taxable, tax ₹1,57,000 + cess = ₹1,63,280. Less the ₹60,000
        // deducted, over November to March: ₹20,656 a month.
        a.Deductions.Single(d => d.Code == SalaryComponent.TdsCode).Amount.ShouldBe(20_656m);
        a.NetPay.ShouldBe(132_000m - 1_800m - 20_656m);
        b.Deductions.ShouldNotContain(d => d.Code == SalaryComponent.TdsCode);
        (Deduction(b, SalaryComponent.PfCode), Deduction(b, SalaryComponent.EsiCode), b.EmployerEsi, b.NetPay).ShouldBe((1_440m, 126m, 546m, 15_234m));

        var computation = await hr.GetAsync<TaxComputationDto>($"{Statutory}/employees/{consultant.Id}/tax-computation/2025");
        (computation.ChosenRegime, computation.Chosen.TaxableIncome, computation.Chosen.TotalTax).ShouldBe((TaxRegime.New, 1_785_000m, 163_280m));
        computation.Other.Regime.ShouldBe(TaxRegime.Old);

        // Nothing leaves before the month is approved, by someone other than who processed it.
        using (var early = await manager.SendRawAsync(HttpMethod.Get, $"{Statutory}/runs/{run}/ecr.txt"))
        {
            early.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        await manager.PostAsync($"/api/v1/hr/payroll/runs/{run}/approve");

        // ECR: the attendant's ₹12,000 of PF wages is ₹1,440 from each side, ₹1,000 of it to EPS and ₹440 to EPF.
        var ecr = await TextAsync(manager, $"{Statutory}/runs/{run}/ecr.txt");
        ecr.ShouldContain($"{uanB}#~#MURUGAN {Surname(b)}#~#16800#~#12000#~#12000#~#12000#~#1440#~#1000#~#440#~#0#~#0\n");
        ecr.ShouldContain($"{uanA}#~#LAKSHMI {Surname(a)}#~#132000#~#15000#~#15000#~#15000#~#1800#~#1250#~#550#~#0#~#0\n");

        var esic = await TextAsync(manager, $"{Statutory}/runs/{run}/esic.csv");
        esic.ShouldContain($"{ipB},MURUGAN {Surname(b)},30,16800,0,\r\n");
        esic.ShouldNotContain(uanA);

        var hdfc = (await TextAsync(manager, $"{Statutory}/runs/{run}/bank-file/Hdfc")).Split("\r\n");
        var hdfcLine = hdfc.Single(l => l.Contains(a.EmployeeCode, StringComparison.Ordinal)).Split(',');
        (hdfcLine[0], hdfcLine[3], hdfcLine[24]).ShouldBe(("I", "109544.00", "HDFC0001234"));

        // The bank's own format pays the account fixed on the payslip at approval, as the generic file does: an account
        // changed (and approved) afterwards does not reach a month already approved (maker-checker slice 11).
        // The new account is this test's own (other tests' employees, paid in the same month, may bank anywhere).
        var newAccount = "4444" + Digits(8);
        var version = (await hr.GetAsync<EmployeeDetail>($"/api/v1/hr/employees/{consultant.Id}")).Version;
        var moved = await TheatreCases.AcceptedAsync(hr, HttpMethod.Put, $"/api/v1/hr/employees/{consultant.Id}/bank-details",
            new UpdateBankDetailsRequest(SalaryPaymentMode.BankTransfer, "Indian Bank", newAccount, "IDIB000A123", false, "Changed banks", version));
        await server.ApproveHrChangeAsync(moved.Id);
        var after = await TextAsync(manager, $"{Statutory}/runs/{run}/bank-file/Hdfc");
        var consultantLine = after.Split("\r\n").Single(l => l.Contains(a.EmployeeCode, StringComparison.Ordinal)).Split(',');
        (consultantLine[2], consultantLine[24]).ShouldBe((hdfcLine[2], "HDFC0001234"));
        after.ShouldNotContain(newAccount);
        var sbi = (await TextAsync(manager, $"{Statutory}/runs/{run}/bank-file/Sbi")).Split("\r\n");
        sbi.Single(l => l.Contains("SBIN0004321", StringComparison.Ordinal) && l.Contains("15234.00", StringComparison.Ordinal)).Split('|')[3].ShouldBe("DCR");

        // Form 24Q for the quarter: refused until November's TDS has its challan, then the consultant's deduction under it.
        using (var refused = await manager.SendRawAsync(HttpMethod.Get, $"{Statutory}/24q/2025/3/file"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("No challan");
        }

        var monthTds = await RunTdsAsync(hr, run);
        await manager.PostAsync<JsonElement>($"{Statutory}/challans", new SaveTdsChallanRequest(
            TdsForm.Form24Q, 2025, 11, "0510308", new DateOnly(2025, 12, 7), Digits(5), monthTds, 0, 0, 0, 0, 0, null));
        var form24 = await TextAsync(manager, $"{Statutory}/24q/2025/3/file");
        var lines = form24.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Split('^')[1].ShouldBe("FH");
        lines[1].Split('^')[StatutoryLayouts.TdsBatchHeader.IndexOf("Form Number")].ShouldBe("24Q");
        var deductee = lines.Single(l => l.Split('^')[1] == "DD" && l.Contains(a.EmployeeName.ToUpperInvariant(), StringComparison.Ordinal)).Split('^');
        deductee[StatutoryLayouts.TdsDeducteeDetail.IndexOf("Total TDS")].ShouldBe("20656.00");
        deductee[StatutoryLayouts.TdsDeducteeDetail.IndexOf("Amount Paid")].ShouldBe("132000.00");
        deductee[StatutoryLayouts.TdsDeducteeDetail.IndexOf("Section Code")].ShouldBe("92B");
        deductee[StatutoryLayouts.TdsDeducteeDetail.IndexOf("PAN")].ShouldStartWith("ABCPL");

        // Form 16 Part B, on the salary paid here and the previous employer's: ₹12,57,000 taxable, tax ₹59,280.
        var form16 = await manager.GetAsync<PrintableDocumentDto>($"{Statutory}/employees/{consultant.Id}/form16/2025");
        form16.Html.ShouldContain("FORM No. 16");
        form16.Html.ShouldContain("12,57,000.00");
        form16.Html.ShouldContain("59,280.00");
        form16.Html.ShouldContain("CHEN12345E");

        // Bonus: the attendant's ₹12,000 counts up to ₹7,000 (8.33 %: ₹583); the consultant's wages are above ₹21,000.
        var bonus = await hr.GetAsync<BonusRegisterDto>($"{Statutory}/bonus/2025?ratePercent=8.33");
        bonus.Rows.Single(r => r.EmployeeId == attendant.Id).Result.Amount.ShouldBe(583m);
        bonus.Rows.Single(r => r.EmployeeId == consultant.Id).Result.Eligible.ShouldBeFalse();

        // Gratuity and full and final for the attendant leaving on 31 March 2026, short of notice by 15 days.
        var gratuity = await hr.GetAsync<GratuityDto>($"{Statutory}/employees/{attendant.Id}/gratuity?asOf=2026-03-31");
        gratuity.Result.Eligible.ShouldBeFalse();
        var settlement = await hr.PostAsync<FullAndFinalDto>($"{Statutory}/employees/{attendant.Id}/full-and-final", new FullAndFinalRequest(
            new DateOnly(2026, 3, 31), 30, 15, false, false, 0, 10, 30, 8.33m, 0, [new SettlementRecoveryInput("UNIFORM", "Uniform not returned", 500)]));
        settlement.MonthlyWages.ShouldBe(12_000m);
        settlement.LeaveEncashment.Amount.ShouldBe(4_000m);
        settlement.Statement.Recoveries.Select(r => (r.Code, r.Amount)).ShouldBe([("NOTICE_SHORT", 8_400m), ("UNIFORM", 500m)]);
        settlement.Statement.NetPayable.ShouldBe(4_000m + 583m - 8_400m - 500m);

        // Leave the month open again, as it was found, for any test that revises salaries later.
        await manager.PostAsync($"/api/v1/hr/payroll/runs/{run}/reopen", new ReopenPayrollRequest("Statutory returns test done; reopened for other tests."));
    }

    // ---------------------------------------------------------------------- steps

    /// <summary>The facility's TAN, PAN and the person responsible, which the returns need (shared by DoctorFeeTdsTests).</summary>
    internal static async Task ProfileAsync(ApiClient manager)
    {
        var current = await manager.GetAsync<StatutoryProfileDto>($"{Statutory}/profile");
        await manager.PutAsync($"{Statutory}/profile", new SaveStatutoryProfileRequest(
            "CHEN12345E", "AABCN1234F", "Nana Hospitals Pvt Ltd", null, "K", "12 Mount Road", null, null, "Chennai", "22", "600002", null, null,
            "S Raman", "Finance Manager", "ABCPR1234K", "9876543210", null, "TNMAS0012345000", null, "00000012345678", BankFileFormat.Sbi, current.Version));
    }

    private async Task StructureAsync(ApiClient hr, Guid employeeId, Dictionary<string, SalaryComponentDto> components, decimal basic, decimal special, bool esi)
    {
        List<SalaryStructureLineInput> lines = [new(components["BASIC"].Id, basic), new(components["HRA"].Id, 40)];
        if (special > 0)
        {
            lines.Add(new SalaryStructureLineInput(components["SPECIAL"].Id, special));
        }

        var asked = await hr.PostAsync<ApprovalRequestDto>($"/api/v1/hr/employees/{employeeId}/salary-structures", new SaveSalaryStructureRequest(
            new DateOnly(2025, 11, 1), null, 0, PfApplicable: true, PfOnActualBasic: false, EsiApplicable: esi, null, null, lines));
        await server.ApproveHrChangeAsync(asked.Id);
    }

    private static async Task<Guid> RunAsync(ApiClient hr, int year, int month)
    {
        using (var created = await hr.SendRawAsync(HttpMethod.Post, "/api/v1/hr/payroll/runs", new CreatePayrollRunRequest(year, month, null)))
        {
            if (created.StatusCode != HttpStatusCode.Conflict)
            {
                return await ApiClient.ReadAsync<Guid>(created, HttpMethod.Post, "/api/v1/hr/payroll/runs");
            }
        }

        return (await hr.GetAsync<PagedResult<PayrollRunDto>>($"/api/v1/hr/payroll/runs?year={year}&pageSize=12")).Items.Single(r => r.Month == month).Id;
    }

    private static async Task<PayslipDto> PayslipAsync(ApiClient hr, Guid run, EmployeeCreated employee)
    {
        var listed = (await hr.GetAsync<PagedResult<PayslipListItem>>($"/api/v1/hr/payroll/runs/{run}/payslips?search={employee.EmployeeCode}")).Items
            .Single(p => p.EmployeeId == employee.Id);
        return await hr.GetAsync<PayslipDto>($"/api/v1/hr/payroll/payslips/{listed.Id}");
    }

    /// <summary>All the TDS the month's payslips deducted, the challan's amount (other tests' employees are in the same month).</summary>
    private static async Task<decimal> RunTdsAsync(ApiClient hr, Guid run)
    {
        var total = 0m;
        foreach (var item in (await hr.GetAsync<PagedResult<PayslipListItem>>($"/api/v1/hr/payroll/runs/{run}/payslips?pageSize=200")).Items)
        {
            total += Deduction(await hr.GetAsync<PayslipDto>($"/api/v1/hr/payroll/payslips/{item.Id}"), SalaryComponent.TdsCode);
        }

        return total;
    }

    private static async Task<string> TextAsync(ApiClient client, string path)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return text;
    }

    private static decimal Deduction(PayslipDto payslip, string code) => payslip.Deductions.Where(d => d.Code == code).Sum(d => d.Amount);

    private static string Surname(PayslipDto payslip) => payslip.EmployeeName.Split(' ')[^1].ToUpperInvariant();

    /// <summary>Exempt from attendance, so every day of the month is paid without punches; joined on 1 November 2025.</summary>
    private static SaveEmployeeRequest Employee(string firstName, string? pan, string uan, string? esiNumber, BankDetailsInput bank) => new(
        null, firstName, null, "Stat" + Letters(6), Gender.Female, new DateOnly(1990, 5, 10), null, null, null, null, null, null, null,
        EmploymentType.Permanent, EmployeeStatus.Active, new DateOnly(2025, 11, 1), null, null, null, null, null, null, null,
        IsAttendanceExempt: true, SalaryPaymentMode.BankTransfer, pan, uan, null, esiNumber, null, null, bank, null);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
