using System.Net;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Modules.HR.Features.Leave;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Modules.HR.Features.Statutory;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.SharedKernel;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Leave encashment (HR-05) over the real API and PostgreSQL. In service: HR asks, another person who approves payroll
/// approves, and only then is the balance debited and the amount posted into the month's payroll inputs as a taxable
/// earning; the leave policy's yearly cap and minimum balance refuse what goes beyond them. On leaving: settling the full
/// and final statement debits the balance it pays out, once.
/// </summary>
/// <remarks>
/// Each test makes its own leave type (encashable, at most 10 days a year in service, 15 days kept) and employee, on
/// ₹20,000 basic and 40% house rent: ₹20,000 of wages a month, so ₹666.67 a day at the divisor of 30. Its salary structure
/// starts in 2031, and the encashments are paid in 2031's payroll, months no other test approves (approving a month closes
/// salary revisions dated in it or before, for the whole facility).
/// </remarks>
public sealed class LeaveEncashmentTests(HospitalServer server)
{
    private const string Leave = "/api/v1/hr/leave";
    private const string Employees = "/api/v1/hr/employees";
    private const decimal DailyRate = 666.67m;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_encashment_waits_for_another_payroll_approver_then_debits_the_balance_and_pays_through_payroll()
    {
        var hr = await server.SignInAsync(Users.HospitalAdmin);
        var (type, code) = await LeaveTypeAsync(hr);
        var employee = await EmployeeAsync(hr, type, credit: 30);
        var run = await RunAsync(hr, 2031, 2);

        // Asked for: nothing is paid or debited yet, and the days are reserved against the balance.
        var asked = await TheatreCases.AcceptedAsync(hr, HttpMethod.Post, $"{Leave}/encashments",
            new EncashLeaveRequest(employee.Id, type, 2026, 10, 2031, 2, "Encashment asked for in February"));
        asked.Kind.ShouldBe(LeaveEncashmentService.Kind);
        var balance = await BalanceAsync(hr, employee.Id, code);
        (balance.Closing, balance.Encashed, balance.Pending, balance.Available).ShouldBe((30m, 0m, 10m, 20m));
        (await hr.GetAsync<List<PendingLeaveEncashment>>($"{Leave}/encashments/pending?employeeId={employee.Id}"))
            .ShouldHaveSingleItem().Amount.ShouldBe(10 * DailyRate);
        (await InputsAsync(hr, run, employee.Id)).ShouldBeEmpty();

        // Whoever asked cannot approve it.
        (await TheatreCases.RefusalAsync(hr, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve",
            new ApproveRequest("Mine", asked.Version))).Code.ShouldBe("approval.self");

        await server.ApproveHrChangeAsync(asked.Id);

        // Approved: 10 days debited, ₹6,666.70 posted as the month's taxable leave encashment earning, and the encashment kept.
        balance = await BalanceAsync(hr, employee.Id, code);
        (balance.Closing, balance.Encashed, balance.Pending, balance.Available).ShouldBe((20m, 10m, 0m, 20m));
        var earning = (await InputsAsync(hr, run, employee.Id)).ShouldHaveSingleItem();
        (earning.Kind, earning.ComponentCode, earning.Amount, earning.Source).ShouldBe(
            (PayrollInputKind.Earning, LeaveEncashmentService.ComponentCode, 6_666.70m, PayrollInputSource.Integration));
        var component = (await hr.GetAsync<List<SalaryComponentDto>>("/api/v1/hr/payroll/components")).Single(c => c.Code == LeaveEncashmentService.ComponentCode);
        component.IsTaxable.ShouldBeTrue();
        var made = (await hr.GetAsync<List<LeaveEncashmentDto>>($"{Leave}/encashments?employeeId={employee.Id}")).ShouldHaveSingleItem();
        (made.Kind, made.Days, made.DailyRate, made.Amount, made.PayrollMonth).ShouldBe((LeaveEncashmentKind.InService, 10m, DailyRate, 6_666.70m, "02/2031"));
    }

