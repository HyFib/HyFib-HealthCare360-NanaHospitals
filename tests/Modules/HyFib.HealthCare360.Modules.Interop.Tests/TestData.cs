using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.Primitives;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Fixed ids and instants for the tests. "Now" is Friday 25 September 2026, 10:30 IST (05:00 UTC).
/// </summary>
internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 5, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 9, 25);

    public static readonly Guid PatientId = Guid.Parse("01923a4b-0000-7000-8000-000000000001");
    public static readonly Guid OtherPatientId = Guid.Parse("01923a4b-0000-7000-8000-000000000002");
    public static readonly Guid FacilityId = Guid.Parse("01923a4b-0000-7000-8000-0000000000fa");
    public static readonly Guid DoctorId = Guid.Parse("01923a4b-0000-7000-8000-0000000000d1");
    public static readonly Guid DepartmentId = Guid.Parse("01923a4b-0000-7000-8000-0000000000de");

    public static ReferenceDirectory Directory { get; } = new()
    {
        Practitioners = new Dictionary<Guid, PractitionerInfo> { [DoctorId] = new("Dr. Asha Rao", AbdmTestData.HprId) },
        Departments = new Dictionary<Guid, string> { [DepartmentId] = "General Medicine" },
        Facilities = new Dictionary<Guid, FacilityInfo> { [FacilityId] = new("Nana Hospitals, Pune", "IN2710000123") },
    };

    public static PatientRow Patient() => new()
    {
        Id = PatientId,
        Uhid = "NH-UH-2600123",
        Title = "Mrs",
        FirstName = "Meera",
        MiddleName = "Anil",
        LastName = "Kulkarni",
        FullName = "Meera Anil Kulkarni",
        Gender = Gender.Female,
        DateOfBirth = new DateOnly(1984, 4, 12),
        MaritalStatus = MaritalStatus.Married,
        Phone = "+919876543210",
        Email = "meera@example.org",
        Address = new Address { Line1 = "12 MG Road", City = "Pune", District = "Pune", State = "Maharashtra", PinCode = "411001" },
        RegisteredFacilityId = FacilityId,
        CreatedAt = Now.AddYears(-1),
        UpdatedAt = Now.AddDays(-3),
    };

    /// <summary>Parses a query string ("a=1&amp;b=2") into the parameter pairs the endpoints receive.</summary>
    public static IEnumerable<KeyValuePair<string, StringValues>> Query(string query) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query).Select(kv => new KeyValuePair<string, StringValues>(kv.Key, kv.Value));

    public static SearchRequest Search(string type, string query)
    {
        var parsed = SearchRequestParser.Parse(SearchDefinitions.Find(type)!, Query(query));
        parsed.Error.ShouldBeNull(parsed.Error?.Outcome.Issue[0].Diagnostics);
        return parsed.Request!;
    }
}
