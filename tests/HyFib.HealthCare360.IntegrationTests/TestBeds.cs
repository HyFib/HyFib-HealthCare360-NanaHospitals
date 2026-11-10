using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A bed that the hospital admin adds to a ward for one admission of a test. The run's tests share the seeded wards' beds
/// (twelve in each general ward), and many of their admissions leave the bed for cleaning, so a free bed of a ward cannot be
/// counted on: which tests ran first decides it, and on Windows CI the theatre tests found none. A bed added for the
/// admission is free whatever ran before, and in the ward the test asks for, so its tariff and rules are the ward's.
/// </summary>
internal static class TestBeds
{
    /// <summary>A new, vacant bed of the ward with this code (GWM, GWF, SPW, PVT, DLX, ICU or NICU).</summary>
    public static async Task<TestBed> AddAsync(HospitalServer server, string wardCode)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var ward = (await admin.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == wardCode);
        return await AddAsync(admin, ward.Id);
    }

    /// <summary>A new, vacant bed of the ward.</summary>
    public static async Task<TestBed> AddAsync(HospitalServer server, Guid wardId) =>
        await AddAsync(await server.SignInAsync(HospitalServer.Users.HospitalAdmin), wardId);

    /// <summary>A new, vacant bed of the ward, added with a client of the hospital admin's.</summary>
    public static async Task<TestBed> AddAsync(ApiClient admin, Guid wardId)
    {
        var bed = await admin.PostAsync<BedDto>("/api/v1/beds", new SaveBedRequest(
            wardId, null, "T-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), false, false, false, 999, true, null));
        bed.Status.ShouldBe(BedStatus.Vacant);
        return new TestBed(bed.Id, bed.Label);
    }
}

/// <summary>A bed added for a test: its id, and the label the bed board and the kitchen's lists show.</summary>
internal sealed record TestBed(Guid BedId, string Label);
