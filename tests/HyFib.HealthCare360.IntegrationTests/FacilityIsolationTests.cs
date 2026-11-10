using System.Globalization;
using System.Net;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Attendance;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Modules.HR.Features.Leave;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Capas;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A facility's lists show that facility's records only, and removed records nowhere, over the real API and database.
/// EF Core ignores a query filter for the whole query, so a list that read a lookup (a department's name, an employee's,
/// an assignee's, an incident number, a merged patient) without the facility or soft-delete filter read its own records
/// without it too: the appointment desk listed every branch's appointments, HR every branch's leave and regularisations,
/// the privacy desk every branch's data requests, quality every branch's actions, and the critical-values register listed
/// results taken off a rejected tube. Each test adds the same kind of record at another facility of the group (the hospital
/// administrator adds the facility; the records go straight into the database) and reads the lists as the main hospital.
/// </summary>
public sealed class FacilityIsolationTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    [Fact]
    public async Task The_appointment_desk_lists_and_opens_its_own_facilitys_appointments_only()
    {
        var (main, annexe) = await FacilitiesAsync();
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        Appointment ours, theirs;
        string departmentName;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var department = await db.Set<Department>().Where(d => d.FacilityId == main).Select(d => new { d.Id, d.Name }).FirstAsync(Ct);
            departmentName = department.Name;

            // A doctor of the test's own, years ahead: no other test's doctor gets a booking, a seat or work ahead from it.
            var doctor = Guid.CreateVersion7();
            var at = new DateTimeOffset(2099, 6, 15, 4, 30, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(0, 480));
            ours = Book(main, patient.Id, doctor, department.Id, at);
            theirs = Book(annexe, patient.Id, doctor, department.Id, at.AddMinutes(15));
            db.AddRange(ours, theirs);
            await db.SaveChangesAsync(Ct);
        }

        var desk = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var from = Day(ours.StartsAt);
        var to = Day(theirs.StartsAt);
        var listed = (await desk.GetAsync<PagedResult<AppointmentListItem>>(string.Create(CultureInfo.InvariantCulture,
            $"/api/v1/scheduling/appointments?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&patientId={patient.Id}&pageSize=50"))).Items;

        listed.Select(a => a.Id).ShouldBe([ours.Id], "the annexe's appointment of the same patient is the annexe's");
        listed.Single().DepartmentName.ShouldBe(departmentName);
        (await desk.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{ours.Id}")).DepartmentName.ShouldBe(departmentName);
        (await desk.SendRawAsync(HttpMethod.Get, $"/api/v1/scheduling/appointments/{theirs.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HR_reads_its_own_facilitys_leave_regularisations_and_payslips_and_still_names_staff_who_moved()
    {
        var (main, annexe) = await FacilitiesAsync();
        var hr = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var moved = await hr.PostAsync<EmployeeCreated>("/api/v1/hr/employees/", Employee("Moved"));
        var there = await hr.PostAsync<EmployeeCreated>("/api/v1/hr/employees/", Employee("There"));
        var uan = "10" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);
        LeaveRequest ourLeave, theirLeave;
        AttendanceRegularisation ourDay, theirDay;
        Payslip ourSlip, theirSlip;
        string movedName;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

            // Both work at the annexe now; the first asked for leave, a corrected day and was paid while at the main hospital.
            var employees = await db.Set<Employee>().Where(e => e.Id == moved.Id || e.Id == there.Id).ToListAsync(Ct);
            foreach (var employee in employees)
            {
                employee.FacilityId = annexe;
            }

            employees.Single(e => e.Id == moved.Id).Uan = uan;
            movedName = employees.Single(e => e.Id == moved.Id).FullName;
            var leaveType = await db.Set<LeaveType>().Select(t => t.Id).FirstAsync(Ct);
            var day = new DateOnly(2099, 1, 1).AddDays(Random.Shared.Next(0, 300));
            ourLeave = Leave(main, moved.Id, leaveType, day);
            theirLeave = Leave(annexe, there.Id, leaveType, day);
            ourDay = Regularisation(main, moved.Id, day);
            theirDay = Regularisation(annexe, there.Id, day);
            var month = Random.Shared.Next(1, 13);
            var ourRun = Run(main, month);
            var theirRun = Run(annexe, month);
            ourSlip = Slip(ourRun, moved);
            theirSlip = Slip(theirRun, there);
            db.AddRange(ourLeave, theirLeave, ourDay, theirDay, ourRun, theirRun, ourSlip, theirSlip);
            await db.SaveChangesAsync(Ct);
        }

        var leave = (await hr.GetAsync<PagedResult<LeaveRequestDto>>($"/api/v1/hr/leave/requests?employeeId={moved.Id}")).Items.ShouldHaveSingleItem();
        (leave.Id, leave.EmployeeCode, leave.EmployeeName).ShouldBe((ourLeave.Id, moved.EmployeeCode, movedName), "asked for at the main hospital, by someone now at the annexe");
        (await hr.GetAsync<PagedResult<LeaveRequestDto>>($"/api/v1/hr/leave/requests?employeeId={there.Id}")).Items.ShouldBeEmpty();

        var days = (await hr.GetAsync<PagedResult<RegularisationDto>>("/api/v1/hr/attendance/regularisations?status=Pending&pageSize=200")).Items;
        days.Where(d => d.Id == ourDay.Id).Select(d => (d.EmployeeCode, d.EmployeeName)).ShouldBe([(moved.EmployeeCode, movedName)]);
        days.ShouldNotContain(d => d.Id == theirDay.Id);

        (await hr.GetAsync<PayslipDto>($"/api/v1/hr/payroll/payslips/{ourSlip.Id}")).Uan.ShouldBe(uan);
        (await hr.SendRawAsync(HttpMethod.Get, $"/api/v1/hr/payroll/payslips/{theirSlip.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        theirLeave.Id.ShouldNotBe(ourLeave.Id);
    }

    [Fact]
    public async Task The_privacy_desk_and_quality_list_their_own_facilitys_requests_and_actions_only()
    {
        var (main, annexe) = await FacilitiesAsync();
        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var me = await officer.GetAsync<AuthUser>("/api/v1/auth/me");
        DataRequest ourRequest, theirRequest;
        Capa ourAction, theirAction;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            ourRequest = Request(main);
            theirRequest = Request(annexe);
            ourAction = CorrectiveAction(main, me.Id);
            theirAction = CorrectiveAction(annexe, me.Id);
            db.AddRange(ourRequest, theirRequest, ourAction, theirAction);
            await db.SaveChangesAsync(Ct);
        }

        (await officer.GetAsync<PagedResult<DataRequestListItem>>($"/api/v1/privacy/requests?search={ourRequest.RequestNumber}")).Items
            .Select(r => r.Id).ShouldBe([ourRequest.Id]);
        (await officer.GetAsync<PagedResult<DataRequestListItem>>($"/api/v1/privacy/requests?search={theirRequest.RequestNumber}")).Items.ShouldBeEmpty();
        (await officer.GetAsync<PagedResult<CapaListItem>>($"/api/v1/quality/capas?search={ourAction.CapaNumber}")).Items
            .Select(c => c.Id).ShouldBe([ourAction.Id]);
        (await officer.GetAsync<PagedResult<CapaListItem>>($"/api/v1/quality/capas?search={theirAction.CapaNumber}")).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_critical_value_taken_off_with_its_rejected_tube_is_not_in_the_critical_values_register()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var kept = await LabVerificationTests.RegisterPatientAsync(server);
        var rejected = await LabVerificationTests.RegisterPatientAsync(server);
        foreach (var patient in new[] { kept, rejected })
        {
            var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-ELECTROLYTES", patient.Id);
            var lines = await LabVerificationTests.EnterAsync(lab, tube, new Dictionary<string, string> { ["NA"] = "138", ["K"] = "7.2", ["CL"] = "101" });
            lines.Single(l => l.TestCode == "K").Flag.ShouldBe(ResultFlag.CriticalHigh);
            if (patient == rejected)
            {
                // The tube turns out haemolysed: its values are taken off (kept in the audit trail) and must reach no report.
                await lab.PostAsync<SampleRejected>($"/api/v1/lab/samples/{tube.Id}/reject",
                    new RejectSampleRequest(SampleRejectionReason.Haemolysed, "Haemolysed on receipt", Recollect: false));
            }
        }

        var analyst = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var today = Day(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var register = await analyst.PostAsync<ReportRows>("/api/v1/analytics/reports/critical-values/run",
            new { Parameters = new Dictionary<string, string?> { ["from"] = today, ["to"] = today }, PageSize = 500 });

        var patients = register.Rows.Select(row => row.Select(cell => cell?.ToString()).ToList()).ToList();
        patients.ShouldContain(row => row.Contains(kept.Uhid), "the critical potassium that stands is listed");
        patients.ShouldNotContain(row => row.Contains(rejected.Uhid), "the one taken off with its tube is not");
    }

    /// <summary>The main hospital (the administrator's) and a new facility of the group, out of service so it shows in no picker.</summary>
    private async Task<(Guid Main, Guid Annexe)> FacilitiesAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var main = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("The administrator has no facility.");
        var annexe = await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            Letters(6).ToUpperInvariant(), "Nana Hospitals Annexe " + Letters(4), null, new Address { City = "Chennai", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, IsActive: false, null));
        return (main, annexe.Id);
    }

    private static Appointment Book(Guid facility, Guid patient, Guid doctor, Guid department, DateTimeOffset at) => Appointment.Book(
        new AppointmentBooking(facility, patient, doctor, department, ScheduleId: null, at, at.AddMinutes(10), SeatNumber: 1, AppointmentChannel.Phone,
            AppointmentVisitType.New, IsTelehealth: false, Room: null, FeeAmount: 0m, Reason: "Facility isolation", Notes: null, BookedByUserId: null),
        "APT" + Letters(9).ToUpperInvariant(), DateTimeOffset.UtcNow);

    private static SaveEmployeeRequest Employee(string surname) => new(
        null, "Isolation", null, surname + Letters(6), Gender.Female, new DateOnly(1994, 4, 4), null, null, null, null, null, null, null,
        EmploymentType.Permanent, EmployeeStatus.Active, new DateOnly(2025, 6, 1), null, null, null, null, null, null, null,
        IsAttendanceExempt: true, SalaryPaymentMode.Cash, null, null, null, null, null, null, null, null);

    private static LeaveRequest Leave(Guid facility, Guid employee, Guid leaveType, DateOnly day) => new()
    {
        FacilityId = facility,
        EmployeeId = employee,
        LeaveTypeId = leaveType,
        FromDate = day,
        FromSession = LeaveSession.FirstHalf,
        ToDate = day,
        ToSession = LeaveSession.SecondHalf,
        Days = 1,
        Reason = "Family function",
        Status = ApprovalStatus.Pending,
        AppliedAt = DateTimeOffset.UtcNow,
    };

    private static AttendanceRegularisation Regularisation(Guid facility, Guid employee, DateOnly day) => new()
    {
        FacilityId = facility,
        EmployeeId = employee,
        Date = day,
        Kind = RegularisationKind.MissedPunch,
        RequestedIn = new TimeOnly(9, 0),
        RequestedOut = new TimeOnly(17, 0),
        Reason = "Forgot to punch",
        Status = ApprovalStatus.Pending,
    };

    /// <summary>
    /// A processed (not approved) run: the payroll screens open its payslips, and it closes no month. An approved run
    /// would close every month up to it to new salary structures at the facility, for every other test.
    /// </summary>
    private static PayrollRun Run(Guid facility, int month) => new()
    {
        FacilityId = facility,
        Year = 2099,
        Month = month,
        Status = PayrollRunStatus.Processed,
        ProcessedAt = DateTimeOffset.UtcNow,
    };

    private static Payslip Slip(PayrollRun run, EmployeeCreated employee) => new()
    {
        FacilityId = run.FacilityId,
        PayrollRunId = run.Id,
        EmployeeId = employee.Id,
        Year = run.Year,
        Month = run.Month,
        EmployeeCode = employee.EmployeeCode,
        EmployeeName = "Isolation " + employee.EmployeeCode,
        DaysInMonth = 30,
        PayableDays = 30,
        GrossEarnings = 30000m,
        NetPay = 30000m,
        PaymentMode = SalaryPaymentMode.Cash,
    };

    private static DataRequest Request(Guid facility)
    {
        var request = new DataRequest
        {
            FacilityId = facility,
            RequestNumber = "DPR" + Letters(9).ToUpperInvariant(),
            Type = DataRequestType.Access,
            Channel = DataRequestChannel.FrontDesk,
            PrincipalName = "Isolation " + Letters(5),
            RequestedBy = RequesterRelation.Self,
            Details = "A copy of my records",
        };
        request.Receive(DateTimeOffset.UtcNow, 30, null, "test");
        return request;
    }

    private static Capa CorrectiveAction(Guid facility, Guid owner) => new()
    {
        FacilityId = facility,
        CapaNumber = "CAPA" + Letters(8).ToUpperInvariant(),
        Source = CapaSource.Audit,
        Type = CapaType.Corrective,
        Action = "Recalibrate the infusion pumps",
        OwnerUserId = owner,
        DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
        Status = CapaStatus.Open,
    };

    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(Ist).DateTime);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);

    private sealed record ReportRows(IReadOnlyList<object?[]> Rows);
}
