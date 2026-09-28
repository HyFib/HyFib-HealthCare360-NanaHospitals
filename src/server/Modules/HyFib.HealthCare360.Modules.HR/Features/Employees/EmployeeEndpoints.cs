using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Features.Employees;

/// <summary>/api/v1/hr/employees — employee master (HR-01/02).</summary>
internal static class EmployeeEndpoints
{
    private static readonly Dictionary<string, Expression<Func<EmployeeRow, object?>>> SortKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = e => e.EmployeeCode,
        ["name"] = e => e.FullName,
        ["joiningDate"] = e => e.JoiningDate,
        ["department"] = e => e.DepartmentName,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr/employees").WithTags("HR");

        group.MapGet("/", List).RequirePermission(HRPermissions.EmployeesRead);
        group.MapGet("/{id:guid}", Get).RequirePermission(HRPermissions.EmployeesRead);
        group.MapPost("/", Create).RequirePermission(HRPermissions.EmployeesManage).WithValidation<SaveEmployeeRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(HRPermissions.EmployeesManage).WithValidation<SaveEmployeeRequest>();
        group.MapPut("/{id:guid}/bank-details", UpdateBank).RequirePermission(HRPermissions.EmployeesManage).WithValidation<UpdateBankDetailsRequest>();
        group.MapPost("/{id:guid}/separate", Separate).RequirePermission(HRPermissions.EmployeesManage).WithValidation<SeparateEmployeeRequest>();
    }

    /// <summary>Query row built with an object initializer so EF can still sort on it after the joins.</summary>
    private sealed class EmployeeRow
    {
        public Guid Id { get; init; }
        public string EmployeeCode { get; init; } = string.Empty;
        public string FullName { get; init; } = string.Empty;
        public Gender Gender { get; init; }
        public Guid? DepartmentId { get; init; }
        public string? DepartmentName { get; init; }
        public Guid? DesignationId { get; init; }
        public string? DesignationName { get; init; }
        public EmploymentType EmploymentType { get; init; }
        public EmployeeStatus Status { get; init; }
        public DateOnly JoiningDate { get; init; }
        public string? Phone { get; init; }
        public Guid? UserId { get; init; }
        public string? BiometricPin { get; init; }
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        Guid? departmentId,
        Guid? designationId,
        EmployeeStatus? status,
        EmploymentType? employmentType,
        bool? includeSeparated,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var employees = db.Set<Employee>().AsNoTracking();
        if (status is not null)
        {
            employees = employees.Where(e => e.Status == status);
        }
        else if (includeSeparated != true)
        {
            employees = employees.Where(e => e.Status != EmployeeStatus.Separated);
        }

        if (departmentId is not null)
        {
            employees = employees.Where(e => e.DepartmentId == departmentId);
        }

        if (designationId is not null)
        {
            employees = employees.Where(e => e.DesignationId == designationId);
        }

        if (employmentType is not null)
        {
            employees = employees.Where(e => e.EmploymentType == employmentType);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            var code = term.ToUpperInvariant();
            var phone = Phone.Normalize(term);
            employees = employees.Where(e => EF.Functions.ILike(e.FullName, like) || e.EmployeeCode == code || e.EmployeeCode.EndsWith(code)
                                             || (phone != null && e.Phone == phone) || e.BiometricPin == term);
        }

        var rows = from e in employees
                   join d in db.Set<Department>() on e.DepartmentId equals d.Id into departments
                   from d in departments.DefaultIfEmpty()
                   join g in db.Set<Designation>() on e.DesignationId equals g.Id into designations
                   from g in designations.DefaultIfEmpty()
                   select new EmployeeRow
                   {
                       Id = e.Id,
                       EmployeeCode = e.EmployeeCode,
                       FullName = e.FullName,
                       Gender = e.Gender,
                       DepartmentId = e.DepartmentId,
                       DepartmentName = d != null ? d.Name : null,
                       DesignationId = e.DesignationId,
                       DesignationName = g != null ? g.Name : null,
                       EmploymentType = e.EmploymentType,
                       Status = e.Status,
                       JoiningDate = e.JoiningDate,
                       Phone = e.Phone,
                       UserId = e.UserId,
                       BiometricPin = e.BiometricPin,
                   };

        var result = await rows.OrderByKey(page, SortKeys, e => e.FullName).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(e => new EmployeeListItem(
            e.Id, e.EmployeeCode, e.FullName, e.Gender, e.DepartmentId, e.DepartmentName, e.DesignationId, e.DesignationName, e.EmploymentType,
            e.Status, e.JoiningDate, Phone.Mask(e.Phone), e.UserId != null, e.BiometricPin)));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var e = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
        {
            return Error.NotFound(nameof(Employee), id).ToProblem();
        }

        var department = e.DepartmentId is { } departmentId ? await db.Set<Department>().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct) : null;
        var designation = e.DesignationId is { } designationId ? await db.Set<Designation>().Where(d => d.Id == designationId).Select(d => d.Name).FirstOrDefaultAsync(ct) : null;
        var manager = e.ReportingManagerId is { } managerId ? await db.Set<Employee>().Where(m => m.Id == managerId).Select(m => m.FullName).FirstOrDefaultAsync(ct) : null;

        return TypedResults.Ok(new EmployeeDetail(
            e.Id, e.EmployeeCode, e.Title, e.FirstName, e.MiddleName, e.LastName, e.FullName, e.Gender, e.DateOfBirth, e.Phone, e.Email, e.Address,
            e.EmergencyContactName, e.EmergencyContactPhone, e.DepartmentId, department, e.DesignationId, designation, e.EmploymentType, e.Status,
            e.JoiningDate, e.ConfirmationDate, e.LeavingDate, e.SeparationReason, e.SeparationNote, e.ReportingManagerId, manager, e.UserId,
            e.PractitionerId, e.BiometricPin, e.DefaultShiftId, e.WeeklyOff, e.IsAttendanceExempt, e.PaymentMode, e.BankName, e.BankAccountMasked,
            e.BankIfscMasked, e.PanMasked, e.Uan, e.PfNumber, e.EsiNumber, e.PhotoFileId, e.Notes, e.Version));
    }

    private static async Task<IResult> Create(SaveEmployeeRequest request, EmployeeService employees, CancellationToken ct) =>
        (await employees.CreateAsync(request, ct)).ToCreated(e => $"/api/v1/hr/employees/{e.Id}");

    private static async Task<IResult> Update(Guid id, SaveEmployeeRequest request, EmployeeService employees, CancellationToken ct) =>
        (await employees.UpdateAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> UpdateBank(Guid id, UpdateBankDetailsRequest request, EmployeeService employees, CancellationToken ct) =>
        (await employees.UpdateBankDetailsAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> Separate(Guid id, SeparateEmployeeRequest request, EmployeeService employees, CancellationToken ct) =>
        (await employees.SeparateAsync(id, request, ct)).ToNoContent();
}
