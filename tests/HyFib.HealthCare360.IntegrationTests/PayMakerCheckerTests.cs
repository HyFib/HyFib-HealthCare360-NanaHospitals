using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Attendance;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Modules.HR.Features.Leave;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlatformApprovalStatus = HyFib.HealthCare360.Platform.Approvals.ApprovalStatus;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Pay, payment details and leave on a second person's word (maker-checker slice 11), over the real API and PostgreSQL: a
/// salary's bank account or payment mode, a salary structure, an additional earning, a loss-of-pay or TDS override, a
/// deduction another module posted and a leave credit are asked for by one HR user and approved by another who holds the
/// approving permission, never the one who asked and never the employee whose pay or leave it is. The bank file pays the
/// accounts in force when the payroll was approved.
/// </summary>
public sealed class PayMakerCheckerTests(HospitalServer server)
{
    private const string Approvals = "/api/v1/approvals";
    private const string Employees = "/api/v1/hr/employees";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_salary_account_waits_for_another_payroll_approver_and_the_old_details_stay_in_force_meanwhile()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var manager = await server.SignInAsync(Users.HrManager);
        var employee = await admin.PostAsync<EmployeeCreated>($"{Employees}/", Employee(SalaryPaymentMode.Cash));
        employee.BankApprovalId.ShouldBeNull();
        var path = $"{Employees}/{employee.Id}/bank-details";
        var version = (await admin.GetAsync<EmployeeDetail>($"{Employees}/{employee.Id}")).Version;

        // A reason is needed; then the change waits, and the employee is still paid in cash meanwhile.
        (await TheatreCases.InvalidFieldsAsync(admin, HttpMethod.Put, path,
            new UpdateBankDetailsRequest(SalaryPaymentMode.BankTransfer, "State Bank of India", "001234567890", "SBIN0001234", false, "", version)))
            .ShouldContain(f => f.Equals("reason", StringComparison.OrdinalIgnoreCase));
        var asked = await TheatreCases.AcceptedAsync(admin, HttpMethod.Put, path,
            new UpdateBankDetailsRequest(SalaryPaymentMode.BankTransfer, "State Bank of India", "001234567890", "SBIN0001234", false, "Opened a salary account", version));
        asked.Kind.ShouldBe(PaymentDetailsApprovals.Kind);
        asked.Summary.ShouldContain("cash → bank transfer to account XXXXXXXX7890");
        asked.Summary.ShouldNotContain("001234567890");
        string.Join(" ", asked.Details.Select(d => d.Value)).ShouldNotContain("001234567890");
        var waiting = await admin.GetAsync<EmployeeDetail>($"{Employees}/{employee.Id}");
        (waiting.PaymentMode, waiting.BankAccountMasked).ShouldBe((SalaryPaymentMode.Cash, null));
        waiting.PendingPaymentDetails.ShouldNotBeNull().ShouldSatisfyAllConditions(
            p => p.ApprovalId.ShouldBe(asked.Id),
            p => p.PaymentMode.ShouldBe(SalaryPaymentMode.BankTransfer),
            p => p.BankAccountMasked.ShouldBe("XXXXXXXX7890"));
        (await TheatreCases.RefusalAsync(admin, HttpMethod.Put, path,
                new UpdateBankDetailsRequest(SalaryPaymentMode.Cheque, null, null, null, false, "Cheque instead", version)))
            .ShouldBe((HttpStatusCode.Conflict, "approval.already_requested"));

