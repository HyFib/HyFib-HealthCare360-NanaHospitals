using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Patients.Seed;

/// <summary>Demonstration patients and today's OP queue (only when <c>HealthCare360:SeedDemoData</c> is true).</summary>
internal sealed class PatientsSeeder(IOptions<PlatformOptions> options, INumberSeries numbers, TimeProvider time) : ISeeder
{
    public int Order => 100;

    private static readonly (string First, string? Last, Gender Gender, int Age, string Phone, string City, BloodGroup Blood)[] Demo =
    [
        ("Ravi", "Kumar", Gender.Male, 45, "9876543210", "Chennai", BloodGroup.BPositive),
        ("Lakshmi", "Narayanan", Gender.Female, 62, "9845012345", "Chennai", BloodGroup.OPositive),
        ("Aarav", "Sharma", Gender.Male, 7, "9811122233", "Bengaluru", BloodGroup.APositive),
        ("Priya", "Menon", Gender.Female, 29, "9900011122", "Kochi", BloodGroup.ABPositive),
        ("Mohammed", "Irfan", Gender.Male, 38, "9791234567", "Hyderabad", BloodGroup.ONegative),
        ("Sunita", "Devi", Gender.Female, 51, "9701234567", "Patna", BloodGroup.BNegative),
        ("Karthik", "Subramanian", Gender.Male, 33, "9884455667", "Coimbatore", BloodGroup.OPositive),
        ("Ananya", "Reddy", Gender.Female, 24, "9866677788", "Vijayawada", BloodGroup.APositive),
        ("Joseph", "Mathew", Gender.Male, 70, "9447788990", "Kottayam", BloodGroup.BPositive),
        ("Fatima", "Begum", Gender.Female, 44, "9885566778", "Hyderabad", BloodGroup.OPositive),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        if (!options.Value.SeedDemoData || await db.Set<Patient>().AnyAsync(cancellationToken))
        {
            return;
        }

        var facility = await db.Set<Facility>().OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (facility is null)
        {
            return;
        }

        var today = time.Today();
        var now = time.GetUtcNow();
        var patients = new List<Patient>();
        foreach (var d in Demo)
        {
            var patient = new Patient
            {
                Gender = d.Gender,
                DateOfBirth = today.AddYears(-d.Age).AddDays(-37 * d.Age % 300),
                Phone = Phone.Normalize(d.Phone),
                BloodGroup = d.Blood,
                Address = new Address { City = d.City, Country = "India" },
                RegisteredFacilityId = facility.Id,
                RegisteredAt = now.AddDays(-d.Age),
                Category = d.Age >= 60 ? PatientCategory.SeniorCitizen : PatientCategory.General,
            };
            patient.SetName(d.Gender == Gender.Male ? "Mr." : "Ms.", d.First, null, d.Last);
            patient.Uhid = await numbers.NextAsync(PatientRegistrationService.UhidSeries, null, PatientRegistrationService.UhidFormat, NumberResetPolicy.Never, cancellationToken);
            db.Add(patient);
            patients.Add(patient);
        }

        await db.SaveChangesAsync(cancellationToken);

        var department = await db.Set<Department>().FirstOrDefaultAsync(d => d.FacilityId == facility.Id && d.Code == "GM", cancellationToken);
        var doctor = await db.Set<Practitioner>().Where(p => p.DepartmentId == (department == null ? null : department.Id)).FirstOrDefaultAsync(cancellationToken);
        if (department is null)
        {
            return;
        }

        var sequence = 0;
        foreach (var patient in patients.Take(5))
        {
            sequence++;
            var visit = new Visit
            {
                FacilityId = facility.Id,
                VisitNumber = await numbers.NextAsync(VisitService.VisitSeries, facility.Id, VisitService.VisitFormat, NumberResetPolicy.Yearly, cancellationToken),
                PatientId = patient.Id,
                Type = VisitType.OutPatient,
                Status = VisitStatus.Open,
                DepartmentId = department.Id,
                PractitionerId = doctor?.Id,
                VisitedAt = now.AddMinutes(-60 + (sequence * 8)),
                ChiefComplaint = sequence switch { 1 => "Fever and cough for 3 days", 2 => "Knee pain", 3 => "Routine diabetes review", 4 => "Headache", _ => "Follow-up" },
            };
            db.Add(visit);
            db.Add(new QueueToken
            {
                FacilityId = facility.Id,
                VisitId = visit.Id,
                PatientId = patient.Id,
                DepartmentId = department.Id,
                PractitionerId = doctor?.Id,
                TokenDate = today,
                Sequence = int.Parse(await numbers.NextAsync($"token:{department.Id:N}", facility.Id, "{SEQ}", NumberResetPolicy.Daily, cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture),
                Label = $"{department.TokenPrefix ?? department.Code}-{sequence:000}",
                Status = TokenStatus.Waiting,
                Priority = patient.Category == PatientCategory.SeniorCitizen ? TokenPriority.Priority : TokenPriority.Normal,
                IssuedAt = visit.VisitedAt,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
