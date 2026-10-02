using HyFib.HealthCare360.Modules.HR.Features.Attendance;
using HyFib.HealthCare360.Modules.HR.Features.Credentials;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Modules.HR.Features.Leave;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Modules.HR.Features.Rostering;
using HyFib.HealthCare360.Modules.HR.Features.SelfService;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.HR;

public static class HRPermissions
{
    public const string EmployeesRead = "hr.employees.read";
    public const string EmployeesManage = "hr.employees.manage";
    public const string AttendanceManage = "hr.attendance.manage";
    public const string RosterManage = "hr.roster.manage";
    public const string LeaveApprove = "hr.leave.approve";
    public const string PayrollProcess = "hr.payroll.process";
    public const string PayrollApprove = "hr.payroll.approve";
    public const string CredentialsManage = "hr.credentials.manage";

    /// <summary>Employee self-service (own profile, attendance, leave, payslips) and reporting-manager approvals.</summary>
    public const string Self = "hr.self";

    private static readonly string[] Hr = [SystemRoles.HrManager, SystemRoles.HospitalAdmin];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(EmployeesRead, "HR", "View the employee master and credentials", Hr),
        new(EmployeesManage, "HR", "Add and edit employees, bank details, designations, leave policies; separate employees", Hr),
        new(AttendanceManage, "HR", "Biometric terminals, attendance, holidays and regularisation approvals", Hr),
        new(RosterManage, "HR", "Shifts and duty rosters", Hr),
        new(LeaveApprove, "HR", "Approve leave for any employee; credit and adjust leave balances", Hr),
        new(PayrollProcess, "HR", "Prepare payroll: salary structures, inputs and processing", Hr),
        new(PayrollApprove, "HR", "Approve payroll, export the bank transfer file and edit professional-tax slabs", Hr),
        new(CredentialsManage, "HR", "Maintain staff licences and certifications; receives expiry alerts", Hr),
        new(Self, "HR", "Employee self-service: own profile, attendance, leave and payslips", PlatformPermissions.AllStaff),
    ];
}

/// <summary>HRMS: employees, shifts, biometric attendance, leave and payroll (SRS HR-01..08, INT-BIO).</summary>
public sealed class HRModule : IModule
{
    public string Name => "hr";

    public IEnumerable<PermissionDefinition> Permissions => HRPermissions.All;

    public IEnumerable<NumberSeriesDefinition> NumberSeries => HRNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<EmployeeService>();
        services.AddScoped<CurrentEmployee>();
        services.AddScoped<HrApprovers>();
        services.AddScoped<RosterService>();
        services.AddScoped<BiometricIngestionService>();
        services.AddScoped<AttendanceService>();
        services.AddScoped<RegularisationService>();
        services.AddScoped<LeaveService>();
        services.AddScoped<PayrollService>();
        services.AddScoped<PayrollInputService>();

        // Cross-module lookup (canteen staff credit and similar); identification only.
        services.AddScoped<IEmployeeDirectory, EmployeeDirectory>();

        // Both jobs return immediately on API-only nodes (PlatformOptions.RunsJobs).
        services.AddHostedService<AttendanceComputationJob>();
        services.AddHostedService<CredentialExpiryAlertJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        EmployeeEndpoints.Map(endpoints);
        DesignationEndpoints.Map(endpoints);
        CredentialEndpoints.Map(endpoints);
        RosterEndpoints.Map(endpoints);
        AttendanceEndpoints.Map(endpoints);
        BiometricDeviceEndpoints.Map(endpoints);
        AdmsEndpoints.Map(endpoints);
        LeaveEndpoints.Map(endpoints);
        PayrollSetupEndpoints.Map(endpoints);
        PayrollEndpoints.Map(endpoints);
        SelfServiceEndpoints.Map(endpoints);
    }
}