        // The one who asked does not approve it, nor does someone who may not approve payroll.
        var request = await admin.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        (await TheatreCases.RefusalAsync(admin, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, request.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await TheatreCases.RefusalAsync(await server.SignInAsync(Users.Nurse), HttpMethod.Post, $"{Approvals}/{asked.Id}/approve",
            new ApproveRequest(null, request.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));

        // The HR manager approves: the account is in force, audited with both names and the last four digits only.
        var approved = await manager.PostAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}/approve", new ApproveRequest("Bank letter seen", request.Version));
        approved.Status.ShouldBe(PlatformApprovalStatus.Approved);
        var now = await admin.GetAsync<EmployeeDetail>($"{Employees}/{employee.Id}");
        (now.PaymentMode, now.BankAccountMasked, now.BankIfscMasked, now.PendingPaymentDetails).ShouldBe((SalaryPaymentMode.BankTransfer, "XXXXXXXX7890", "SBIN0XXXXXX", null));
        var audited = await AuditAsync("employee.bank_changed", employee.Id);
        audited.Summary.ShouldNotBeNull().ShouldContain("asked for by");
        audited.Summary.ShouldContain("approved by");
        audited.Summary.ShouldContain("XXXXXXXX7890");
        audited.Summary.ShouldNotContain("001234567890");

        // Switching the salary to cash waits too, also when asked from the employee's record.
        var record = await admin.GetAsync<EmployeeDetail>($"{Employees}/{employee.Id}");
        var toCash = await TheatreCases.AcceptedAsync(admin, HttpMethod.Put, $"{Employees}/{employee.Id}",
            Employee(SalaryPaymentMode.Cash) with { FirstName = record.FirstName, LastName = record.LastName, Version = record.Version });
        toCash.Summary.ShouldContain("bank transfer to account XXXXXXXX7890 (SBIN0XXXXXX, State Bank of India) → cash");
        (await admin.GetAsync<EmployeeDetail>($"{Employees}/{employee.Id}")).PaymentMode.ShouldBe(SalaryPaymentMode.BankTransfer);
        await WithdrawAsync(admin, toCash.Id);
    }

    [Fact]
    public async Task Nobody_approves_their_own_salary_account_pay_or_leave_nor_makes_a_change_to_their_own_pay()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var manager = await server.SignInAsync(Users.HrManager);
        var own = await OwnEmployeeAsync(manager);
        var run = await RunAsync(admin, 2026, 7);

        // The HR manager neither enters a change to their own pay nor adjusts their own leave.
        (await TheatreCases.RefusalAsync(manager, HttpMethod.Post, $"/api/v1/hr/payroll/runs/{run}/inputs",
            new SavePayrollInputRequest(own.Id, PayrollInputKind.Earning, "INCENTIVE", "Own incentive", 5_000))).ShouldBe((HttpStatusCode.Forbidden, PayApprovals.OwnCode));
        (await TheatreCases.RefusalAsync(manager, HttpMethod.Post, $"/api/v1/hr/payroll/runs/{run}/inputs",
            new SavePayrollInputRequest(own.Id, PayrollInputKind.Deduction, "ADVANCE", "Own advance", 500))).ShouldBe((HttpStatusCode.Forbidden, PayApprovals.OwnCode));
        (await TheatreCases.RefusalAsync(manager, HttpMethod.Post, $"{Employees}/{own.Id}/salary-structures", Structure(await ComponentsAsync(admin), new DateOnly(2026, 8, 1))))
            .ShouldBe((HttpStatusCode.Forbidden, PayApprovals.OwnCode));
        var casual = await LeaveTypeAsync(admin, "CL");
        (await TheatreCases.RefusalAsync(manager, HttpMethod.Post, "/api/v1/hr/leave/balances/adjust",
            new AdjustLeaveBalanceRequest(own.Id, casual, 2026, 2, "Own comp-off"))).ShouldBe((HttpStatusCode.Forbidden, LeaveService.OwnCode));
        (await TheatreCases.RefusalAsync(manager, HttpMethod.Post, "/api/v1/hr/leave/balances/adjust",
            new AdjustLeaveBalanceRequest(own.Id, casual, 2026, -1, "Own correction"))).ShouldBe((HttpStatusCode.Forbidden, LeaveService.OwnCode));

