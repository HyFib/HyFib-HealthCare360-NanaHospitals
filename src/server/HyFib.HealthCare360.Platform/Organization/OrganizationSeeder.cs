using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Organization;

/// <summary>Creates the default facility, standard specialities, departments and core billable services (idempotent).</summary>
internal sealed class OrganizationSeeder(IOptions<PlatformOptions> options) : ISeeder
{
    public int Order => 5;

    private static readonly (string Code, string Name)[] Specialties =
    [
        ("GM", "General Medicine"), ("PED", "Paediatrics"), ("OBG", "Obstetrics & Gynaecology"), ("GS", "General Surgery"),
        ("ORTH", "Orthopaedics"), ("CARD", "Cardiology"), ("NEUR", "Neurology"), ("NEPH", "Nephrology"), ("UROL", "Urology"),
        ("ENT", "Otorhinolaryngology (ENT)"), ("OPHT", "Ophthalmology"), ("DERM", "Dermatology"), ("PSY", "Psychiatry"),
        ("PULM", "Pulmonology"), ("GAST", "Gastroenterology"), ("ENDO", "Endocrinology"), ("ONCO", "Oncology"),
        ("ANES", "Anaesthesiology"), ("RAD", "Radiology"), ("PATH", "Pathology"), ("EM", "Emergency Medicine"),
        ("DENT", "Dentistry"), ("PMR", "Physical Medicine & Rehabilitation"), ("NUTR", "Clinical Nutrition"),
    ];

    private static readonly (string Code, string Name, DepartmentKind Kind, string? Specialty, string? Token)[] Departments =
    [
        ("GM", "General Medicine", DepartmentKind.Clinical, "GM", "GM"),
        ("PED", "Paediatrics", DepartmentKind.Clinical, "PED", "PD"),
        ("OBG", "Obstetrics & Gynaecology", DepartmentKind.Clinical, "OBG", "OG"),
        ("GS", "General Surgery", DepartmentKind.Clinical, "GS", "GS"),
        ("ORTH", "Orthopaedics", DepartmentKind.Clinical, "ORTH", "OR"),
        ("CARD", "Cardiology", DepartmentKind.Clinical, "CARD", "CD"),
        ("ENT", "ENT", DepartmentKind.Clinical, "ENT", "EN"),
        ("OPHT", "Ophthalmology", DepartmentKind.Clinical, "OPHT", "EY"),
        ("DERM", "Dermatology", DepartmentKind.Clinical, "DERM", "DM"),
        ("EMER", "Emergency", DepartmentKind.Clinical, "EM", "ER"),
        ("ICU", "Intensive Care Unit", DepartmentKind.Clinical, null, null),
        ("OT", "Operation Theatre", DepartmentKind.Clinical, "ANES", null),
        ("DIAL", "Dialysis Unit", DepartmentKind.Clinical, "NEPH", "DL"),
        ("PHYSIO", "Physiotherapy", DepartmentKind.Clinical, "PMR", "PT"),
        ("LAB", "Laboratory", DepartmentKind.Diagnostic, "PATH", "LB"),
        ("RAD", "Radiology & Imaging", DepartmentKind.Diagnostic, "RAD", "RD"),
        ("CDX", "Cardiac Diagnostics", DepartmentKind.Diagnostic, "CARD", "CX"),
        ("BB", "Blood Bank", DepartmentKind.Diagnostic, "PATH", null),
        ("PHARM", "Pharmacy", DepartmentKind.Support, null, "PH"),
        ("DIET", "Dietary", DepartmentKind.Support, "NUTR", null),
        ("CSSD", "CSSD", DepartmentKind.Support, null, null),
        ("CANT", "Canteen", DepartmentKind.Support, null, null),
        ("STORE", "Central Store", DepartmentKind.Support, null, null),
        ("BIOMED", "Biomedical Engineering", DepartmentKind.Support, null, null),
        ("FO", "Front Office", DepartmentKind.Administrative, null, null),
        ("BILL", "Billing & Accounts", DepartmentKind.Administrative, null, "BL"),
        ("MRD", "Medical Records", DepartmentKind.Administrative, null, null),
        ("HR", "Human Resources", DepartmentKind.Administrative, null, null),
        ("QUAL", "Quality & Patient Safety", DepartmentKind.Administrative, null, null),
    ];

