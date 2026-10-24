using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// MRD keeps files that exist only on paper (episodes from before the system, or written by hand while it was down): a
/// chart with the file's number and the episode's days, found by that number, issued and returned, checked by hand
/// (there is no record to scan), completed with its retention counted from the episode, and archived.
/// </summary>
public sealed class PaperChartTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_paper_file_is_charted_tracked_and_kept_from_its_episode()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var patient = await RegisterPatientAsync(frontOffice);
        var number = $"ip/{Random.Shared.Next(1990, 2020)}/{Random.Shared.Next(100_000, 999_999)}";
        var admitted = new DateOnly(2016, 3, 2);
        var discharged = new DateOnly(2016, 3, 9);

        var chart = await mrd.PostAsync<MedicalRecordDto>("/api/v1/mrd/records", PaperChart(patient, number, admitted, discharged));

        chart.Paper.ShouldNotBeNull();
        chart.Paper.FileNumber.ShouldBe(number.ToUpperInvariant());
        chart.Paper.IsInpatient.ShouldBeTrue();
        chart.EncounterId.ShouldBeNull();
        chart.AdmissionId.ShouldBeNull();

        // One chart per file: the same number again, in any case, is refused.
        using (var again = await mrd.SendRawAsync(HttpMethod.Post, "/api/v1/mrd/records", PaperChart(patient, number.ToUpperInvariant(), admitted, discharged)))
        {
            (await CodeAsync(again, HttpStatusCode.Conflict)).ShouldBe("chart.paper_exists");
        }

        // MRD finds the file by its number.
        var found = await mrd.GetAsync<PagedResult<MedicalRecordDto>>($"/api/v1/mrd/records?search={Uri.EscapeDataString(number)}");
        found.Items.ShouldHaveSingleItem().Id.ShouldBe(chart.Id);

        // Lent to a ward and returned, like any file.
        var request = await mrd.PostAsync<RecordRequestDto>("/api/v1/mrd/record-requests",
            new CreateRecordRequestRequest(patient, chart.Id, "Dr. Meera Iyer", "Surgery", RecordRequestPurpose.PatientCare, "Old operation notes"));
        (await mrd.PostAsync<RecordRequestDto>($"/api/v1/mrd/record-requests/{request.Id}/issue", new IssueRecordRequest(null))).Status
            .ShouldBe(RecordRequestStatus.Issued);
        (await mrd.PostAsync<RecordRequestDto>($"/api/v1/mrd/record-requests/{request.Id}/return")).Status.ShouldBe(RecordRequestStatus.Returned);

        // Nothing electronic to scan: MRD checks the file and raises what is missing by hand.
        using (var scan = await mrd.SendRawAsync(HttpMethod.Post, $"/api/v1/mrd/records/{chart.Id}/scan"))
        {
            (await CodeAsync(scan, HttpStatusCode.UnprocessableEntity)).ShouldBe("chart.paper");
        }

        var deficient = await mrd.PostAsync<MedicalRecordDto>($"/api/v1/mrd/records/{chart.Id}/deficiencies",
            new AddDeficiencyRequest(DeficiencyType.MissingConsent, "Consent for surgery is not in the file.", null, null));
        await mrd.PostAsync<MedicalRecordDto>($"/api/v1/mrd/records/deficiencies/{deficient.Deficiencies.Single().Id}/resolve",
            new ResolveDeficiencyRequest("Found in volume 2."));

        var complete = await mrd.PostAsync<MedicalRecordDto>($"/api/v1/mrd/records/{chart.Id}/complete");
        complete.RetainUntil.ShouldBe(discharged.AddYears(await InpatientYearsAsync()));

        var archived = await mrd.PostAsync<MedicalRecordDto>($"/api/v1/mrd/records/{chart.Id}/archive", new ArchiveMedicalRecordRequest("Rack 12, shelf C"));
        archived.Status.ShouldBe(MedicalRecordStatus.Archived);
        archived.Paper.ShouldNotBeNull().FileNumber.ShouldBe(number.ToUpperInvariant());
    }

    [Fact]
    public async Task A_paper_chart_is_of_a_file_only_and_its_days_add_up()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var patient = await RegisterPatientAsync(frontOffice);
        var encounterId = Guid.CreateVersion7();

        (await FieldErrorsAsync(mrd, PaperChart(patient, "OP-1", new DateOnly(2018, 1, 1), null) with { EncounterId = encounterId })).ShouldContainKey("paper");
        (await FieldErrorsAsync(mrd, PaperChart(patient, "OP-2", new DateOnly(2018, 1, 1), new DateOnly(2018, 1, 2), inpatient: false)))
            .ShouldContainKey("paper.episodeTo");
        (await FieldErrorsAsync(mrd, PaperChart(patient, "IP-3", new DateOnly(2018, 1, 5), new DateOnly(2018, 1, 2)))).ShouldContainKey("paper.episodeTo");
        (await FieldErrorsAsync(mrd, PaperChart(patient, "IP 4", new DateOnly(2018, 1, 5), new DateOnly(2018, 1, 9)))).ShouldContainKey("paper.fileNumber");

        // A stay is kept from its discharge, so it must have one; and every episode has its day.
        (await FieldErrorsAsync(mrd, PaperChart(patient, "IP-5", new DateOnly(2018, 1, 5), null))).ShouldContainKey("paper.episodeTo");
        (await FieldErrorsAsync(mrd, PaperChart(patient, "OP-6", default, null, inpatient: false))).ShouldContainKey("paper.episodeFrom");
    }

    [Fact]
    public async Task Only_medical_records_staff_chart_a_paper_file()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await RegisterPatientAsync(frontOffice);

        using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/mrd/records",
            PaperChart(patient, $"OP/{Random.Shared.Next(100_000, 999_999)}", new DateOnly(2019, 6, 1), null, inpatient: false));

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static CreateMedicalRecordRequest PaperChart(Guid patientId, string number, DateOnly from, DateOnly? to, bool inpatient = true) =>
        new(patientId, EncounterId: null, AdmissionId: null, VisitId: null, AssignedPractitionerId: null, DueDate: null, IsMlc: false,
            new PaperChartDetails(number, inpatient, from, to, "Two volumes, General surgery"));

    private async Task<int> InpatientYearsAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await RecordRetention.YearsAsync(scope.ServiceProvider.GetRequiredService<ISettingsService>(), inpatient: true, Ct);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    /// <summary>The fields a refused request names, by their JSON path.</summary>
    private static async Task<Dictionary<string, string[]>> FieldErrorsAsync(ApiClient mrd, CreateMedicalRecordRequest request)
    {
        using var response = await mrd.SendRawAsync(HttpMethod.Post, "/api/v1/mrd/records", request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.GetProperty("errors").EnumerateObject()
            .ToDictionary(e => e.Name, e => e.Value.EnumerateArray().Select(m => m.GetString() ?? "").ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A new patient; the random surname, date of birth and mobile number keep the duplicate check quiet.</summary>
    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice)
    {
        var surname = Letters(8);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Lata"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
