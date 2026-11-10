using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Salary structures (HR-06): a revision creates a new structure and closes the one in force the day before it starts, so
/// every past payslip can be reproduced. A revision is asked for and approved by someone else (<see cref="PayApprovals"/>);
/// this checks it when it is asked and again when it is made.
/// </summary>
public sealed class SalaryStructureService(HospitalDbContext db)
{
    /// <summary>The employee a revision is for, if it can be made as asked: their facility, and the latest structure's start.</summary>
    public async Task<Result<(Guid FacilityId, DateOnly? LatestFrom)>> CheckAsync(Guid employeeId, SaveSalaryStructureRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == employeeId).Select(e => new { e.FacilityId, e.Status }).FirstOrDefaultAsync(ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        if (employee.Status == EmployeeStatus.Separated)
        {
            return Error.Rule("employee.separated", "Salary structures cannot be added for separated employees.");
        }

        var componentIds = request.Lines.Select(l => l.SalaryComponentId).ToList();
        var components = await db.Set<SalaryComponent>().AsNoTracking().Where(c => componentIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        if (components.Count != componentIds.Count || components.Values.Any(c => c.IsStatutory || !c.IsActive))
        {
            return Error.Field("lines", "Use active, non-statutory pay heads only (PF, ESI, PT and TDS are computed).");
        }

        if (!components.Values.Any(c => c.Code == SalaryComponent.BasicCode))
        {
            return Error.Field("lines", "A salary structure needs a BASIC pay head.");
        }

        if (request.Lines.Any(l => components[l.SalaryComponentId].CalculationType == SalaryCalculationType.PercentOfBasic && l.Value > 100))
        {
            return Error.Field("lines", "A percentage of basic cannot exceed 100.");
        }

        var locked = await db.Set<PayrollRun>().AnyAsync(r => r.FacilityId == employee.FacilityId && r.Status != PayrollRunStatus.Draft
                                                          && r.Status != PayrollRunStatus.Processed
                                                          && (r.Year * 100) + r.Month >= (request.EffectiveFrom.Year * 100) + request.EffectiveFrom.Month, ct);
        if (locked)
        {
            return Error.Rule("salary_structure.month_closed", "Payroll for that month is already approved; revisions start from the next open month.");
        }

        var latest = await db.Set<EmployeeSalaryStructure>().Where(s => s.EmployeeId == employeeId).MaxAsync(s => (DateOnly?)s.EffectiveFrom, ct);
        if (latest >= request.EffectiveFrom)
        {
            return Error.Conflict("salary_structure.overlap", "A structure already starts on or after this date.");
        }

        return (employee.FacilityId, latest);
    }

    /// <summary>Adds the structure and closes the one in force the day before; the caller saves.</summary>
    public async Task<EmployeeSalaryStructure> AddAsync(Guid employeeId, Guid facilityId, SaveSalaryStructureRequest request, CancellationToken ct)
    {
        var existing = await db.Set<EmployeeSalaryStructure>().Where(s => s.EmployeeId == employeeId).ToListAsync(ct);
        foreach (var open in existing.Where(s => s.EffectiveTo is null || s.EffectiveTo >= request.EffectiveFrom))
        {
            open.EffectiveTo = request.EffectiveFrom.AddDays(-1);
        }

        var structure = new EmployeeSalaryStructure
        {
            FacilityId = facilityId,
            EmployeeId = employeeId,
            EffectiveFrom = request.EffectiveFrom,
            AnnualCtc = request.AnnualCtc,
            MonthlyTds = Money.Round(request.MonthlyTds),
            PfApplicable = request.PfApplicable,
            PfOnActualBasic = request.PfOnActualBasic,
            EsiApplicable = request.EsiApplicable,
            ProfessionalTaxState = string.IsNullOrWhiteSpace(request.ProfessionalTaxState) ? null : request.ProfessionalTaxState.Trim().ToUpperInvariant(),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        };
        structure.Lines.AddRange(request.Lines.Select(l => new SalaryStructureLine { StructureId = structure.Id, SalaryComponentId = l.SalaryComponentId, Value = l.Value }));
        db.Add(structure);
        return structure;
    }

    /// <summary>"from 01 Apr 2026: BASIC ₹30,000.00, HRA 40% of basic…; TDS ₹1,200.00 a month", for the checker and the audit trail.</summary>
    public async Task<string> DescribeAsync(SaveSalaryStructureRequest request, CancellationToken ct)
    {
        var ids = request.Lines.Select(l => l.SalaryComponentId).ToList();
        var components = await db.Set<SalaryComponent>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var lines = request.Lines
            .Where(l => components.ContainsKey(l.SalaryComponentId))
            .Select(l =>
            {
                var c = components[l.SalaryComponentId];
                return c.CalculationType == SalaryCalculationType.PercentOfBasic ? $"{c.Code} {l.Value:0.##}% of basic" : $"{c.Code} ₹{Money.FormatInr(l.Value)}";
            });
        return $"from {request.EffectiveFrom:dd MMM yyyy}: {string.Join(", ", lines)}; TDS ₹{Money.FormatInr(request.MonthlyTds)} a month";
    }
}
