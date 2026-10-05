using System.Text.Json;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The modality worklist as PostgreSQL filters it (IMG-01): patient name starts, UHID wildcards, start-time windows,
/// nearest-to-now ordering and paging, so the worklist server never loses matching entries behind a page of others.
/// </summary>
public sealed class ModalityWorklistSourceTests(HospitalServer server)
{
    [Fact]
    public async Task Filters_names_ids_and_times_in_the_database_and_pages_nearest_to_now_first()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var facilityId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
        var surname = "Wl" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))
            .ToArray());
        var withSurname = await RegisterPatientAsync("Kavitha", surname);
        var mononym = await RegisterPatientAsync(surname + "x", null);
        var now = DateTimeOffset.UtcNow;
        var today = DiagnosticsTime.LocalDate(now);
        var dayStart = DiagnosticsTime.StartOfDayUtc(today);

        var earlier = await AddStudyAsync(facilityId, withSurname.Id, now.AddHours(-3));
        var soon = await AddStudyAsync(facilityId, withSurname.Id, now.AddHours(1));
        var justNow = await AddStudyAsync(facilityId, withSurname.Id, now.AddMinutes(-30));
        var atThree = await AddStudyAsync(facilityId, mononym.Id, dayStart.AddHours(3));
        var atFive = await AddStudyAsync(facilityId, mononym.Id, dayStart.AddHours(5));
        var all = new ModalityWorklistQuery(facilityId, today.AddDays(-1), today.AddDays(1), ["CT"]);

        // The family name start finds the surname and the whole name of a patient registered without one.
        var byName = await FindAsync(all with { PatientNamePrefix = surname.ToUpperInvariant() });
        byName.Select(i => i.StudyId)
            .ShouldBe([earlier.Id, soon.Id, justNow.Id, atThree.Id, atFive.Id], ignoreOrder: true);
        (await FindAsync(all with { PatientNamePrefix = surname + "x" })).Select(i => i.StudyId)
            .ShouldBe([atThree.Id, atFive.Id], ignoreOrder: true);

        // A UHID pattern, with LIKE's own wildcards taken literally.
        (await FindAsync(all with { PatientId = "?" + withSurname.Uhid[1..] })).Select(i => i.StudyId)
            .ShouldBe([earlier.Id, soon.Id, justNow.Id], ignoreOrder: true);
        (await FindAsync(all with { PatientId = mononym.Uhid[..2].ToLowerInvariant() + "*" + mononym.Uhid[2..] }))
            .Select(i => i.StudyId).ShouldBe([atThree.Id, atFive.Id], ignoreOrder: true);
        (await FindAsync(all with { PatientId = withSurname.Uhid[..^2] + "%*" })).ShouldBeEmpty();

        // Nearest to now first, page by page.
        var near = all with { PatientId = withSurname.Uhid, Around = now, Limit = 2 };
        (await FindAsync(near)).Select(i => i.StudyId).ShouldBe([justNow.Id, soon.Id]);
        (await FindAsync(near with { Offset = 2 })).Select(i => i.StudyId).ShouldBe([earlier.Id]);

        // Start times on the day, in hospital time, to the end of the last second.
        var window = new ModalityWorklistQuery(facilityId, today, today, ["CT"], PatientId: mononym.Uhid)
        {
            FromTime = new TimeOnly(2, 30),
            ToTime = new TimeOnly(3, 0),
        };
        (await FindAsync(window)).Select(i => i.StudyId).ShouldBe([atThree.Id]);
        (await FindAsync(window with { FromTime = new TimeOnly(3, 0, 1), ToTime = null })).Select(i => i.StudyId)
            .ShouldBe([atFive.Id]);
    }

    private async Task<IReadOnlyList<ModalityWorklistItem>> FindAsync(ModalityWorklistQuery query)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModalityWorklistSource>()
            .FindAsync(query, TestContext.Current.CancellationToken);
    }

    private async Task<PatientCreated> RegisterPatientAsync(string firstName, string? lastName)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var phone = "9" + Digits();
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: firstName, MiddleName: null, LastName: lastName, Gender: Gender.Female,
            DateOfBirth: new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(15_000)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
            PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null,
            PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
    }

    /// <summary>A CT study scheduled at <paramref name="at"/>.</summary>
    private async Task<ImagingStudy> AddStudyAsync(Guid facilityId, Guid patientId, DateTimeOffset at)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var study = new ImagingStudy
        {
            FacilityId = facilityId,
            PatientId = patientId,
            AccessionNumber = "WL" + Digits(),
            ServiceCode = "RAD-CT-HEAD",
            StudyName = "CT brain (plain)",
            Modality = Modality.CT,
            Priority = OrderPriority.Routine,
            OrderedAt = at.AddDays(-1),
            ScheduledAt = at,
            Status = ImagingStudyStatus.Scheduled,
            StudyInstanceUid = DicomConventions.NewUid(),
        };
        db.Add(study);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return study;
    }

    private static string Digits() =>
        Random.Shared.Next(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
