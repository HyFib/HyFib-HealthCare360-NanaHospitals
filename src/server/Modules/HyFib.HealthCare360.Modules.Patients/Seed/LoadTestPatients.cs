using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Patients.Seed;

/// <summary>
/// Synthetic patients for load tests: patient search is to answer within 500 ms over a million patients (SRS PERF-02),
/// which only a database that holds that many can show. Their UHIDs start with <see cref="UhidPrefix"/>, so they never
/// take a number from the hospital's series and are told apart at a glance, and registering them raises no event: no
/// welcome message goes anywhere. Only on a server set up with demonstration data (<c>HealthCare360:SeedDemoData</c>),
/// never among a hospital's real records. <c>HyFib.HealthCare360.Server.exe loadtest patients --count N</c> runs it.
/// </summary>
public sealed class LoadTestPatients(HospitalDbContext db, IOptions<PlatformOptions> options, TimeProvider time)
{
    public const string UhidPrefix = "LT";

    /// <summary>The note on every synthetic patient.</summary>
    public const string Note = "Synthetic patient for load tests.";

    /// <summary>Patients saved at a time: large enough to be quick, small enough to keep memory flat.</summary>
    public const int BatchSize = 2000;

    /// <summary>
    /// Common given names and surnames from across India. With a middle initial they make some 90,000 different names,
    /// so a search by name finds a page of people, as in a large hospital.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, Gender Gender)> FirstNames =
    [
        ("Aarav", Gender.Male), ("Abdul", Gender.Male), ("Aditya", Gender.Male), ("Ajay", Gender.Male), ("Akash", Gender.Male),
        ("Amit", Gender.Male), ("Anand", Gender.Male), ("Anil", Gender.Male), ("Arjun", Gender.Male), ("Arun", Gender.Male),
        ("Ashok", Gender.Male), ("Balaji", Gender.Male), ("Deepak", Gender.Male), ("Dinesh", Gender.Male), ("Ganesh", Gender.Male),
        ("Gopal", Gender.Male), ("Harish", Gender.Male), ("Imran", Gender.Male), ("Jagdish", Gender.Male), ("Joseph", Gender.Male),
        ("Karthik", Gender.Male), ("Kiran", Gender.Male), ("Krishna", Gender.Male), ("Mahesh", Gender.Male), ("Manoj", Gender.Male),
        ("Mohammed", Gender.Male), ("Murugan", Gender.Male), ("Naveen", Gender.Male), ("Prakash", Gender.Male), ("Rahul", Gender.Male),
        ("Rajesh", Gender.Male), ("Ramesh", Gender.Male), ("Ravi", Gender.Male), ("Sanjay", Gender.Male), ("Senthil", Gender.Male),
        ("Suresh", Gender.Male), ("Thomas", Gender.Male), ("Venkat", Gender.Male), ("Vijay", Gender.Male), ("Vikram", Gender.Male),
        ("Aisha", Gender.Female), ("Ananya", Gender.Female), ("Anjali", Gender.Female), ("Deepa", Gender.Female), ("Divya", Gender.Female),
        ("Fatima", Gender.Female), ("Geetha", Gender.Female), ("Kavitha", Gender.Female), ("Lakshmi", Gender.Female), ("Mary", Gender.Female),
        ("Meena", Gender.Female), ("Nandini", Gender.Female), ("Neha", Gender.Female), ("Pooja", Gender.Female), ("Priya", Gender.Female),
        ("Radha", Gender.Female), ("Rekha", Gender.Female), ("Revathi", Gender.Female), ("Saranya", Gender.Female), ("Shalini", Gender.Female),
        ("Shanthi", Gender.Female), ("Sneha", Gender.Female), ("Sunita", Gender.Female), ("Swathi", Gender.Female), ("Uma", Gender.Female),
        ("Vani", Gender.Female), ("Vidya", Gender.Female), ("Yamuna", Gender.Female), ("Zainab", Gender.Female), ("Kamala", Gender.Female),
    ];

