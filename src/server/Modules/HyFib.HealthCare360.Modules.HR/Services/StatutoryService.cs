using System.Text;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;
using HyFib.HealthCare360.Modules.HR.Features.Statutory;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Statutory returns and settlements from HC360's own payroll: the EPFO ECR and ESIC file of an approved month, TDS on
/// salary under the employee's regime, Form 24Q's FVU input file and Form 16 Part B, gratuity, statutory bonus, full and
/// final settlement and the banks' salary upload files. Files holding UANs, PANs or account numbers are made on demand,
/// never stored, and every one is written to the audit trail.
/// </summary>
public sealed class StatutoryService(HospitalDbContext db, ICurrentUser currentUser, IFieldProtector protector, IAuditLogger audit, PayApprovals pay, TimeProvider time)
{
    public const string SalarySection = "92B";

    // ---- Employer profile -------------------------------------------------------------------------------------------

    /// <summary>The facility's details; an empty profile (no version) until the first save.</summary>
    public async Task<StatutoryProfileDto> ProfileAsync(CancellationToken ct)
    {
        var p = await db.Set<StatutoryEmployerProfile>().AsNoTracking().FirstOrDefaultAsync(ct);
        return p is null
            ? new StatutoryProfileDto(null, null, null, null, "K", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, BankFileFormat.Csv, null)
            : ToDto(p);
    }

