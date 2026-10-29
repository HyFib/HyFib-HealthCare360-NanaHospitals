using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.HR.Seed;

/// <summary>
/// HR reference data (idempotent, matched by code): designations, leave policies, pay heads, Tamil Nadu and
/// Karnataka professional-tax slabs, standard hospital shifts and national holidays per facility. Demonstration
/// employees only when <c>HealthCare360:SeedDemoData</c> is on.
/// The professional-tax slabs reflect the notifications known at release (Greater Chennai Corporation half-yearly
/// slabs as revised in 2024; Karnataka ₹200/month from ₹25,000 with ₹300 in February) — HR must verify them with
/// the local body before go-live; they are editable under Payroll → Professional tax.
/// </summary>
internal sealed class HrSeeder(IOptions<PlatformOptions> options, INumberSeries numbers, TimeProvider time) : ISeeder
{
    public int Order => 110;

    private static readonly (string Code, string Name, StaffCategory Category, int Grade)[] Designations =
    [
        ("CONS", "Consultant", StaffCategory.Medical, 1),
        ("SR-RES", "Senior Resident", StaffCategory.Medical, 2),
        ("RMO", "Resident Medical Officer", StaffCategory.Medical, 3),
        ("NS", "Nursing Superintendent", StaffCategory.Nursing, 2),
        ("NIC", "Nurse In-charge / Ward Sister", StaffCategory.Nursing, 3),
        ("SN", "Staff Nurse", StaffCategory.Nursing, 4),
        ("ANM", "Auxiliary Nurse Midwife", StaffCategory.Nursing, 5),
        ("LT", "Laboratory Technician", StaffCategory.Paramedical, 4),
        ("RT", "Radiographer", StaffCategory.Paramedical, 4),
        ("PHARM", "Pharmacist", StaffCategory.Paramedical, 4),
        ("PHYSIO", "Physiotherapist", StaffCategory.Paramedical, 4),
        ("DIAL-T", "Dialysis Technician", StaffCategory.Paramedical, 4),
        ("OT-T", "OT Technician", StaffCategory.Paramedical, 4),
        ("ADMIN", "Hospital Administrator", StaffCategory.Administrative, 1),
        ("HR-EXE", "HR Executive", StaffCategory.Administrative, 4),
        ("ACCT", "Accountant", StaffCategory.Administrative, 4),
        ("FO-EXE", "Front Office Executive", StaffCategory.Administrative, 5),
        ("BILL-EXE", "Billing Executive", StaffCategory.Administrative, 5),
        ("GDA", "General Duty Assistant", StaffCategory.Support, 7),
        ("HK", "Housekeeping Staff", StaffCategory.Support, 7),
        ("SEC", "Security Guard", StaffCategory.Support, 7),
        ("DRV", "Ambulance Driver", StaffCategory.Support, 6),
        ("COOK", "Cook", StaffCategory.Support, 7),
    ];

    private static readonly LeaveType[] LeaveTypes =
    [
        new() { Code = "CL", Name = "Casual Leave", AnnualQuota = 12, MaxDaysPerRequest = 3 },
        new() { Code = "SL", Name = "Sick Leave", AnnualQuota = 12, CarryForwardCap = 30, DocumentRequiredAfterDays = 2 },
        new() { Code = "EL", Name = "Earned Leave", AnnualQuota = 15, CarryForwardCap = 45, IsEncashable = true, AllowHalfDay = false },
        new()
        {
            Code = "ML", Name = "Maternity Leave", AnnualQuota = 182, MaxDaysPerRequest = 182, AllowHalfDay = false, CountsNonWorkingDays = true,
            ApplicableGender = Gender.Female, DocumentRequiredAfterDays = 0,
        },
        new() { Code = "CO", Name = "Compensatory Off", AnnualQuota = 0 },
        new() { Code = "LOP", Name = "Leave Without Pay", IsPaid = false, TracksBalance = false },
    ];

    /// <summary>
    /// The seeded earnings the Code on Social Security 2020 leaves out of wages: house rent, conveyance and overtime
    /// (s.2(88)(f), (d) and (h)). The others (medical, special, night duty, arrears, incentive) count as wages: the
    /// definition does not leave them out.
    /// </summary>
    internal static readonly IReadOnlySet<string> ExcludedFromWages = new HashSet<string>(StringComparer.Ordinal) { "HRA", "CONV", "OT" };

