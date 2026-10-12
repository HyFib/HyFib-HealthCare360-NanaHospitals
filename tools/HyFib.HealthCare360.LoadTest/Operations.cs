using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace HyFib.HealthCare360.LoadTest;

/// <summary>What an operation does to the server, which decides its target (SRS §7.3).</summary>
internal enum OperationKind
{
    /// <summary>PERF-01: p95 within 300 ms.</summary>
    Read,

    /// <summary>PERF-02: patient search within 500 ms (over a million patients).</summary>
    Search,

    /// <summary>PERF-01: p95 within 600 ms.</summary>
    Write,
}

/// <summary>One step a front-office or clinical user takes, and how often, among the others.</summary>
internal sealed record Operation(string Name, OperationKind Kind, int Weight, Func<OperationContext, HttpRequestMessage> Request)
{
    public static TimeSpan TargetOf(OperationKind kind) => kind switch
    {
        OperationKind.Read => TimeSpan.FromMilliseconds(300),
        OperationKind.Search => TimeSpan.FromMilliseconds(500),
        _ => TimeSpan.FromMilliseconds(600),
    };

    public static string RequirementOf(OperationKind kind) => kind == OperationKind.Search ? "PERF-02" : "PERF-01";
}

/// <summary>What a virtual user's next request may use: patients the server has, and a source of chance.</summary>
internal sealed class OperationContext(IReadOnlyList<Guid> patients, Random random, Func<long> nextNumber)
{
    public Random Random => random;

    public Guid AnyPatient() => patients[random.Next(patients.Count)];

    /// <summary>A number no other request of this run has had, for unique phone numbers.</summary>
    public long NextNumber() => nextNumber();

    public T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];
}

internal static class Operations
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Names common among the synthetic patients (<c>loadtest patients</c>) and in any hospital's register.</summary>
    private static readonly string[] Names =
    [
        "Kumar", "Priya", "Ravi Kumar", "Lakshmi", "Sharma", "Mohammed", "Reddy", "Devi", "Suresh Nair", "Meena",
        "Anand Rao", "Fatima", "Joseph", "Iyer", "Kavitha", "Singh", "Patel", "Venkat", "Shanthi", "Gupta",
    ];

    /// <summary>The mix: about one search in three, one change in ten, as at a registration and OPD counter.</summary>
    public static IReadOnlyList<Operation> All { get; } =
    [
        new("Search patients by name", OperationKind.Search, 22, c => Get($"api/v1/patients?search={Uri.EscapeDataString(c.Pick(Names))}&pageSize=25")),
        // The first million synthetic patients have the mobile numbers 9000000001 onwards.
        new("Search patients by phone", OperationKind.Search, 8, c => Get(
            $"api/v1/patients?search=9{c.Random.Next(1, 1_000_001).ToString("D9", CultureInfo.InvariantCulture)}&pageSize=25")),
        // LT000012 begins the UHIDs of synthetic patients 12,000 to 12,999.
        new("Search patients by UHID", OperationKind.Search, 5, c => Get(
            $"api/v1/patients?search=LT0000{c.Random.Next(0, 100).ToString("D2", CultureInfo.InvariantCulture)}&pageSize=25")),
        new("Open a patient", OperationKind.Read, 15, c => Get($"api/v1/patients/{c.AnyPatient()}")),
        new("Patient banner", OperationKind.Read, 15, c => Get($"api/v1/patients/{c.AnyPatient()}/summary")),
        new("A patient's visits", OperationKind.Read, 8, c => Get($"api/v1/patients/{c.AnyPatient()}/visits")),
        new("Today's appointments", OperationKind.Read, 8, c =>
            Get($"api/v1/scheduling/appointments?from={Today()}&to={Today()}&pageSize=25")),
        new("Visits", OperationKind.Read, 6, _ => Get("api/v1/visits?pageSize=25")),
        new("Who am I", OperationKind.Read, 3, _ => Get("api/v1/auth/me")),
        new("Register a patient", OperationKind.Write, 10, Register),
    ];

    /// <summary>Lists whose largest page must stay under 100 KB as sent, compressed (PERF-05).</summary>
    public static IReadOnlyList<(string Name, string Path)> Lists { get; } =
    [
        ("Patients", "api/v1/patients?pageSize=200"),
        ("Patient search", "api/v1/patients?search=Kumar&pageSize=200"),
        ("Visits", "api/v1/visits?pageSize=200"),
        ("Appointments", "api/v1/scheduling/appointments?pageSize=200"),
    ];

    /// <summary>An operation chosen by weight.</summary>
    public static Operation Choose(IReadOnlyList<Operation> operations, Random random)
    {
        var roll = random.Next(operations.Sum(o => o.Weight));
        foreach (var operation in operations)
        {
            roll -= operation.Weight;
            if (roll < 0)
            {
                return operation;
            }
        }

        return operations[^1];
    }

    /// <summary>A new patient as the registration screen sends one, with a phone number no one else has.</summary>
    internal static HttpRequestMessage Register(OperationContext context)
    {
        var number = context.NextNumber();
        var phone = "8" + (number % 1_000_000_000).ToString("D9", CultureInfo.InvariantCulture);
        var first = context.Pick(Names).Split(' ')[0];
        return new HttpRequestMessage(HttpMethod.Post, "api/v1/patients")
        {
            Content = JsonContent.Create(
                new
                {
                    firstName = first,
                    lastName = "Loadtest",
                    gender = context.Random.Next(2) == 0 ? "Male" : "Female",
                    ageYears = context.Random.Next(1, 90),
                    phone,
                    address = new { city = "Chennai", country = "India" },
                    notes = "Registered by the load test.",
                    isVip = false,
                    confirmedNotDuplicate = true,
                },
                options: Json),
        };
    }

    private static HttpRequestMessage Get(string path) => new(HttpMethod.Get, path);

    private static string Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