    [Fact]
    public async Task The_policy_refuses_more_than_the_yearly_cap_and_what_would_leave_less_than_the_minimum()
    {
        var hr = await server.SignInAsync(Users.HospitalAdmin);
        var (type, _) = await LeaveTypeAsync(hr);

        // 40 in the balance: the cap of 10 a year decides.
        var rich = await EmployeeAsync(hr, type, credit: 40);
        (await TheatreCases.RefusalAsync(hr, HttpMethod.Post, $"{Leave}/encashments",
            new EncashLeaveRequest(rich.Id, type, 2026, 11, 2031, 2, "Too many"))).Code.ShouldBe(LeaveEncashmentPolicy.OverCapCode);
        var first = await TheatreCases.AcceptedAsync(hr, HttpMethod.Post, $"{Leave}/encashments",
            new EncashLeaveRequest(rich.Id, type, 2026, 6, 2031, 2, "First part"));
        await server.ApproveHrChangeAsync(first.Id);
        (await TheatreCases.RefusalAsync(hr, HttpMethod.Post, $"{Leave}/encashments",
            new EncashLeaveRequest(rich.Id, type, 2026, 5, 2031, 3, "Over what is left of the cap"))).Code.ShouldBe(LeaveEncashmentPolicy.OverCapCode);

        // 20 in the balance: 15 are kept, so at most 5.
        var poor = await EmployeeAsync(hr, type, credit: 20);
        (await TheatreCases.RefusalAsync(hr, HttpMethod.Post, $"{Leave}/encashments",
            new EncashLeaveRequest(poor.Id, type, 2026, 6, 2031, 2, "Below the minimum"))).Code.ShouldBe(LeaveEncashmentPolicy.BelowMinimumCode);
        (await hr.GetAsync<List<EncashableLeaveDto>>($"{Leave}/encashments/encashable?employeeId={poor.Id}&year=2026"))
            .Single(e => e.LeaveTypeId == type).MostDays.ShouldBe(5);
        await TheatreCases.AcceptedAsync(hr, HttpMethod.Post, $"{Leave}/encashments", new EncashLeaveRequest(poor.Id, type, 2026, 5, 2031, 2, "Up to the minimum"));
    }

    [Fact]
    public async Task The_full_and_final_settlement_debits_the_leave_it_pays_out_once_and_in_service_encashment_stops()
    {
        var hr = await server.SignInAsync(Users.HospitalAdmin);
        var (type, code) = await LeaveTypeAsync(hr);
        var employee = await EmployeeAsync(hr, type, credit: 30);
        var version = (await hr.GetAsync<EmployeeDetail>($"{Employees}/{employee.Id}")).Version;
        await hr.PostAsync($"{Employees}/{employee.Id}/separate",
            new SeparateEmployeeRequest(new DateOnly(2026, 10, 31), SeparationReason.Resignation, "Moving cities", version));

        // Leaving: unused leave is paid in the settlement, not encashed in service.
        (await TheatreCases.RefusalAsync(hr, HttpMethod.Post, $"{Leave}/encashments",
            new EncashLeaveRequest(employee.Id, type, 2026, 5, 2031, 2, "Before leaving"))).Code.ShouldBe(LeaveEncashmentService.LeavingCode);

        // Working the statement out changes nothing; settling it pays the 30 days out and debits them.
        var request = new FullAndFinalRequest(new DateOnly(2026, 10, 31), 30, 30, false, false, 0, null, 30, 8.33m, 0, null);
        var statement = await hr.PostAsync<FullAndFinalDto>($"/api/v1/hr/statutory/employees/{employee.Id}/full-and-final", request);
        statement.LeaveEncashment.Days.ShouldBe(30);
        (await BalanceAsync(hr, employee.Id, code)).Closing.ShouldBe(30);

        var settled = await hr.PostAsync<FullAndFinalDto>($"/api/v1/hr/statutory/employees/{employee.Id}/full-and-final/settle", request);
        (settled.LeaveEncashment.Days, settled.LeaveEncashment.DailyRate).ShouldBe((30m, DailyRate));
        var balance = await BalanceAsync(hr, employee.Id, code);
        (balance.Closing, balance.Encashed).ShouldBe((0m, 30m));
        var made = (await hr.GetAsync<List<LeaveEncashmentDto>>($"{Leave}/encashments?employeeId={employee.Id}")).ShouldHaveSingleItem();
        (made.Kind, made.Days, made.LastWorkingDay).ShouldBe((LeaveEncashmentKind.Separation, 30m, new DateOnly(2026, 10, 31)));

        // Once: settling again pays nothing twice.
        (await TheatreCases.RefusalAsync(hr, HttpMethod.Post, $"/api/v1/hr/statutory/employees/{employee.Id}/full-and-final/settle", request))
            .ShouldBe((HttpStatusCode.Conflict, "settlement.already_settled"));
    }