    public async Task<Result> SaveProfileAsync(SaveStatutoryProfileRequest r, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var p = await db.Set<StatutoryEmployerProfile>().FirstOrDefaultAsync(ct);
        if (p is null)
        {
            p = new StatutoryEmployerProfile { FacilityId = facilityId };
            db.Add(p);
        }

        db.SetExpectedVersion(p, r.Version);
        p.Tan = HrIdentifiers.Normalize(r.Tan);
        p.Pan = HrIdentifiers.Normalize(r.Pan);
        p.DeductorName = r.DeductorName.Trim();
        p.Branch = Clean(r.Branch);
        p.DeductorType = r.DeductorType.Trim();
        p.AddressLine1 = r.AddressLine1.Trim();
        p.AddressLine2 = Clean(r.AddressLine2);
        p.AddressLine3 = Clean(r.AddressLine3);
        p.City = r.City.Trim();
        p.StateCode = r.StateCode;
        p.Pin = r.Pin;
        p.Email = Clean(r.Email);
        p.Phone = Clean(r.Phone);
        p.ResponsibleName = r.ResponsibleName.Trim();
        p.ResponsibleDesignation = r.ResponsibleDesignation.Trim();
        p.ResponsiblePan = string.IsNullOrWhiteSpace(r.ResponsiblePan) ? null : HrIdentifiers.Normalize(r.ResponsiblePan);
        p.ResponsibleMobile = Clean(r.ResponsibleMobile);
        p.ResponsibleEmail = Clean(r.ResponsibleEmail);
        p.EpfEstablishmentCode = Clean(r.EpfEstablishmentCode)?.ToUpperInvariant();
        p.EsicEmployerCode = Clean(r.EsicEmployerCode);
        if (!string.IsNullOrWhiteSpace(r.SalaryDebitAccount))
        {
            p.SalaryDebitAccount = HrIdentifiers.Normalize(r.SalaryDebitAccount);
        }

        p.SalaryBank = r.SalaryBank;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Challans -----------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<TdsChallanDto>> ChallansAsync(TdsForm? form, int? financialYear, CancellationToken ct)
    {
        var query = db.Set<TdsChallan>().AsNoTracking().Where(c => form == null || c.Form == form);
        if (financialYear is { } fy)
        {
            query = query.Where(c => (c.Year * 100) + c.Month >= (fy * 100) + 4 && (c.Year * 100) + c.Month <= ((fy + 1) * 100) + 3);
        }

        return (await query.OrderBy(c => c.Year).ThenBy(c => c.Month).ThenBy(c => c.DepositedOn).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<Result<Guid>> AddChallanAsync(SaveTdsChallanRequest r, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (await db.Set<TdsChallan>().AnyAsync(c => c.BsrCode == r.BsrCode && c.DepositedOn == r.DepositedOn && c.ChallanSerial == r.ChallanSerial, ct))
        {
            return Error.Conflict("tds_challan.exists", "This challan (BSR code, date and serial number) is already recorded.");
        }

        var challan = new TdsChallan
        {
            FacilityId = facilityId,
            Form = r.Form,
            Year = r.Year,
            Month = r.Month,
            BsrCode = r.BsrCode,
            DepositedOn = r.DepositedOn,
            ChallanSerial = r.ChallanSerial,
            Tax = r.Tax,
            Surcharge = r.Surcharge,
            Cess = r.Cess,
            Interest = r.Interest,
            Others = r.Others,
            LateFee = r.LateFee,
            MinorHead = r.MinorHead ?? "200",
        };
        db.Add(challan);
        await db.SaveChangesAsync(ct);
        return challan.Id;
    }

    public async Task<Result> RemoveChallanAsync(Guid id, CancellationToken ct)
    {
        var challan = await db.Set<TdsChallan>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (challan is null)
        {
            return Error.NotFound(nameof(TdsChallan), id);
        }

        db.Remove(challan);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Declarations and computation ---------------------------------------------------------------------------------

    public async Task<TaxDeclarationDto?> DeclarationAsync(Guid employeeId, int financialYear, CancellationToken ct)
    {
        var d = await db.Set<TaxDeclaration>().AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == employeeId && x.FinancialYear == financialYear, ct);
        return d is null ? null : ToDto(d);
    }

    public async Task<Result> SaveDeclarationAsync(Guid employeeId, int financialYear, SaveTaxDeclarationRequest r, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        if (IncomeTaxRates.Find(financialYear) is null)
        {
            return Error.Rule("tax.year_unknown", $"Income-tax rates for {financialYear}-{(financialYear + 1) % 100:00} are not set up.");
        }

        // A month already approved keeps the TDS it paid; the change applies from the next payroll.
        var d = await db.Set<TaxDeclaration>().FirstOrDefaultAsync(x => x.EmployeeId == employeeId && x.FinancialYear == financialYear, ct);
        if (d is null)
        {
            d = new TaxDeclaration { FacilityId = employee.FacilityId, EmployeeId = employeeId, FinancialYear = financialYear };
            db.Add(d);
        }

        db.SetExpectedVersion(d, r.Version);
        d.Regime = r.Regime;
        d.RentPaid = r.RentPaid;
        d.MetroCity = r.MetroCity;
        d.Section80C = r.Section80C;
        d.Section80Ccd1B = r.Section80Ccd1B;
        d.Section80DSelf = r.Section80DSelf;
        d.Section80DParents = r.Section80DParents;
        d.ParentsSenior = r.ParentsSenior;
        d.Section80E = r.Section80E;
        d.Section80G = r.Section80G;
        d.Section80Tta = r.Section80Tta;
        d.HousingLoanInterest = r.HousingLoanInterest;
        d.OtherIncome = r.OtherIncome;
        d.PreviousEmployerSalary = r.PreviousEmployerSalary;
        d.PreviousEmployerTds = r.PreviousEmployerTds;
        d.Notes = Clean(r.Notes);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// The year's tax under both regimes: salary on this year's payslips, and the salary structure in force for each month
    /// still to come (to March, or the last working day).
    /// </summary>
    public async Task<Result<TaxComputationDto>> ComputationAsync(Guid employeeId, int financialYear, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        if (IncomeTaxRates.Find(financialYear) is not { } rates)
        {
            return Error.Rule("tax.year_unknown", $"Income-tax rates for {financialYear}-{(financialYear + 1) % 100:00} are not set up.");
        }

        var annual = await AnnualSalaryAsync(employee, financialYear, ct);
        var declaration = await db.Set<TaxDeclaration>().AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == employeeId && x.FinancialYear == financialYear, ct);
        var regime = declaration?.Regime ?? TaxRegime.New;
        var amounts = declaration?.ToAmounts() ?? TaxDeclarationAmounts.None;
        var salary = new AnnualSalary(annual.Salary.Gross, annual.Salary.Basic, annual.Salary.HouseRentAllowance, annual.Salary.EmployeePf, annual.Salary.ProfessionalTax);
        var chosen = IncomeTaxCalculator.Compute(rates, regime, salary, amounts, employee.DateOfBirth);
        var other = IncomeTaxCalculator.Compute(rates, regime == TaxRegime.New ? TaxRegime.Old : TaxRegime.New, salary, amounts, employee.DateOfBirth);
        return new TaxComputationDto(financialYear, regime, annual.MonthsPaid, annual.MonthsProjected, annual.TdsDeducted, chosen, other, rates.Source);
    }

    private sealed record AnnualFigures(MonthSalary Salary, int MonthsPaid, int MonthsProjected, decimal TdsDeducted);

    private async Task<AnnualFigures> AnnualSalaryAsync(Employee employee, int fy, CancellationToken ct)
    {
        var ytd = (await PayrollTaxContext.YearToDateAsync(db, [employee.Id], (fy * 100) + 4, ((fy + 1) * 100) + 4, ct)).GetValueOrDefault(employee.Id) ?? YearToDate.None;
        var paidKeys = await db.Set<Payslip>().AsNoTracking()
            .Where(p => p.EmployeeId == employee.Id && (p.Year * 100) + p.Month >= (fy * 100) + 4 && (p.Year * 100) + p.Month <= ((fy + 1) * 100) + 3)
            .Select(p => (p.Year * 100) + p.Month)
            .ToListAsync(ct);
        var lastPaid = paidKeys.Count == 0 ? (fy * 100) + 3 : paidKeys.Max();
        var (lastYear, lastMonth) = (lastPaid / 100, lastPaid % 100);
        var projected = PayrollTaxContext.MonthsLeftAfter(lastYear, lastMonth, employee.LeavingDate);
        if (paidKeys.Count == 0)
        {
            projected = Math.Min(12, projected);
        }

        var salary = ytd.ToSalary();
        var structure = await CurrentStructureAsync(employee.Id, ct);
        if (structure is not null && projected > 0)
        {
            var full = await StructureMonthAsync(structure, ct);
            salary += new MonthSalary(full.Gross, full.Basic, full.HouseRent, ProvidentFund.EmployeeContribution(
                structure.PfApplicable ? ProvidentFund.Wage(full.PfWages, structure.PfOnActualBasic) : 0), 0).Times(projected);
        }

        return new AnnualFigures(salary, paidKeys.Count, structure is null ? 0 : projected, ytd.Tds);
    }

    // ---- ECR and ESIC -----------------------------------------------------------------------------------------------

    public async Task<Result<(string FileName, byte[] Content, EcrResult Ecr)>> EcrAsync(Guid runId, CancellationToken ct)
    {
        var run = await ApprovedRunAsync(runId, ct);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        var members = await (
                from p in db.Set<Payslip>().AsNoTracking()
                join e in db.Set<Employee>() on p.EmployeeId equals e.Id
                where p.PayrollRunId == runId
                orderby p.EmployeeCode
                select new EcrMemberMonth(e.Uan, p.EmployeeCode, p.EmployeeName, e.DateOfBirth, p.GrossEarnings, p.PfWage, p.EmployeePf, p.EmployerPf, p.LopDays, true))
            .ToListAsync(ct);
        var ecr = EcrFile.Build(members, run.Value.Year, run.Value.Month);
        await AuditFileAsync("statutory.ecr_exported", run.Value, $"EPFO ECR for {run.Value.Month:00}/{run.Value.Year}: {ecr.Lines.Count} member(s), EPF ₹{ecr.TotalEpf}, "
                                                                 + $"EPS ₹{ecr.TotalEps}, difference ₹{ecr.TotalDifference}", ct);
        return ($"ecr-{run.Value.Year}-{run.Value.Month:00}.txt", Encoding.UTF8.GetBytes(ecr.Text), ecr);
    }

    public async Task<Result<(string FileName, byte[] Content, EsicResult Esic)>> EsicAsync(Guid runId, CancellationToken ct)
    {
        var run = await ApprovedRunAsync(runId, ct);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        var members = await (
                from p in db.Set<Payslip>().AsNoTracking()
                join e in db.Set<Employee>() on p.EmployeeId equals e.Id
                where p.PayrollRunId == runId && p.EsiWage > 0
                orderby p.EmployeeCode
                select new EsicMemberMonth(e.EsiNumber, p.EmployeeCode, p.EmployeeName, p.PayableDays, p.EsiWage, p.EmployeeEsi, p.EmployerEsi, e.LeavingDate, e.SeparationReason))
            .ToListAsync(ct);
        var esic = EsicContributionFile.Build(members, run.Value.Year, run.Value.Month);
        await AuditFileAsync("statutory.esic_exported", run.Value, $"ESIC contributions for {run.Value.Month:00}/{run.Value.Year}: {esic.Lines.Count} insured person(s), "
                                                                  + $"wages ₹{esic.TotalWages}", ct);
        return ($"esic-{run.Value.Year}-{run.Value.Month:00}.csv", Encoding.UTF8.GetBytes(esic.Csv), esic);
    }

    // ---- Form 24Q and Form 16 ---------------------------------------------------------------------------------------

    public async Task<Result<(string FileName, byte[] Content, TdsReturnResult Return)>> Form24QAsync(int fy, int quarter, CancellationToken ct)
    {
        var profile = await db.Set<StatutoryEmployerProfile>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (profile is null)
        {
            return Error.Rule("statutory.profile_missing", "Enter the employer's TAN, PAN and the person responsible under Statutory details first.");
        }

        var (from, to) = TdsReturnFile.QuarterDates(fy, quarter);
        var (fromKey, toKey) = ((from.Year * 100) + from.Month, (to.Year * 100) + to.Month);
        var payslips = await (
                from p in db.Set<Payslip>().AsNoTracking()
                join r in db.Set<PayrollRun>() on p.PayrollRunId equals r.Id
                join e in db.Set<Employee>() on p.EmployeeId equals e.Id
                where (p.Year * 100) + p.Month >= fromKey && (p.Year * 100) + p.Month <= toKey
                      && r.Status != PayrollRunStatus.Draft && r.Status != PayrollRunStatus.Processed
                orderby p.Year, p.Month, p.EmployeeCode
                select new { p.Year, p.Month, p.EmployeeName, p.EmployeeCode, e.PanCipher, p.GrossEarnings, p.Tds, r.PaidOn })
            .ToListAsync(ct);
        var challans = await db.Set<TdsChallan>().AsNoTracking()
            .Where(c => c.Form == TdsForm.Form24Q && (c.Year * 100) + c.Month >= fromKey && (c.Year * 100) + c.Month <= toKey)
            .OrderBy(c => c.Year).ThenBy(c => c.Month).ThenBy(c => c.DepositedOn)
            .ToListAsync(ct);

        var problems = new List<string>();
        var deposits = new List<TdsChallanDeposit>();
        foreach (var month in payslips.GroupBy(p => (p.Year, p.Month)))
        {
            var monthChallans = challans.Where(c => c.Year == month.Key.Year && c.Month == month.Key.Month).ToList();
            var deducted = month.Where(p => p.Tds > 0).ToList();
            if (deducted.Count == 0)
            {
                continue;
            }

            if (monthChallans.Count == 0)
            {
                problems.Add($"No challan is recorded for TDS on salary of {month.Key.Month:00}/{month.Key.Year} (₹{Money.FormatInr(deducted.Sum(d => d.Tds))}).");
                continue;
            }

            // All of a month's deductions go under its first challan; a second challan for the month (interest, a correction)
            // is listed with no deductees.
            var paidOn = deducted[0].PaidOn ?? new DateOnly(month.Key.Year, month.Key.Month, 1).AddMonths(1).AddDays(-1);
            var rows = deducted.Select(p => new TdsDeduction(p.EmployeeName, protector.Unprotect(p.PanCipher), p.GrossEarnings, paidOn, paidOn, p.Tds, null,
                SalarySection, p.PanCipher is null ? "C" : null)).ToList();
            deposits.Add(Deposit(monthChallans[0], rows));
            deposits.AddRange(monthChallans.Skip(1).Select(c => Deposit(c, [])));
        }

        var deductor = Deductor(profile);
        var result = TdsReturnFile.Build("24Q", deductor, fy, quarter, deposits, time.LocalToday(), problems);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "statutory.24q_exported",
            $"Form 24Q {fy}-{(fy + 1) % 100:00} {TdsReturnFile.Quarter(quarter)}: {result.Deductees} deduction(s), ₹{Money.FormatInr(result.TotalDeducted)}")
        {
            EntityType = nameof(TdsChallan),
            Severity = AuditSeverity.Warning,
        }, ct);
        return ($"24Q-{fy}-{(fy + 1) % 100:00}-{TdsReturnFile.Quarter(quarter)}.txt", Encoding.UTF8.GetBytes(result.Text), result);
    }

    public async Task<Result<PrintableDocument>> Form16Async(Guid employeeId, int fy, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        if (IncomeTaxRates.Find(fy) is not { } rates)
        {
            return Error.Rule("tax.year_unknown", $"Income-tax rates for {fy}-{(fy + 1) % 100:00} are not set up.");
        }

        var profile = await db.Set<StatutoryEmployerProfile>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (profile is null)
        {
            return Error.Rule("statutory.profile_missing", "Enter the employer's TAN, PAN and the person responsible under Statutory details first.");
        }

        var payslips = await (
                from p in db.Set<Payslip>().AsNoTracking()
                join r in db.Set<PayrollRun>() on p.PayrollRunId equals r.Id
                where p.EmployeeId == employeeId && (p.Year * 100) + p.Month >= (fy * 100) + 4 && (p.Year * 100) + p.Month <= ((fy + 1) * 100) + 3
                      && r.Status != PayrollRunStatus.Draft && r.Status != PayrollRunStatus.Processed
                select new { p.Year, p.Month, p.GrossEarnings, p.Tds })
            .ToListAsync(ct);
        if (payslips.Count == 0)
        {
            return Error.Rule("form16.no_salary", "No approved salary for this employee in the year.");
        }

        var ytd = (await PayrollTaxContext.YearToDateAsync(db, [employeeId], (fy * 100) + 4, ((fy + 1) * 100) + 4, ct)).GetValueOrDefault(employeeId) ?? YearToDate.None;
        var declaration = await db.Set<TaxDeclaration>().AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == employeeId && x.FinancialYear == fy, ct);
        var computation = IncomeTaxCalculator.Compute(rates, declaration?.Regime ?? TaxRegime.New,
            new AnnualSalary(ytd.Gross, ytd.Basic, ytd.HouseRent, ytd.EmployeePf, ytd.ProfessionalTax), declaration?.ToAmounts() ?? TaxDeclarationAmounts.None,
            employee.DateOfBirth);
        var quarters = Enumerable.Range(1, 4).Select(q =>
        {
            var (qFrom, qTo) = TdsReturnFile.QuarterDates(fy, q);
            var inQuarter = payslips.Where(p => new DateOnly(p.Year, p.Month, 1) >= qFrom && new DateOnly(p.Year, p.Month, 1) <= qTo).ToList();
            return new QuarterTds(TdsReturnFile.Quarter(q), inQuarter.Sum(p => p.GrossEarnings), inQuarter.Sum(p => p.Tds), inQuarter.Sum(p => p.Tds));
        }).ToList();

        var fyStart = new DateOnly(fy, 4, 1);
        var fyEnd = new DateOnly(fy + 1, 3, 31);
        var from = employee.JoiningDate > fyStart ? employee.JoiningDate : fyStart;
        var to = employee.LeavingDate is { } left && left < fyEnd ? left : fyEnd;
        var employer = new CertificateParty(profile.DeductorName ?? string.Empty, profile.Pan, profile.Tan, Address(profile));
        var person = new CertificateParty(employee.FullName, protector.Unprotect(employee.PanCipher), null, null);
        var document = TdsCertificates.Form16PartB(employer, person, rates, from, to, computation, quarters, profile.ResponsibleName ?? string.Empty,
            profile.ResponsibleDesignation ?? string.Empty, time.LocalToday());
        await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "statutory.form16_issued", $"Form 16 Part B {rates.Label} for {employee.EmployeeCode}")
        {
            EntityType = nameof(Employee),
            EntityId = employee.Id.ToString(),
        }, ct);
        return document;
    }

    // ---- Gratuity, bonus, full and final -----------------------------------------------------------------------------

    public async Task<Result<GratuityDto>> GratuityAsync(Guid employeeId, DateOnly? asOf, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        var date = asOf ?? employee.LeavingDate ?? time.LocalToday();
        var structure = await CurrentStructureAsync(employeeId, ct);
        var wages = structure is null ? 0 : GratuityWages(await StructureMonthAsync(structure, ct), date);
        return new GratuityDto(employeeId, employee.JoiningDate, date,
            Gratuity.Compute(employee.JoiningDate, date, wages, employee.SeparationReason, employee.EmploymentType == EmploymentType.Contract));
    }

    public async Task<Result<BonusRegisterDto>> BonusRegisterAsync(int fy, decimal ratePercent, decimal minimumWage, CancellationToken ct)
    {
        var months = await BonusMonthsAsync(null, fy, ct);
        var rows = months.GroupBy(m => (m.EmployeeId, m.EmployeeCode, m.EmployeeName))
            .Select(g => new BonusRegisterRow(g.Key.EmployeeId, g.Key.EmployeeCode, g.Key.EmployeeName,
                StatutoryBonus.Compute(g.Select(m => m.Month), ratePercent, minimumWage)))
            .OrderBy(r => r.EmployeeCode)
            .ToList();
        return new BonusRegisterDto(fy, Math.Clamp(ratePercent, StatutoryBonus.MinimumRate, StatutoryBonus.MaximumRate), minimumWage, rows, rows.Sum(r => r.Result.Amount));
    }

    public async Task<Result<FullAndFinalDto>> FullAndFinalAsync(Guid employeeId, FullAndFinalRequest r, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        var lastDay = r.LastWorkingDay ?? employee.LeavingDate ?? time.LocalToday();
        if (lastDay < employee.JoiningDate)
        {
            return Error.Field("lastWorkingDay", "The last working day cannot be before the joining date.");
        }

        var structure = await CurrentStructureAsync(employeeId, ct);
        if (structure is null)
        {
            return Error.Rule("settlement.no_structure", "The employee has no salary structure, so the settlement cannot be worked out.");
        }

        var month = await StructureMonthAsync(structure, ct);
        var wages = GratuityWages(month, lastDay);
        var gratuity = Gratuity.Compute(employee.JoiningDate, lastDay, wages, employee.SeparationReason, employee.EmploymentType == EmploymentType.Contract);

        var leaveDays = r.LeaveDays ?? await EncashableLeaveAsync(employeeId, lastDay.Year, ct);
        var encashment = LeaveEncashment.Compute(leaveDays, wages, r.LeaveDivisor);

        var fy = IncomeTaxRates.FinancialYearOf(lastDay);
        var bonusMonths = (await BonusMonthsAsync(employeeId, fy, ct)).Select(m => m.Month).ToList();
        var bonus = StatutoryBonus.Compute(bonusMonths, r.BonusRatePercent, r.MinimumWage, r.BonusDisqualified);

        var statement = FullAndFinalSettlement.Compute(new FullAndFinalInput(
            lastDay, r.NoticeDaysRequired, r.NoticeDaysServed, r.NoticeWaived, r.EmployerEndedWithoutNotice, month.Gross, r.UnpaidSalary, encashment, gratuity,
            bonus.Amount, (r.Recoveries ?? []).Select(x => new SettlementRecovery(x.Code.ToUpperInvariant(), x.Description.Trim(), x.Amount)).ToList()));
        return new FullAndFinalDto(employee.Id, employee.EmployeeCode, employee.FullName, employee.JoiningDate, lastDay, month.Gross, wages, gratuity, encashment, bonus,
            statement);
    }

    /// <summary>
    /// Settles the full and final statement worked out as <see cref="FullAndFinalAsync"/> does: the leave days it pays out
    /// are debited from the employee's encashable balances of the last working day's year (<see cref="LeaveBalance.Encashed"/>,
    /// type by type) and recorded as a separation encashment, so the balance no longer shows days already paid. Only for an
    /// employee who is leaving, once (<c>settlement.already_settled</c>), with no in-service encashment waiting, and never by
    /// the employee themself.
    /// </summary>
    public async Task<Result<FullAndFinalDto>> SettleFullAndFinalAsync(Guid employeeId, FullAndFinalRequest r, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        if (await pay.OwnPayAsync([employeeId], approving: true, ct) is { } own)
        {
            return own;
        }

        if (employee.Status != EmployeeStatus.Separated && employee.LeavingDate is null)
        {
            return Error.Rule("settlement.not_leaving", $"{employee.FullName} has no leaving date, so there is no full and final settlement to settle.");
        }

        if (await db.Set<LeaveEncashmentRecord>().AnyAsync(e => e.EmployeeId == employeeId && e.Kind == LeaveEncashmentKind.Separation, ct))
        {
            return Error.Conflict("settlement.already_settled", $"The full and final settlement of {employee.FullName} has already been settled and its leave paid out.");
        }

        if ((await LeaveEncashmentService.WaitingAsync(db, employeeId, time.GetUtcNow(), ct)).Count > 0)
        {
            return Error.Rule("settlement.encashment_waiting", "An in-service leave encashment for this employee is waiting for approval. Decide or withdraw it first.");
        }

        var worked = await FullAndFinalAsync(employeeId, r, ct);
        if (worked.IsFailure)
        {
            return worked;
        }

        var statement = worked.Value;
        var remaining = statement.LeaveEncashment.Days;
        var year = statement.LastWorkingDay.Year;
        var encashable = await db.Set<LeaveType>().AsNoTracking().Where(t => t.IsEncashable).OrderBy(t => t.Code).ToListAsync(ct);
        var encashableIds = encashable.Select(t => t.Id).ToList();
        var balances = await db.Set<LeaveBalance>()
            .Where(b => b.EmployeeId == employeeId && b.Year == year && encashableIds.Contains(b.LeaveTypeId))
            .ToListAsync(ct);
        if (remaining > balances.Sum(b => Math.Max(0, b.Closing)))
        {
            return Error.Rule("settlement.leave_over_balance",
                $"The settlement pays {remaining:0.#} day(s) of leave, more than the {balances.Sum(b => Math.Max(0, b.Closing)):0.#} left in the encashable balances of {year}. Correct the balance or the days first.");
        }

        var paid = new List<string>();
        foreach (var type in encashable)
        {
            var balance = balances.FirstOrDefault(b => b.LeaveTypeId == type.Id);
            if (remaining <= 0 || balance is null || balance.Closing <= 0)
            {
                continue;
            }

            var days = Math.Min(remaining, balance.Closing);
            balance.Encashed += days;
            remaining -= days;
            db.Add(new LeaveEncashmentRecord
            {
                FacilityId = employee.FacilityId,
                EmployeeId = employeeId,
                LeaveTypeId = type.Id,
                Year = year,
                Kind = LeaveEncashmentKind.Separation,
                Days = days,
                DailyRate = statement.LeaveEncashment.DailyRate,
                Amount = Money.Round(days * statement.LeaveEncashment.DailyRate),
                LastWorkingDay = statement.LastWorkingDay,
                Reason = "Full and final settlement",
            });
            paid.Add($"{days:0.#} {type.Code}");
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "settlement.settled",
            $"Full and final settlement of {employee.EmployeeCode} settled to {statement.LastWorkingDay:dd MMM yyyy}: net ₹{Money.FormatInr(statement.Statement.NetPayable)}"
            + (paid.Count > 0 ? $", leave paid out and debited ({string.Join(", ", paid)}, ₹{Money.FormatInr(statement.LeaveEncashment.Amount)})" : ", no leave paid out"))
        {
            EntityType = nameof(Employee),
            EntityId = employeeId.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { statement.LastWorkingDay, leaveDays = statement.LeaveEncashment.Days, statement.LeaveEncashment.Amount, net = statement.Statement.NetPayable, settledBy = currentUser.UserId },
        });
        await db.SaveChangesAsync(ct);
        return statement;
    }

    // ---- Bank upload files ------------------------------------------------------------------------------------------

    public async Task<Result<(string FileName, byte[] Content)>> BankUploadAsync(Guid runId, BankFileFormat format, CancellationToken ct)
    {
        var run = await ApprovedRunAsync(runId, ct);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        // The bank's own formats pay the accounts fixed on the payslips when the payroll was approved, as the generic file
        // does, never the employees' accounts as they are now (maker-checker slice 11).
        if (run.Value.BankDetailsFixedAt is null)
        {
            return Error.Rule(PayrollService.BankDetailsNotFixedCode,
                "This payroll was approved before the payment details were fixed at approval, so its bank file would read today's accounts. Reopen it and approve it again.");
        }

        var profile = await db.Set<StatutoryEmployerProfile>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (format is BankFileFormat.Sbi or BankFileFormat.Icici && string.IsNullOrEmpty(profile?.SalaryDebitAccount))
        {
            return Error.Rule("statutory.debit_account_missing", "Enter the hospital's salary account under Statutory details first: the bank file names it.");
        }

        var payees = await db.Set<Payslip>().AsNoTracking()
            .Where(p => p.PayrollRunId == runId && p.NetPay > 0 && p.PaymentMode == SalaryPaymentMode.BankTransfer && p.BankAccountCipher != null && p.BankIfscCipher != null)
            .OrderBy(p => p.EmployeeCode)
            .Select(p => new { p.EmployeeCode, p.EmployeeName, p.NetPay, p.BankAccountCipher, p.BankIfscCipher })
            .ToListAsync(ct);
        var narration = BankTransferFile.Narration(run.Value.Year, run.Value.Month);
        var rows = payees.Select(p => new BankTransferRow(p.EmployeeCode, p.EmployeeName, protector.Unprotect(p.BankAccountCipher) ?? string.Empty,
            protector.Unprotect(p.BankIfscCipher) ?? string.Empty, p.NetPay, narration)).ToList();
        var valueDate = run.Value.PaidOn ?? time.LocalToday();
        var text = BankUploadFiles.Build(format, rows, profile?.SalaryDebitAccount, valueDate);

        await AuditFileAsync("payroll.bank_file_exported", run.Value,
            $"Salary bank file ({format}) for {run.Value.Month:00}/{run.Value.Year} exported: {rows.Count} payee(s), ₹{Money.FormatInr(rows.Sum(r => r.Amount))}", ct);
        var extension = format == BankFileFormat.Sbi ? "txt" : "csv";
        return ($"salary-{format.ToString().ToLowerInvariant()}-{run.Value.Year}-{run.Value.Month:00}.{extension}", Encoding.UTF8.GetBytes(text));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    public static TdsDeductor Deductor(StatutoryEmployerProfile p) => new(
        p.Tan ?? string.Empty, p.Pan ?? string.Empty, p.DeductorName ?? string.Empty, p.Branch,
        new[] { p.AddressLine1, p.AddressLine2, p.AddressLine3, p.City }.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!).ToList(),
        p.StateCode ?? string.Empty, p.Pin ?? string.Empty, p.Email, p.Phone, p.DeductorType, p.ResponsibleName ?? string.Empty, p.ResponsibleDesignation ?? string.Empty,
        p.ResponsiblePan, p.ResponsibleMobile, p.ResponsibleEmail);

    public static TdsChallanDeposit Deposit(TdsChallan c, IReadOnlyList<TdsDeduction> rows) =>
        new(c.BsrCode, c.DepositedOn, c.ChallanSerial, c.Tax, c.Surcharge, c.Cess, c.Interest, c.Others, c.LateFee, c.MinorHead, rows);

    public static string Address(StatutoryEmployerProfile p) =>
        string.Join(", ", new[] { p.AddressLine1, p.AddressLine2, p.AddressLine3, p.City, p.Pin }.Where(a => !string.IsNullOrWhiteSpace(a)));

    /// <summary>Gratuity wages: basic pay under the Act; wages as the Code defines them for anyone leaving from 21 November 2025.</summary>
    /// <summary>
    /// The month's wages leave is encashed on, the same as the full and final settlement's: wages under the Code on Wages
    /// from its commencement (basic before it). Null when the employee has no salary structure.
    /// </summary>
    internal async Task<decimal?> EncashmentWagesAsync(Guid employeeId, DateOnly asOf, CancellationToken ct)
    {
        var structure = await CurrentStructureAsync(employeeId, ct);
        return structure is null ? null : GratuityWages(await StructureMonthAsync(structure, ct), asOf);
    }

    private static decimal GratuityWages(StructureMonth month, DateOnly lastDay) => lastDay >= Gratuity.CodeInForce ? month.PfWages : month.Basic;

    private sealed record StructureMonth(decimal Gross, decimal Basic, decimal HouseRent, decimal PfWages);

    private async Task<EmployeeSalaryStructure?> CurrentStructureAsync(Guid employeeId, CancellationToken ct) =>
        await db.Set<EmployeeSalaryStructure>().AsNoTracking().Include(s => s.Lines)
            .Where(s => s.EmployeeId == employeeId)
            .OrderByDescending(s => s.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

    /// <summary>A full month of the structure: gross, basic, house rent and the wages PF (and gratuity under the Code) are on.</summary>
    private async Task<StructureMonth> StructureMonthAsync(EmployeeSalaryStructure structure, CancellationToken ct)
    {
        var components = await db.Set<SalaryComponent>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var lines = structure.Lines.Where(l => components.ContainsKey(l.SalaryComponentId)).Select(l => (Component: components[l.SalaryComponentId], l.Value)).ToList();
        var basic = lines.Where(l => l.Component.Code == SalaryComponent.BasicCode).Sum(l => l.Value);
        decimal Amount((SalaryComponent Component, decimal Value) l) =>
            l.Component.CalculationType == SalaryCalculationType.PercentOfBasic ? Money.PercentOf(basic, l.Value) : Money.Round(l.Value);
        var earnings = lines.Where(l => l.Component.Kind == SalaryComponentKind.Earning).ToList();
        var gross = earnings.Sum(Amount);
        var houseRent = earnings.Where(l => l.Component.Code == PayrollTaxContext.HouseRentCode).Sum(Amount);
        var wages = ProvidentFund.Wages(earnings.Where(l => l.Component.WageTreatment == WageTreatment.Wages).Sum(Amount),
            earnings.Where(l => l.Component.WageTreatment == WageTreatment.Excluded).Sum(Amount));
        return new StructureMonth(gross, basic, houseRent, Money.Round(wages));
    }

    private sealed record EmployeeBonusMonth(Guid EmployeeId, string EmployeeCode, string EmployeeName, BonusMonth Month);

    /// <summary>Each approved month's bonus wages (wages as the Code defines them, from the payslip's pay heads) and days paid.</summary>
    private async Task<IReadOnlyList<EmployeeBonusMonth>> BonusMonthsAsync(Guid? employeeId, int fy, CancellationToken ct)
    {
        var treatment = await db.Set<SalaryComponent>().AsNoTracking().ToDictionaryAsync(c => c.Code, c => c.WageTreatment, ct);
        var payslips = await (
                from p in db.Set<Payslip>().AsNoTracking().Include(p => p.Lines)
                join r in db.Set<PayrollRun>() on p.PayrollRunId equals r.Id
                where (employeeId == null || p.EmployeeId == employeeId) && (p.Year * 100) + p.Month >= (fy * 100) + 4 && (p.Year * 100) + p.Month <= ((fy + 1) * 100) + 3
                      && r.Status != PayrollRunStatus.Draft && r.Status != PayrollRunStatus.Processed
                select p)
            .ToListAsync(ct);
        return payslips.Select(p =>
        {
            var earnings = p.Lines.Where(l => l.Kind == SalaryComponentKind.Earning).ToList();
            var wages = ProvidentFund.Wages(
                earnings.Where(l => treatment.GetValueOrDefault(l.Code, WageTreatment.Wages) == WageTreatment.Wages).Sum(l => l.Amount),
                earnings.Where(l => treatment.GetValueOrDefault(l.Code) == WageTreatment.Excluded).Sum(l => l.Amount));
            return new EmployeeBonusMonth(p.EmployeeId, p.EmployeeCode, p.EmployeeName, new BonusMonth(p.Year, p.Month, Money.Round(wages), p.PayableDays));
        }).ToList();
    }

    private async Task<decimal> EncashableLeaveAsync(Guid employeeId, int year, CancellationToken ct)
    {
        var encashable = await db.Set<LeaveType>().AsNoTracking().Where(t => t.IsEncashable).Select(t => t.Id).ToListAsync(ct);
        var balances = await db.Set<LeaveBalance>().AsNoTracking()
            .Where(b => b.EmployeeId == employeeId && b.Year == year && encashable.Contains(b.LeaveTypeId))
            .ToListAsync(ct);
        return Math.Max(0, balances.Sum(b => b.Closing));
    }

    private async Task<Result<PayrollRun>> ApprovedRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(PayrollRun), runId);
        }

        return run.Status is PayrollRunStatus.Draft or PayrollRunStatus.Processed
            ? Error.Rule("payroll.not_approved", "Statutory and bank files are made only from an approved payroll.")
            : run;
    }

    private Task AuditFileAsync(string action, PayrollRun run, string summary, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.Security, action, summary)
        {
            EntityType = nameof(PayrollRun),
            EntityId = run.Id.ToString(),
            Severity = AuditSeverity.Warning,
        }, ct);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StatutoryProfileDto ToDto(StatutoryEmployerProfile p) => new(
        p.Tan, p.Pan, p.DeductorName, p.Branch, p.DeductorType, p.AddressLine1, p.AddressLine2, p.AddressLine3, p.City, p.StateCode, p.Pin, p.Email, p.Phone,
        p.ResponsibleName, p.ResponsibleDesignation, p.ResponsiblePan, p.ResponsibleMobile, p.ResponsibleEmail, p.EpfEstablishmentCode, p.EsicEmployerCode,
        p.SalaryDebitAccount is { } a ? HrIdentifiers.MaskAccount(a) : null, p.SalaryBank, p.Version);

    private static TdsChallanDto ToDto(TdsChallan c) => new(
        c.Id, c.Form, c.Year, c.Month, c.BsrCode, c.DepositedOn, c.ChallanSerial, c.Tax, c.Surcharge, c.Cess, c.Interest, c.Others, c.LateFee, c.MinorHead,
        c.Tax + c.Surcharge + c.Cess + c.Interest + c.Others + c.LateFee);

    private static TaxDeclarationDto ToDto(TaxDeclaration d) => new(
        d.EmployeeId, d.FinancialYear, d.Regime, d.RentPaid, d.MetroCity, d.Section80C, d.Section80Ccd1B, d.Section80DSelf, d.Section80DParents, d.ParentsSenior,
        d.Section80E, d.Section80G, d.Section80Tta, d.HousingLoanInterest, d.OtherIncome, d.PreviousEmployerSalary, d.PreviousEmployerTds, d.Notes, d.Version);
}