    private static readonly (string Code, string Name, ServiceCategory Category, decimal Price, string? Department)[] Services =
    [
        ("REG-NEW", "Registration fee (new patient)", ServiceCategory.Registration, 100m, "FO"),
        ("REG-RENEW", "Registration renewal", ServiceCategory.Registration, 50m, "FO"),
        ("CONS-NEW", "Consultation — new visit", ServiceCategory.Consultation, 500m, null),
        ("CONS-FU", "Consultation — follow-up", ServiceCategory.Consultation, 300m, null),
        ("CONS-ER", "Emergency consultation", ServiceCategory.Consultation, 800m, "EMER"),
        ("CONS-TELE", "Tele-consultation", ServiceCategory.Consultation, 400m, null),
        ("BED-GEN", "Bed charges — general ward (per day)", ServiceCategory.BedCharge, 1500m, null),
        ("BED-SEMI", "Bed charges — semi-private (per day)", ServiceCategory.BedCharge, 2500m, null),
        ("BED-PVT", "Bed charges — private room (per day)", ServiceCategory.BedCharge, 4000m, null),
        ("BED-DLX", "Bed charges — deluxe suite (per day)", ServiceCategory.BedCharge, 6500m, null),
        ("BED-ICU", "Bed charges — ICU (per day)", ServiceCategory.BedCharge, 8000m, "ICU"),
        ("BED-NICU", "Bed charges — NICU (per day)", ServiceCategory.BedCharge, 7000m, "ICU"),
        ("NURS-DAY", "Nursing charges (per day)", ServiceCategory.Nursing, 600m, null),
        ("VISIT-DR", "Consultant visit (in-patient)", ServiceCategory.DoctorVisit, 700m, null),
        ("VISIT-DR-ICU", "Consultant visit (ICU)", ServiceCategory.DoctorVisit, 1000m, "ICU"),
        ("PROC-DRESS", "Dressing — minor", ServiceCategory.Procedure, 250m, null),
        ("PROC-INJ", "Injection administration", ServiceCategory.Procedure, 50m, null),
        ("PROC-NEB", "Nebulisation", ServiceCategory.Procedure, 150m, null),
        ("PROC-SUTURE", "Suturing — minor", ServiceCategory.Procedure, 600m, "EMER"),
        ("AMB-LOCAL", "Ambulance — local", ServiceCategory.Ambulance, 1000m, null),
        ("MISC-MRD-COPY", "Medical records copy", ServiceCategory.Miscellaneous, 200m, "MRD"),
        ("MISC-CERT", "Medical certificate", ServiceCategory.Miscellaneous, 300m, null),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var facility = await db.Set<Facility>().OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (facility is null)
        {
            facility = new Facility
            {
                Code = options.Value.DefaultFacilityCode.ToUpperInvariant(),
                Name = options.Value.DefaultFacilityName,
                TimeZone = options.Value.TimeZone,
                Address = new Address { City = null, State = null, Country = "India" },
            };
            db.Add(facility);
            await db.SaveChangesAsync(cancellationToken);
        }

        var specialtyCodes = await db.Set<Specialty>().Select(s => s.Code).ToListAsync(cancellationToken);
        foreach (var (code, name) in Specialties.Where(s => !specialtyCodes.Contains(s.Code)))
        {
            db.Add(new Specialty { Code = code, Name = name });
        }

        await db.SaveChangesAsync(cancellationToken);
        var specialties = await db.Set<Specialty>().ToDictionaryAsync(s => s.Code, s => s.Id, cancellationToken);

        var departmentCodes = await db.Set<Department>().Where(d => d.FacilityId == facility.Id).Select(d => d.Code).ToListAsync(cancellationToken);
        var order = 0;
        foreach (var (code, name, kind, specialty, token) in Departments)
        {
            order += 10;
            if (departmentCodes.Contains(code))
            {
                continue;
            }

            db.Add(new Department
            {
                FacilityId = facility.Id,
                Code = code,
                Name = name,
                Kind = kind,
                SpecialtyId = specialty is not null && specialties.TryGetValue(specialty, out var sid) ? sid : null,
                TokenPrefix = token,
                DisplayOrder = order,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        var departments = await db.Set<Department>().Where(d => d.FacilityId == facility.Id).ToDictionaryAsync(d => d.Code, d => d.Id, cancellationToken);

        var serviceCodes = await db.Set<ServiceItem>().Select(s => s.Code).ToListAsync(cancellationToken);
        foreach (var (code, name, category, price, department) in Services.Where(s => !serviceCodes.Contains(s.Code)))
        {
            db.Add(new ServiceItem
            {
                Code = code,
                Name = name,
                Category = category,
                BasePrice = price,
                DepartmentId = department is not null && departments.TryGetValue(department, out var did) ? did : null,
                IsPriceEditable = category is ServiceCategory.Consultation or ServiceCategory.Miscellaneous,
                SacCode = "999312",
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        if (options.Value.SeedDemoData)
        {
            await SeedDemoPractitionersAsync(db, facility, specialties, departments, cancellationToken);
        }
    }

    private static async Task SeedDemoPractitionersAsync(
        HospitalDbContext db, Facility facility, Dictionary<string, Guid> specialties, Dictionary<string, Guid> departments, CancellationToken ct)
    {
        if (await db.Set<Practitioner>().AnyAsync(ct))
        {
            return;
        }

        var demo = new (string Code, string First, string Last, Gender Gender, string Qualification, string Specialty, string Department, decimal Fee, bool Tele)[]
        {
            ("DR001", "Arjun", "Mehta", Gender.Male, "MBBS, MD (General Medicine)", "GM", "GM", 600m, true),
            ("DR002", "Kavya", "Nair", Gender.Female, "MBBS, MD (Paediatrics)", "PED", "PED", 500m, true),
            ("DR003", "Rahul", "Verma", Gender.Male, "MBBS, MS (Orthopaedics)", "ORTH", "ORTH", 700m, false),
            ("DR004", "Sneha", "Iyer", Gender.Female, "MBBS, MS (OBG), DNB", "OBG", "OBG", 700m, true),
            ("DR005", "Vikram", "Rao", Gender.Male, "MBBS, MD, DM (Cardiology)", "CARD", "CARD", 900m, true),
            ("DR006", "Meera", "Pillai", Gender.Female, "MBBS, MS (General Surgery)", "GS", "GS", 700m, false),
        };

        foreach (var d in demo)
        {
            db.Add(new Practitioner
            {
                Code = d.Code,
                Title = "Dr.",
                FirstName = d.First,
                LastName = d.Last,
                DisplayName = Practitioner.ComposeDisplayName("Dr.", d.First, d.Last),
                Gender = d.Gender,
                Qualification = d.Qualification,
                RegistrationCouncil = "State Medical Council",
                RegistrationNumber = "REG-" + d.Code,
                SpecialtyId = specialties.GetValueOrDefault(d.Specialty),
                DepartmentId = departments.GetValueOrDefault(d.Department),
                Kind = PractitionerKind.Consultant,
                ConsultationFeeNew = d.Fee,
                ConsultationFeeFollowUp = Money.Round(d.Fee * 0.6m),
                Languages = ["English", "Hindi"],
                AvailableForOnlineBooking = true,
                AvailableForTelehealth = d.Tele,
                FacilityIds = [facility.Id],
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