    public static readonly IReadOnlyList<string> LastNames =
    [
        "Agarwal", "Ahmed", "Bhat", "Chandran", "Das", "Desai", "Devi", "Fernandes", "Ganesan", "Gupta",
        "Iyer", "Jain", "Joshi", "Khan", "Kumar", "Krishnan", "Mathew", "Menon", "Mishra", "Mukherjee",
        "Naidu", "Nair", "Narayanan", "Patel", "Pillai", "Prasad", "Rao", "Reddy", "Sharma", "Singh",
        "Subramanian", "Thomas", "Varghese", "Verma", "Yadav", "Rajan", "Murthy", "Banerjee", "Begum", "Hegde",
        "Kaur", "Kulkarni", "Shetty", "Srinivasan", "Chopra", "Saxena", "Pandey", "Tiwari", "Raman", "Pandian",
    ];

    private static readonly string[] Cities =
        ["Chennai", "Bengaluru", "Hyderabad", "Mumbai", "Delhi", "Kolkata", "Pune", "Kochi", "Coimbatore", "Madurai", "Patna", "Jaipur"];

    /// <summary>
    /// Synthetic patient number <paramref name="number"/> (1 for the first): the same each time for the same number, so a
    /// test knows what it will find. The phone numbers are unique, from +919000000001.
    /// </summary>
    public static Patient Create(long number, Guid facilityId, DateOnly today, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        var (first, gender) = FirstNames[(int)(number % FirstNames.Count)];
        var last = LastNames[(int)(number / FirstNames.Count % LastNames.Count)];
        var initial = (char)('A' + (number / (FirstNames.Count * LastNames.Count) % 26));
        var ageDays = (int)(number * 7919 % (90 * 365));
        var patient = new Patient
        {
            Uhid = Uhid(number),
            Gender = gender,
            DateOfBirth = today.AddDays(-ageDays),
            Phone = "+91" + (9_000_000_000L + number).ToString(CultureInfo.InvariantCulture),
            Address = new Address { City = Cities[(int)(number % Cities.Length)], Country = "India" },
            RegisteredFacilityId = facilityId,
            RegisteredAt = now.AddMinutes(-(number * 13 % (5 * 365 * 24 * 60))),
            Category = ageDays >= 60 * 365 ? PatientCategory.SeniorCitizen : PatientCategory.General,
            Notes = Note,
        };
        patient.SetName(null, first, initial.ToString(), last);
        return patient;
    }

    /// <summary>The UHID of synthetic patient <paramref name="number"/>: LT and nine digits.</summary>
    public static string Uhid(long number) => UhidPrefix + number.ToString("D9", CultureInfo.InvariantCulture);

    /// <summary>
    /// Adds <paramref name="count"/> synthetic patients after those already there, and returns how many there are now.
    /// <paramref name="progress"/> hears the running total after each batch.
    /// </summary>
    public async Task<Result<long>> AddAsync(long count, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (!options.Value.SeedDemoData)
        {
            return Error.Rule("loadtest.not_demo",
                "Synthetic patients are added only on a server set up with demonstration data (HealthCare360:SeedDemoData), never among a hospital's real records.");
        }

        if (count is < 1 or > 10_000_000)
        {
            return Error.Field("count", "Give a number of patients from 1 to 10,000,000.");
        }

        var facilityId = await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(cancellationToken);
        if (facilityId is null)
        {
            return Error.Rule("loadtest.no_facility", "The server has no facility yet: start it once so it sets one up.");
        }

        var last = await db.Set<Patient>().IgnoreQueryFilters()
            .Where(p => p.Uhid.StartsWith(UhidPrefix))
            .OrderByDescending(p => p.Uhid)
            .Select(p => p.Uhid)
            .FirstOrDefaultAsync(cancellationToken);
        var start = last is not null && long.TryParse(last.AsSpan(UhidPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

        var today = time.Today();
        var now = time.GetUtcNow();
        var tracking = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            for (var done = 0L; done < count;)
            {
                var size = (int)Math.Min(BatchSize, count - done);
                for (var i = 1; i <= size; i++)
                {
                    db.Add(Create(start + done + i, facilityId.Value, today, now));
                }

                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
                done += size;
                progress?.Report(start + done);
            }
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = tracking;
        }

        return start + count;
    }
}