        // What the administrator asks for the HR manager's record, the HR manager does not approve: it stays waiting.
        var version = (await admin.GetAsync<EmployeeDetail>($"{Employees}/{own.Id}")).Version;
        var bank = await TheatreCases.AcceptedAsync(admin, HttpMethod.Put, $"{Employees}/{own.Id}/bank-details",
            new UpdateBankDetailsRequest(SalaryPaymentMode.BankTransfer, null, "000987654321", "HDFC0000123", false, "New salary account", version));
        var earning = await TheatreCases.AcceptedAsync(admin, HttpMethod.Post, $"/api/v1/hr/payroll/runs/{run}/inputs",
            new SavePayrollInputRequest(own.Id, PayrollInputKind.Earning, "INCENTIVE", "Quarterly incentive", 5_000));
        var credit = await TheatreCases.AcceptedAsync(admin, HttpMethod.Post, "/api/v1/hr/leave/balances/adjust",
            new AdjustLeaveBalanceRequest(own.Id, casual, 2026, 2, "Worked on the holiday"));
        foreach (var (asked, code) in new[] { (bank, PaymentDetailsApprovals.OwnCode), (earning, PayApprovals.OwnCode), (credit, LeaveService.OwnCode) })
        {
            var request = await manager.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
            request.CanDecide.ShouldBeTrue();
            (await TheatreCases.RefusalAsync(manager, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, request.Version)))
                .ShouldBe((HttpStatusCode.Forbidden, code));
            (await manager.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}")).Status.ShouldBe(PlatformApprovalStatus.Pending);
            await WithdrawAsync(admin, asked.Id);
        }

        // Nor does the HR manager decide their own leave, applied for them by HR.
        var leave = await admin.PostAsync<Guid>($"{Employees}/{own.Id}/leave-requests",
            new ApplyLeaveRequest(await LeaveTypeAsync(admin, "LOP"), WorkingDay(30), LeaveSession.FirstHalf, WorkingDay(30), LeaveSession.SecondHalf, "Family function", null));
        (await TheatreCases.RefusalAsync(manager, HttpMethod.Post, $"/api/v1/hr/leave/requests/{leave}/approve", new DecisionRequest("Fine")))
            .ShouldBe((HttpStatusCode.Forbidden, LeaveService.OwnCode));
        await admin.PostAsync($"/api/v1/hr/leave/requests/{leave}/cancel", new CancelLeaveRequest("Test over"));
    }

    [Fact]
    public async Task The_bank_file_pays_the_accounts_in_force_when_the_payroll_was_approved()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var manager = await server.SignInAsync(Users.HrManager);

        // An employee from December 2023, whose first account waits like any later one.
        var request = Employee(SalaryPaymentMode.BankTransfer) with
        {
            JoiningDate = new DateOnly(2023, 12, 1),
            Bank = new BankDetailsInput("Canara Bank", "111122223333", "CNRB0001234"),
        };
        var employee = await admin.PostAsync<EmployeeCreated>($"{Employees}/", request);
        var firstAccount = employee.BankApprovalId.ShouldNotBeNull();
        (await admin.GetAsync<EmployeeDetail>($"{Employees}/{employee.Id}")).BankAccountMasked.ShouldBeNull();
        await server.ApproveHrChangeAsync(firstAccount);
        var structure = await TheatreCases.AcceptedAsync(admin, HttpMethod.Post, $"{Employees}/{employee.Id}/salary-structures",
            Structure(await ComponentsAsync(admin), new DateOnly(2023, 12, 1)));
        (await admin.GetAsync<List<SalaryStructureDto>>($"{Employees}/{employee.Id}/salary-structures")).ShouldBeEmpty();
        await server.ApproveHrChangeAsync(structure.Id);

        // January 2024: processed by the administrator, approved by the HR manager. Approved, it closes every month up to it to
        // new salary structures, so the test reopens and removes it whatever happens.
        var run = await RunAsync(admin, 2024, 1);
        try
        {
            await admin.PostAsync<PayrollProcessResult>($"/api/v1/hr/payroll/runs/{run}/process");
            await manager.PostAsync($"/api/v1/hr/payroll/runs/{run}/approve");

            // The account changes after the approval, approved by a second person as it must be.
            var version = (await admin.GetAsync<EmployeeDetail>($"{Employees}/{employee.Id}")).Version;
            var moved = await TheatreCases.AcceptedAsync(admin, HttpMethod.Put, $"{Employees}/{employee.Id}/bank-details",
                new UpdateBankDetailsRequest(SalaryPaymentMode.BankTransfer, "Indian Bank", "444455556666", "IDIB000A123", false, "Changed banks", version));
            await server.ApproveHrChangeAsync(moved.Id);
            (await admin.GetAsync<EmployeeDetail>($"{Employees}/{employee.Id}")).BankAccountMasked.ShouldBe("XXXXXXXX6666");

            // The bank file still pays the account in force at approval.
            var file = await BankFileAsync(manager, run);
            var row = file.Split("\r\n").Single(l => l.StartsWith(employee.EmployeeCode + ",", StringComparison.Ordinal));
            row.ShouldContain(",111122223333,CNRB0001234,");
            file.ShouldNotContain("444455556666");

            // A payroll approved before the details were fixed at approval has no bank file until it is approved again.
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PayrollRun>().Where(r => r.Id == run)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.BankDetailsFixedAt, (DateTimeOffset?)null), Ct);
            }

            (await TheatreCases.RefusalAsync(manager, HttpMethod.Get, $"/api/v1/hr/payroll/runs/{run}/bank-transfer.csv", null))
                .ShouldBe((HttpStatusCode.UnprocessableEntity, PayrollService.BankDetailsNotFixedCode));

            // Reopened and approved again, it pays the account in force now.
            await manager.PostAsync($"/api/v1/hr/payroll/runs/{run}/reopen", new ReopenPayrollRequest("Account changed"));
            await manager.PostAsync($"/api/v1/hr/payroll/runs/{run}/approve");
            (await BankFileAsync(manager, run)).Split("\r\n").Single(l => l.StartsWith(employee.EmployeeCode + ",", StringComparison.Ordinal))
                .ShouldContain(",444455556666,IDIB000A123,");
        }
        finally
        {
            using (await manager.SendRawAsync(HttpMethod.Post, $"/api/v1/hr/payroll/runs/{run}/reopen", new ReopenPayrollRequest("Test over")))
            {
            }

            using (await admin.SendRawAsync(HttpMethod.Delete, $"/api/v1/hr/payroll/runs/{run}"))
            {
            }
        }
    }

    [Fact]
    public async Task An_earning_or_override_waits_a_deduction_does_not_and_another_modules_deduction_is_not_taken_off_alone()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var manager = await server.SignInAsync(Users.HrManager);
        var employee = await admin.PostAsync<EmployeeCreated>($"{Employees}/", Employee(SalaryPaymentMode.Cash));
        var run = await RunAsync(admin, 2026, 7);
        var inputs = $"/api/v1/hr/payroll/runs/{run}/inputs";

        // A deduction is made at once.
        using (var deducted = await admin.SendRawAsync(HttpMethod.Post, inputs, new SavePayrollInputRequest(employee.Id, PayrollInputKind.Deduction, "ADVANCE", "Advance", 1_000)))
        {
            deducted.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // An earning, a loss-of-pay override and a TDS override wait, and are not in the run meanwhile.
        var earning = await TheatreCases.AcceptedAsync(admin, HttpMethod.Post, inputs, new SavePayrollInputRequest(employee.Id, PayrollInputKind.Earning, "INCENTIVE", "Incentive", 3_000));
        var lop = await TheatreCases.AcceptedAsync(admin, HttpMethod.Post, inputs, new SavePayrollInputRequest(employee.Id, PayrollInputKind.LopDaysOverride, null, "No LOP", 0));
        var tds = await TheatreCases.AcceptedAsync(admin, HttpMethod.Post, inputs, new SavePayrollInputRequest(employee.Id, PayrollInputKind.TdsOverride, null, "Declaration", 0));
        (earning.Kind, lop.Kind, tds.Kind).ShouldBe((PayApprovals.InputKind, PayApprovals.InputKind, PayApprovals.InputKind));
        earning.Summary.ShouldContain("Earning INCENTIVE ₹3,000.00");
        (await InputsAsync(admin, run, employee.Id)).Select(i => i.Kind).ShouldBe([PayrollInputKind.Deduction]);
        (await admin.GetAsync<List<PendingPayChange>>($"/api/v1/hr/payroll/pending-changes?runId={run}&employeeId={employee.Id}")).Select(p => p.ApprovalId)
            .ShouldBe([earning.Id, lop.Id, tds.Id], ignoreOrder: true);

        // The one who asked does not approve; the HR manager does, and it is audited with both names.
        var request = await admin.GetAsync<ApprovalRequestDto>($"{Approvals}/{earning.Id}");
        (await TheatreCases.RefusalAsync(admin, HttpMethod.Post, $"{Approvals}/{earning.Id}/approve", new ApproveRequest(null, request.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        await server.ApproveHrChangeAsync(earning.Id);
        var added = (await InputsAsync(admin, run, employee.Id)).Single(i => i.Kind == PayrollInputKind.Earning);
        (added.ComponentCode, added.Amount).ShouldBe(("INCENTIVE", 3_000m));
        var audited = await AuditAsync("payroll.input_added", added.Id);
        audited.Summary.ShouldNotBeNull().ShouldContain("asked for by");
        audited.Summary.ShouldContain("approved by");
        await WithdrawAsync(admin, lop.Id);
        await WithdrawAsync(admin, tds.Id);

        // A deduction the HR manager entered is taken off by the administrator only with approval; one's own at once.
        var theirs = await manager.PostAsync<Guid>(inputs, new SavePayrollInputRequest(employee.Id, PayrollInputKind.Deduction, "UNIFORM", "Uniform", 400));
        var removal = await TheatreCases.AcceptedAsync(admin, HttpMethod.Delete, $"/api/v1/hr/payroll/inputs/{theirs}", null!);
        removal.Kind.ShouldBe(PayApprovals.RemovalKind);
        (await InputsAsync(admin, run, employee.Id)).ShouldContain(i => i.Id == theirs);
        await server.ApproveHrChangeAsync(removal.Id);
        (await InputsAsync(admin, run, employee.Id)).ShouldNotContain(i => i.Id == theirs);
        var mine = await admin.PostAsync<Guid>(inputs, new SavePayrollInputRequest(employee.Id, PayrollInputKind.Deduction, "UNIFORM", "Uniform again", 400));
        using (var removed = await admin.SendRawAsync(HttpMethod.Delete, $"/api/v1/hr/payroll/inputs/{mine}"))
        {
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // A deduction another module posted (the canteen's staff credit) is never taken off on one person's word.
        Guid canteen;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            canteen = (await scope.ServiceProvider.GetRequiredService<PayrollInputService>()
                .PostDeductionAsync(employee.Id, 2026, 7, "CANTEEN", 750, "Canteen credit", "canteen-" + Guid.NewGuid().ToString("N"), Ct)).Value;
        }

        var canteenRemoval = await TheatreCases.AcceptedAsync(admin, HttpMethod.Delete, $"/api/v1/hr/payroll/inputs/{canteen}", null!);
        canteenRemoval.Details.ShouldContain(d => d.Value.Contains("posted by another module", StringComparison.Ordinal));
        await WithdrawAsync(admin, canteenRemoval.Id);
        (await InputsAsync(admin, run, employee.Id)).ShouldContain(i => i.Id == canteen);

        // An imported file with an earning in it waits as a whole; one of deductions only is imported at once.
        using var withEarning = await admin.SendRawAsync(HttpMethod.Post, $"{inputs}/import",
            new ImportPayrollInputsRequest($"{employee.EmployeeCode},INCENTIVE,1000,Bonus\n{employee.EmployeeCode},ADVANCE,200,Advance"));
        withEarning.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var import = await ApiClient.ReadAsync<PayrollImportResult>(withEarning, HttpMethod.Post, $"{inputs}/import");
        import.Imported.ShouldBe(0);
        import.Approval.ShouldNotBeNull().Kind.ShouldBe(PayApprovals.ImportKind);
        await server.ApproveHrChangeAsync(import.Approval.Id);
        (await InputsAsync(admin, run, employee.Id)).Count(i => i.Source == PayrollInputSource.Import).ShouldBe(2);
        (await admin.PostAsync<PayrollImportResult>($"{inputs}/import", new ImportPayrollInputsRequest($"{employee.EmployeeCode},ADVANCE,300,Advance")))
            .ShouldSatisfyAllConditions(r => r.Imported.ShouldBe(1), r => r.Approval.ShouldBeNull());
    }

    [Fact]
    public async Task A_leave_credit_waits_for_another_leave_approver_and_a_debit_is_made_at_once()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var employee = await admin.PostAsync<EmployeeCreated>($"{Employees}/", Employee(SalaryPaymentMode.Cash));
        var casual = await LeaveTypeAsync(admin, "CL");

        var credit = await TheatreCases.AcceptedAsync(admin, HttpMethod.Post, "/api/v1/hr/leave/balances/adjust",
            new AdjustLeaveBalanceRequest(employee.Id, casual, 2026, 2, "Worked on Diwali"));
        credit.Kind.ShouldBe(LeaveService.AdjustmentKind);
        (await CasualAsync(admin, employee.Id)).ShouldBe(0m);
        (await admin.GetAsync<List<PendingLeaveAdjustment>>($"/api/v1/hr/leave/balances/pending?employeeId={employee.Id}")).ShouldHaveSingleItem().Days.ShouldBe(2m);
        var request = await admin.GetAsync<ApprovalRequestDto>($"{Approvals}/{credit.Id}");
        (await TheatreCases.RefusalAsync(admin, HttpMethod.Post, $"{Approvals}/{credit.Id}/approve", new ApproveRequest(null, request.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));

        await server.ApproveHrChangeAsync(credit.Id);
        (await CasualAsync(admin, employee.Id)).ShouldBe(2m);
        var audited = await AuditAsync("leave.balance_adjusted", null, $"of {employee.EmployeeCode} credited +2 day(s)");
        audited.Summary.ShouldNotBeNull().ShouldContain("asked for by");
        audited.Summary.ShouldContain("approved by");

        using (var debit = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/hr/leave/balances/adjust",
                   new AdjustLeaveBalanceRequest(employee.Id, casual, 2026, -0.5m, "Half day taken unrecorded")))
        {
            debit.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await CasualAsync(admin, employee.Id)).ShouldBe(1.5m);
    }

    private static SaveEmployeeRequest Employee(SalaryPaymentMode mode) => new(
        null, "Meena", null, "Pay" + Letters(6), Gender.Female, new DateOnly(1992, 3, 3), null, null, null, null, null, null, null,
        EmploymentType.Permanent, EmployeeStatus.Active, new DateOnly(2025, 12, 1), null, null, null, null, null, null, null,
        IsAttendanceExempt: true, mode, null, null, null, null, null, null, null, null);

    private static SaveSalaryStructureRequest Structure(IReadOnlyDictionary<string, SalaryComponentDto> components, DateOnly from) => new(
        from, null, 0, PfApplicable: false, PfOnActualBasic: false, EsiApplicable: false, null, "Appointment letter",
        [new SalaryStructureLineInput(components["BASIC"].Id, 20_000), new SalaryStructureLineInput(components["HRA"].Id, 40)]);

    private static async Task<IReadOnlyDictionary<string, SalaryComponentDto>> ComponentsAsync(ApiClient hr) =>
        (await hr.GetAsync<List<SalaryComponentDto>>("/api/v1/hr/payroll/components")).ToDictionary(c => c.Code);

    private static async Task<Guid> LeaveTypeAsync(ApiClient hr, string code) =>
        (await hr.GetAsync<List<LeaveTypeDto>>("/api/v1/hr/leave/types")).Single(t => t.Code == code).Id;

    private static async Task<decimal> CasualAsync(ApiClient hr, Guid employeeId) =>
        (await hr.GetAsync<List<LeaveBalanceDto>>($"/api/v1/hr/leave/balances?employeeId={employeeId}&year=2026")).Single(b => b.LeaveTypeCode == "CL").Closing;

    private static async Task<List<PayrollInputDto>> InputsAsync(ApiClient hr, Guid run, Guid employeeId) =>
        [.. (await hr.GetAsync<List<PayrollInputDto>>($"/api/v1/hr/payroll/runs/{run}/inputs")).Where(i => i.EmployeeId == employeeId)];

    private static async Task<string> BankFileAsync(ApiClient hr, Guid run)
    {
        using var response = await hr.SendRawAsync(HttpMethod.Get, $"/api/v1/hr/payroll/runs/{run}/bank-transfer.csv");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadAsStringAsync(Ct);
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

    /// <summary>The employee record linked to <paramref name="user"/>'s account: one made the first time it is needed.</summary>
    private async Task<EmployeeCreated> OwnEmployeeAsync(ApiClient user)
    {
        var userId = (await user.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var linked = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Employee>().IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.UserId == userId).Select(e => new EmployeeCreated(e.Id, e.EmployeeCode, null)).FirstOrDefaultAsync(Ct);
            if (linked is not null)
            {
                return linked;
            }
        }

        var admin = await server.SignInAsync(Users.HospitalAdmin);
        return await admin.PostAsync<EmployeeCreated>($"{Employees}/", Employee(SalaryPaymentMode.Cash) with { FirstName = "Own", UserId = userId });
    }

    private static async Task WithdrawAsync(ApiClient maker, Guid approvalId)
    {
        var request = await maker.GetAsync<ApprovalRequestDto>($"{Approvals}/{approvalId}");
        await maker.PostAsync<ApprovalRequestDto>($"{Approvals}/{approvalId}/withdraw", new WithdrawApprovalRequest(request.Version));
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid? entityId, string? containing = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking().Where(a => a.Action == action);
        if (entityId is { } id)
        {
            events = events.Where(a => a.EntityId == id.ToString());
        }

        if (containing is not null)
        {
            events = events.Where(a => a.Summary!.Contains(containing));
        }

        return await events.OrderByDescending(a => a.OccurredAt).FirstAsync(Ct);
    }

    /// <summary>A weekday this many days ahead (LOP counts working days only).</summary>
    private static DateOnly WorkingDay(int days)
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            day = day.AddDays(1);
        }

        return day;
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
