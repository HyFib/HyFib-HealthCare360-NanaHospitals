using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Features.Payroll;

/// <summary>Payroll set-up: pay heads, state professional-tax slabs and employee salary structures (HR-06).</summary>
internal static class PayrollSetupEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr").WithTags("HR");
        string[] viewers = [HRPermissions.PayrollProcess, HRPermissions.PayrollApprove];

        group.MapGet("/payroll/components", ListComponents).RequireAnyPermission(viewers);
        group.MapPost("/payroll/components", (SaveSalaryComponentRequest r, HospitalDbContext db, CancellationToken ct) => SaveComponent(null, r, db, ct))
            .RequirePermission(HRPermissions.PayrollProcess).WithValidation<SaveSalaryComponentRequest>();
        group.MapPut("/payroll/components/{id:guid}", (Guid id, SaveSalaryComponentRequest r, HospitalDbContext db, CancellationToken ct) => SaveComponent(id, r, db, ct))
            .RequirePermission(HRPermissions.PayrollProcess).WithValidation<SaveSalaryComponentRequest>().RequireVersion();

        group.MapGet("/payroll/professional-tax", ListProfessionalTax).RequireAnyPermission(viewers);
        group.MapPut("/payroll/professional-tax/{stateCode}", SaveProfessionalTax)
            .RequirePermission(HRPermissions.PayrollApprove).WithValidation<SaveProfessionalTaxRequest>().FirstSaveCreates();

        group.MapGet("/employees/{employeeId:guid}/salary-structures", ListStructures).RequireAnyPermission(viewers);
        group.MapPost("/employees/{employeeId:guid}/salary-structures", CreateStructure)
            .RequirePermission(HRPermissions.PayrollProcess).WithValidation<SaveSalaryStructureRequest>();
    }

    // Enums are stored as text, so ordering by Kind in SQL would be alphabetical (deductions first); rank explicitly.
    private static async Task<IResult> ListComponents(HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<SalaryComponent>().AsNoTracking()
            .OrderBy(c => c.Kind == SalaryComponentKind.Deduction).ThenBy(c => c.SortOrder).ThenBy(c => c.Code)
            .Select(c => new SalaryComponentDto(c.Id, c.Code, c.Name, c.Kind, c.CalculationType, c.DefaultValue, c.IsTaxable, c.WageTreatment, c.IsStatutory, c.SortOrder, c.IsActive, c.Version))
            .ToListAsync(ct));

    private static async Task<IResult> SaveComponent(Guid? id, SaveSalaryComponentRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<SalaryComponent>().AnyAsync(c => c.Code == code && c.Id != id, ct))
        {
            return Error.Conflict("salary_component.code_in_use", $"Pay head {code} already exists.").ToProblem();
        }

        SalaryComponent component;
        if (id is { } existingId)
        {
            var found = await db.Set<SalaryComponent>().FirstOrDefaultAsync(c => c.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(SalaryComponent), existingId).ToProblem();
            }

            if (found.IsStatutory)
            {
                return Error.Rule("salary_component.statutory", "Statutory pay heads are maintained by the payroll engine.").ToProblem();
            }

            if (found.Code == SalaryComponent.BasicCode && (code != SalaryComponent.BasicCode || request.CalculationType != SalaryCalculationType.Fixed
                                                            || request.WageTreatment is not (null or WageTreatment.Wages)))
            {
                return Error.Rule("salary_component.basic", "BASIC must stay a fixed earning counted in wages; statutory deductions are computed from it.").ToProblem();
            }

            // Payroll inputs name their pay head by its code: changed, theirs would no longer be found, and an earning would
            // count as wages whatever its pay head says.
            if (found.Code != code && await db.Set<PayrollInput>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(i => i.ComponentCode == found.Code, ct))
            {
                return Error.Rule("salary_component.code_used",
                    $"Pay head {found.Code} has payroll inputs, which name it by its code: keep the code and change the name, or add a new pay head.").ToProblem();
            }

            component = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(component, version);
            }
        }
        else
        {
            component = new SalaryComponent();
            db.Add(component);
        }

        component.Code = code;
        component.Name = request.Name.Trim();
        component.Kind = request.Kind;
        component.CalculationType = request.CalculationType;
        component.DefaultValue = request.DefaultValue;
        component.IsTaxable = request.IsTaxable;
        component.WageTreatment = request.WageTreatment ?? component.WageTreatment;
        component.SortOrder = request.SortOrder;
        component.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/hr/payroll/components/{component.Id}", new { component.Id }) : TypedResults.NoContent();
    }

    private static async Task<IResult> ListProfessionalTax(HospitalDbContext db, CancellationToken ct)
    {
        var slabs = await db.Set<ProfessionalTaxSlab>().AsNoTracking().OrderBy(s => s.StateCode).ThenBy(s => s.IncomeFrom).ToListAsync(ct);
        return TypedResults.Ok(slabs.GroupBy(s => s.StateCode).Select(g => new ProfessionalTaxScheduleDto(
            g.Key, g.First().StateName, g.First().Frequency, g.First().DeductionMonths,
            g.Select(s => new ProfessionalTaxBandDto(s.IncomeFrom, s.IncomeTo, s.Amount, s.FebruaryAmount)).ToList(), g.Max(s => s.Version))).ToList());
    }

    private static async Task<IResult> SaveProfessionalTax(string stateCode, SaveProfessionalTaxRequest request, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var code = stateCode.Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 4 || !code.All(char.IsAsciiLetterUpper))
        {
            return Error.Field("stateCode", "Use the state code, for example TN or KA.").ToProblem();
        }

        // One save writes all of a state's slabs, so any of them carries the version of the table (REL-04). Removing them
        // checks it again, so a save that raced this one is refused too.
        var slabs = await db.Set<ProfessionalTaxSlab>().Where(s => s.StateCode == code).ToListAsync(ct);
        if ((slabs.Count == 0 ? null : slabs.Max(s => (uint?)s.Version)) != request.Version)
        {
            return Error.ConcurrencyConflict.ToProblem();
        }

        db.RemoveRange(slabs);
        var months = request.Frequency == ProfessionalTaxFrequency.HalfYearly ? request.DeductionMonths.Distinct().Order().ToList() : [];
        foreach (var band in request.Bands.OrderBy(b => b.IncomeFrom))
        {
            db.Add(new ProfessionalTaxSlab
            {
                StateCode = code,
                StateName = request.StateName.Trim(),
                Frequency = request.Frequency,
                DeductionMonths = months,
                IncomeFrom = band.IncomeFrom,
                IncomeTo = band.IncomeTo,
                Amount = band.Amount,
                FebruaryAmount = band.FebruaryAmount,
            });
        }

        audit.Add(new AuditEntry(AuditCategories.Admin, "payroll.professional_tax_changed", $"Professional tax slabs for {code} replaced ({request.Bands.Count} bands)")
        {
            EntityType = nameof(ProfessionalTaxSlab),
            EntityId = code,
            Details = request,
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListStructures(Guid employeeId, HospitalDbContext db, CancellationToken ct)
    {
        var structures = await db.Set<EmployeeSalaryStructure>().AsNoTracking().Include(s => s.Lines)
            .Where(s => s.EmployeeId == employeeId)
            .OrderByDescending(s => s.EffectiveFrom)
            .ToListAsync(ct);
        var components = await db.Set<SalaryComponent>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        return TypedResults.Ok(structures.Select(s => ToDto(s, components)).ToList());
    }

    /// <summary>A new structure closes the one in force the day before it starts (salary revisions keep history).</summary>
    private static async Task<IResult> CreateStructure(Guid employeeId, SaveSalaryStructureRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == employeeId).Select(e => new { e.FacilityId, e.JoiningDate, e.Status }).FirstOrDefaultAsync(ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId).ToProblem();
        }

        if (employee.Status == EmployeeStatus.Separated)
        {
            return Error.Rule("employee.separated", "Salary structures cannot be added for separated employees.").ToProblem();
        }

        var componentIds = request.Lines.Select(l => l.SalaryComponentId).ToList();
        var components = await db.Set<SalaryComponent>().AsNoTracking().Where(c => componentIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        if (components.Count != componentIds.Count || components.Values.Any(c => c.IsStatutory || !c.IsActive))
        {
            return Error.Field("lines", "Use active, non-statutory pay heads only (PF, ESI, PT and TDS are computed).").ToProblem();
        }

        if (!components.Values.Any(c => c.Code == SalaryComponent.BasicCode))
        {
            return Error.Field("lines", "A salary structure needs a BASIC pay head.").ToProblem();
        }

        if (request.Lines.Any(l => components[l.SalaryComponentId].CalculationType == SalaryCalculationType.PercentOfBasic && l.Value > 100))
        {
            return Error.Field("lines", "A percentage of basic cannot exceed 100.").ToProblem();
        }

        var locked = await db.Set<PayrollRun>().AnyAsync(r => r.FacilityId == employee.FacilityId && r.Status != PayrollRunStatus.Draft
                                                          && r.Status != PayrollRunStatus.Processed
                                                          && (r.Year * 100) + r.Month >= (request.EffectiveFrom.Year * 100) + request.EffectiveFrom.Month, ct);
        if (locked)
        {
            return Error.Rule("salary_structure.month_closed", "Payroll for that month is already approved; revisions start from the next open month.").ToProblem();
        }

        var existing = await db.Set<EmployeeSalaryStructure>().Where(s => s.EmployeeId == employeeId).ToListAsync(ct);
        if (existing.Any(s => s.EffectiveFrom >= request.EffectiveFrom))
        {
            return Error.Conflict("salary_structure.overlap", "A structure already starts on or after this date.").ToProblem();
        }

        foreach (var open in existing.Where(s => s.EffectiveTo is null || s.EffectiveTo >= request.EffectiveFrom))
        {
            open.EffectiveTo = request.EffectiveFrom.AddDays(-1);
        }

        var structure = new EmployeeSalaryStructure
        {
            FacilityId = employee.FacilityId,
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
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/hr/employees/{employeeId}/salary-structures", new { structure.Id });
    }

    internal static SalaryStructureDto ToDto(EmployeeSalaryStructure s, IReadOnlyDictionary<Guid, SalaryComponent> components)
    {
        var basic = s.Lines.Where(l => components.TryGetValue(l.SalaryComponentId, out var c) && c.Code == SalaryComponent.BasicCode).Sum(l => l.Value);
        var lines = s.Lines
            .Where(l => components.ContainsKey(l.SalaryComponentId))
            .Select(l =>
            {
                var c = components[l.SalaryComponentId];
                var monthly = c.CalculationType == SalaryCalculationType.PercentOfBasic ? Money.PercentOf(basic, l.Value) : l.Value;
                return new SalaryStructureLineDto(c.Id, c.Code, c.Name, c.Kind, c.CalculationType, l.Value, monthly);
            })
            .OrderBy(l => l.Kind).ThenBy(l => components[l.SalaryComponentId].SortOrder)
            .ToList();
        return new SalaryStructureDto(s.Id, s.EmployeeId, s.EffectiveFrom, s.EffectiveTo, s.AnnualCtc, s.MonthlyTds, s.PfApplicable, s.PfOnActualBasic,
            s.EsiApplicable, s.ProfessionalTaxState, s.Notes, lines.Where(l => l.Kind == SalaryComponentKind.Earning).Sum(l => l.MonthlyAmount), lines);
    }
}