    /// <summary>A leave type of this test's own: encashable, at most 10 days a year in service, 15 kept.</summary>
    private static async Task<(Guid Id, string Code)> LeaveTypeAsync(ApiClient hr)
    {
        var code = "EN" + Letters(6).ToUpperInvariant();
        await hr.PostAsync($"{Leave}/types", new SaveLeaveTypeRequest(code, "Encashable leave " + code, 0, 0, IsEncashable: true, IsPaid: true, AllowHalfDay: false,
            TracksBalance: true, CountsNonWorkingDays: false, null, null, null, IsActive: true, null, InServiceEncashmentDaysPerYear: 10, MinimumBalanceAfterEncashment: 15));
        return ((await hr.GetAsync<List<LeaveTypeDto>>($"{Leave}/types")).Single(t => t.Code == code).Id, code);
    }

    /// <summary>An employee on ₹20,000 basic and 40% house rent, with <paramref name="credit"/> days of the type for 2026.</summary>
    private async Task<EmployeeCreated> EmployeeAsync(ApiClient hr, Guid type, decimal credit)
    {
        var employee = await hr.PostAsync<EmployeeCreated>($"{Employees}/", new SaveEmployeeRequest(
            null, "Revathi", null, "Enc" + Letters(6), Gender.Female, new DateOnly(1990, 4, 4), null, null, null, null, null, null, null,
            EmploymentType.Permanent, EmployeeStatus.Active, new DateOnly(2020, 6, 1), null, null, null, null, null, null, null,
            IsAttendanceExempt: true, SalaryPaymentMode.Cash, null, null, null, null, null, null, null, null));
        var components = (await hr.GetAsync<List<SalaryComponentDto>>("/api/v1/hr/payroll/components")).ToDictionary(c => c.Code);
        var structure = await hr.PostAsync<ApprovalRequestDto>($"{Employees}/{employee.Id}/salary-structures", new SaveSalaryStructureRequest(
            new DateOnly(2031, 1, 1), null, 0, PfApplicable: false, PfOnActualBasic: false, EsiApplicable: false, null, "Appointment letter",
            [new SalaryStructureLineInput(components["BASIC"].Id, 20_000), new SalaryStructureLineInput(components["HRA"].Id, 40)]));
        await server.ApproveHrChangeAsync(structure.Id);
        var credited = await TheatreCases.AcceptedAsync(hr, HttpMethod.Post, $"{Leave}/balances/adjust",
            new AdjustLeaveBalanceRequest(employee.Id, type, 2026, credit, "Opening balance for the test"));
        await server.ApproveHrChangeAsync(credited.Id);
        return employee;
    }

    private static async Task<LeaveBalanceDto> BalanceAsync(ApiClient hr, Guid employeeId, string code) =>
        (await hr.GetAsync<List<LeaveBalanceDto>>($"{Leave}/balances?employeeId={employeeId}&year=2026")).Single(b => b.LeaveTypeCode == code);

    private static async Task<List<PayrollInputDto>> InputsAsync(ApiClient hr, Guid run, Guid employeeId) =>
        [.. (await hr.GetAsync<List<PayrollInputDto>>($"/api/v1/hr/payroll/runs/{run}/inputs")).Where(i => i.EmployeeId == employeeId)];

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

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