    private static readonly (string Code, string Name, SalaryComponentKind Kind, SalaryCalculationType Type, decimal Value, bool Taxable, bool Statutory, int Sort)[] Components =
    [
        (SalaryComponent.BasicCode, "Basic Salary", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 0, true, false, 10),
        ("HRA", "House Rent Allowance", SalaryComponentKind.Earning, SalaryCalculationType.PercentOfBasic, 40, true, false, 20),
        ("CONV", "Conveyance Allowance", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 1600, true, false, 30),
        ("MEDICAL", "Medical Allowance", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 1250, true, false, 40),
        ("SPECIAL", "Special Allowance", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 0, true, false, 50),
        ("NIGHT", "Night Duty Allowance", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 0, true, false, 60),
        ("OT", "Overtime", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 0, true, false, 70),
        ("ARREARS", "Arrears", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 0, true, false, 80),
        ("INCENTIVE", "Incentive", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 0, true, false, 90),
        ("CANTEEN", "Canteen Staff Credit", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, false, 10),
        ("ADVANCE", "Salary Advance Recovery", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, false, 20),
        ("LOAN", "Loan Recovery", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, false, 30),
        ("UNIFORM", "Uniform Recovery", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, false, 40),
        ("OTHER_DED", "Other Deduction", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, false, 50),
        (SalaryComponent.PfCode, "Provident Fund (employee)", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, true, 900),
        (SalaryComponent.EsiCode, "ESI (employee)", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, true, 910),
        (SalaryComponent.ProfessionalTaxCode, "Professional Tax", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, true, 920),
        (SalaryComponent.TdsCode, "Income Tax (TDS)", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 0, false, true, 930),
    ];

    private static readonly (string State, string Name, ProfessionalTaxFrequency Frequency, int[] Months, (decimal From, decimal? To, decimal Amount, decimal? February)[] Bands)[] ProfessionalTax =
    [
        ("TN", "Tamil Nadu", ProfessionalTaxFrequency.HalfYearly, [9, 3],
        [
            (0, 21_000, 0, null), (21_001, 30_000, 180, null), (30_001, 45_000, 430, null), (45_001, 60_000, 930, null),
            (60_001, 75_000, 1_025, null), (75_001, null, 1_250, null),
        ]),
        ("KA", "Karnataka", ProfessionalTaxFrequency.Monthly, [],
        [
            (0, 24_999, 0, null), (25_000, null, 200, 300),
        ]),
    ];

    private static readonly (string Code, string Name, TimeOnly Start, TimeOnly End, int Break, int Half, int Full, bool Night, string Color)[] Shifts =
    [
        ("GEN", "General", new(9, 0), new(17, 30), 30, 240, 420, false, "#1A73E8"),
        ("MOR", "Morning", new(7, 0), new(14, 0), 15, 180, 360, false, "#34A853"),
        ("EVE", "Evening", new(14, 0), new(21, 0), 15, 180, 360, false, "#F9AB00"),
        ("NGT", "Night", new(21, 0), new(7, 0), 30, 240, 510, true, "#5E35B1"),
    ];

    private static readonly (int Month, int Day, string Name)[] NationalHolidays =
    [
        (1, 26, "Republic Day"),
        (5, 1, "May Day"),
        (8, 15, "Independence Day"),
        (10, 2, "Gandhi Jayanti"),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        await SeedDesignationsAsync(db, cancellationToken);
        await SeedLeaveTypesAsync(db, cancellationToken);
        await SeedComponentsAsync(db, cancellationToken);
        await SeedProfessionalTaxAsync(db, cancellationToken);

        var facilities = await db.Set<Facility>().Select(f => f.Id).ToListAsync(cancellationToken);
        foreach (var facilityId in facilities)
        {
            await SeedShiftsAsync(db, facilityId, cancellationToken);
            await SeedHolidaysAsync(db, facilityId, cancellationToken);
        }

        if (options.Value.SeedDemoData && facilities.Count > 0)
        {
            await SeedDemoEmployeesAsync(db, facilities[0], cancellationToken);
        }
    }

