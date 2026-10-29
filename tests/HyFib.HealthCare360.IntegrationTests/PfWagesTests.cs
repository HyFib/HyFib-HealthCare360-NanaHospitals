using System.Net;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// PF on "wages" as the Code on Social Security 2020 defines them (s.2(88)), in force since 21 November 2025: basic pay,
/// dearness allowance and the other pay the definition does not leave out, and of the allowances it does leave out (house
/// rent, conveyance, overtime and the like), what comes to more than half of the month's pay. PF was taken on basic pay
/// alone.
/// </summary>
public sealed class PfWagesTests(HospitalServer server)
{
    [Fact]
    public async Task Pf_is_on_wages_with_the_allowances_above_half_the_pay_added_back()
    {
        var hr = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var components = (await hr.GetAsync<List<SalaryComponentDto>>("/api/v1/hr/payroll/components")).ToDictionary(c => c.Code);

        // An attendant on ₹6,000 basic, with house rent (40% of basic: ₹2,400), conveyance (₹1,600) and a special allowance
        // (₹2,000).
        var employee = await hr.PostAsync<EmployeeCreated>("/api/v1/hr/employees/", Employee());
        await hr.PostAsync<Created>($"/api/v1/hr/employees/{employee.Id}/salary-structures", new SaveSalaryStructureRequest(
            new DateOnly(2026, 1, 1), null, 0, PfApplicable: true, PfOnActualBasic: false, EsiApplicable: false, null, null,
            [
                new SalaryStructureLineInput(components["BASIC"].Id, 6_000),
                new SalaryStructureLineInput(components["HRA"].Id, 40),
                new SalaryStructureLineInput(components["CONV"].Id, 1_600),
                new SalaryStructureLineInput(components["SPECIAL"].Id, 2_000),
            ]));

        // June 2026 in full, with ₹8,000 of overtime: ₹20,000 of pay, of which ₹12,000 is house rent, conveyance and overtime.
        var run = await RunAsync(hr, 2026, 6);
        await hr.PostAsync<Guid>($"/api/v1/hr/payroll/runs/{run}/inputs",
            new SavePayrollInputRequest(employee.Id, PayrollInputKind.Earning, "OT", "Overtime in June", 8_000));
        await hr.PostAsync<PayrollProcessResult>($"/api/v1/hr/payroll/runs/{run}/process");

        var listed = (await hr.GetAsync<PagedResult<PayslipListItem>>($"/api/v1/hr/payroll/runs/{run}/payslips?search={employee.EmployeeCode}")).Items
            .Single(p => p.EmployeeId == employee.Id);
        var payslip = await hr.GetAsync<PayslipDto>($"/api/v1/hr/payroll/payslips/{listed.Id}");

        payslip.GrossEarnings.ShouldBe(20_000m);

        // Wages are the basic and the special allowance: ₹8,000. The ₹12,000 left out is ₹2,000 more than half the pay, and
        // that ₹2,000 is added back: PF is 12% of ₹10,000, not of the ₹6,000 basic.
        payslip.PfWage.ShouldBe(10_000m);
        payslip.Deductions.Single(d => d.Code == SalaryComponent.PfCode).Amount.ShouldBe(1_200m);
        payslip.EmployerPf.ShouldBe(1_200m);
    }

    /// <summary>The month's payroll: a new one, or the one already made for that month by another test.</summary>
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

    /// <summary>Exempt from attendance, so every day of the month is paid without punches.</summary>
    private static SaveEmployeeRequest Employee() => new(
        null, "Kavya", null, "Wages" + Letters(6), Gender.Female, new DateOnly(1995, 5, 10), null, null, null, null, null, null, null,
        EmploymentType.Permanent, EmployeeStatus.Active, new DateOnly(2025, 12, 1), null, null, null, null, null, null, null,
        IsAttendanceExempt: true, SalaryPaymentMode.Cash, null, null, null, null, null, null, null, null);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);
}