    private static async Task SeedDesignationsAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<Designation>().Select(d => d.Code).ToListAsync(ct);
        foreach (var (code, name, category, grade) in Designations.Where(d => !existing.Contains(d.Code)))
        {
            db.Add(new Designation { Code = code, Name = name, Category = category, Grade = grade });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedLeaveTypesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<LeaveType>().Select(t => t.Code).ToListAsync(ct);
        foreach (var type in LeaveTypes.Where(t => !existing.Contains(t.Code)))
        {
            db.Add(new LeaveType
            {
                Code = type.Code,
                Name = type.Name,
                AnnualQuota = type.AnnualQuota,
                CarryForwardCap = type.CarryForwardCap,
                IsEncashable = type.IsEncashable,
                IsPaid = type.IsPaid,
                AllowHalfDay = type.AllowHalfDay,
                TracksBalance = type.TracksBalance,
                CountsNonWorkingDays = type.CountsNonWorkingDays,
                MaxDaysPerRequest = type.MaxDaysPerRequest,
                DocumentRequiredAfterDays = type.DocumentRequiredAfterDays,
                ApplicableGender = type.ApplicableGender,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedComponentsAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<SalaryComponent>().Select(c => c.Code).ToListAsync(ct);
        foreach (var c in Components.Where(c => !existing.Contains(c.Code)))
        {
            db.Add(new SalaryComponent
            {
                Code = c.Code,
                Name = c.Name,
                Kind = c.Kind,
                CalculationType = c.Type,
                DefaultValue = c.Value,
                IsTaxable = c.Taxable,
                WageTreatment = ExcludedFromWages.Contains(c.Code) ? WageTreatment.Excluded : WageTreatment.Wages,
                IsStatutory = c.Statutory,
                SortOrder = c.Sort,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedProfessionalTaxAsync(HospitalDbContext db, CancellationToken ct)
    {
        var states = await db.Set<ProfessionalTaxSlab>().Select(s => s.StateCode).Distinct().ToListAsync(ct);
        foreach (var (state, name, frequency, months, bands) in ProfessionalTax.Where(p => !states.Contains(p.State)))
        {
            foreach (var (from, to, amount, february) in bands)
            {
                db.Add(new ProfessionalTaxSlab
                {
                    StateCode = state,
                    StateName = name,
                    Frequency = frequency,
                    DeductionMonths = [.. months],
                    IncomeFrom = from,
                    IncomeTo = to,
                    Amount = amount,
                    FebruaryAmount = february,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedShiftsAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        if (await db.Set<Shift>().AnyAsync(s => s.FacilityId == facilityId, ct))
        {
            return;
        }

        foreach (var (code, name, start, end, breakMinutes, half, full, night, color) in Shifts)
        {
            db.Add(new Shift
            {
                FacilityId = facilityId,
                Code = code,
                Name = name,
                StartTime = start,
                EndTime = end,
                BreakMinutes = breakMinutes,
                GraceMinutes = 10,
                HalfDayMinutes = half,
                FullDayMinutes = full,
                OvertimeThresholdMinutes = 30,
                IsNightShift = night,
                Color = color,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedHolidaysAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        var year = time.LocalToday().Year;
        var dates = NationalHolidays.SelectMany(h => new[] { year, year + 1 }.Select(y => (Date: new DateOnly(y, h.Month, h.Day), h.Name))).ToList();
        var first = dates.Min(d => d.Date);
        var existing = await db.Set<Holiday>().Where(h => h.FacilityId == facilityId && h.Date >= first).Select(h => h.Date).ToListAsync(ct);
        foreach (var (date, name) in dates.Where(d => !existing.Contains(d.Date)))
        {
            db.Add(new Holiday { FacilityId = facilityId, Date = date, Name = name, Kind = HolidayKind.National });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A handful of staff with salary structures so payroll and attendance screens have data in demos.</summary>
    private async Task SeedDemoEmployeesAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        if (await db.Set<Employee>().AnyAsync(ct))
        {
            return;
        }

        var designations = await db.Set<Designation>().ToDictionaryAsync(d => d.Code, d => d.Id, ct);
        var departments = await db.Set<Department>().Where(d => d.FacilityId == facilityId).ToDictionaryAsync(d => d.Code, d => d.Id, ct);
        var components = await db.Set<SalaryComponent>().ToDictionaryAsync(c => c.Code, c => c.Id, ct);
        var general = await db.Set<Shift>().Where(s => s.FacilityId == facilityId && s.Code == "GEN").Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        var today = time.LocalToday();

        var demo = new (string First, string Last, Gender Gender, string Designation, string? Department, decimal Basic, string Pin, EmploymentType Type)[]
        {
            ("Deepa", "Raman", Gender.Female, "SN", "GM", 14_000, "1001", EmploymentType.Permanent),
            ("Suresh", "Babu", Gender.Male, "LT", "LAB", 16_500, "1002", EmploymentType.Permanent),
            ("Anitha", "Krishnan", Gender.Female, "FO-EXE", "FO", 12_000, "1003", EmploymentType.Contract),
            ("Murugan", "Selvam", Gender.Male, "HK", null, 9_500, "1004", EmploymentType.Contract),
            ("Kavitha", "Iyer", Gender.Female, "HR-EXE", "HR", 22_000, "1005", EmploymentType.Permanent),
        };

        for (var index = 0; index < demo.Length; index++)
        {
            var d = demo[index];
            var employee = new Employee
            {
                FacilityId = facilityId,
                Gender = d.Gender,
                DateOfBirth = today.AddYears(-(26 + (index * 4))).AddDays(-(index * 37)),
                DesignationId = designations.GetValueOrDefault(d.Designation),
                DepartmentId = d.Department is { } code ? departments.GetValueOrDefault(code) : null,
                EmploymentType = d.Type,
                JoiningDate = today.AddYears(-2).AddDays(-(index * 53)),
                BiometricPin = d.Pin,
                DefaultShiftId = general,
                PaymentMode = SalaryPaymentMode.BankTransfer,
            };
            employee.SetName(null, d.First, null, d.Last);
            employee.EmployeeCode = await numbers.NextAsync(HRNumberSeries.Employee, facilityId, ct);
            db.Add(employee);

            var structure = new EmployeeSalaryStructure
            {
                FacilityId = facilityId,
                EmployeeId = employee.Id,
                EffectiveFrom = employee.JoiningDate,
                ProfessionalTaxState = "TN",
            };
            structure.Lines.Add(new SalaryStructureLine { StructureId = structure.Id, SalaryComponentId = components[SalaryComponent.BasicCode], Value = d.Basic });
            structure.Lines.Add(new SalaryStructureLine { StructureId = structure.Id, SalaryComponentId = components["HRA"], Value = 40 });
            structure.Lines.Add(new SalaryStructureLine { StructureId = structure.Id, SalaryComponentId = components["CONV"], Value = 1_600 });
            db.Add(structure);
        }

        await db.SaveChangesAsync(ct);
    }
}
