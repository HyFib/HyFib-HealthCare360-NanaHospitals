using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The disease (morbidity) index of medical records (MRD-06) through the API, as the MRD officer reads it: by ICD code,
/// block or chapter, from the doctors' diagnoses or from MRD's coding, counting each episode and each patient once, by
/// sex and by age at the episode. A differential diagnosis is not a morbidity, nor is an episode not yet coded. Other
/// tests record diagnoses too, so each report is read before and after, and the difference checked.
/// </summary>
public sealed class MorbidityIndexTests(HospitalServer server)
{
    private const string Dengue = "A90";
    private const string Malaria = "B50.9";
    private const string Typhoid = "A01.0";

    [Fact]
    public async Task The_clinical_index_counts_each_episode_and_patient_once_by_code_block_and_chapter()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        Task<MorbidityReport> ReportAsync(string groupBy) => IndexAsync(mrd, today, today, groupBy, "clinical");
        var (codesBefore, blocksBefore, chaptersBefore) = (await ReportAsync("code"), await ReportAsync("block"), await ReportAsync("chapter"));

        // A boy of 10 with dengue; a woman of 30 with dengue and malaria together, and back with dengue again; a man of 70
        // with malaria, typhoid only considered.
        var boy = await PatientAsync("Arjun", Gender.Male, today.AddYears(-10));
        var woman = await PatientAsync("Kavya", Gender.Female, today.AddYears(-30));
        var man = await PatientAsync("Harish", Gender.Male, today.AddYears(-70));
        await EncounterAsync(doctor, boy, (Dengue, DiagnosisType.Provisional));
        await EncounterAsync(doctor, woman, (Dengue, DiagnosisType.Final), (Malaria, DiagnosisType.Final));
        await EncounterAsync(doctor, woman, (Dengue, DiagnosisType.Final));
        await EncounterAsync(doctor, man, (Malaria, DiagnosisType.Final), (Typhoid, DiagnosisType.Differential));

        var codes = await ReportAsync("code");
        var byCode = Difference(codesBefore, codes);
        byCode[Dengue].ShouldBe(new Change(Episodes: 3, Patients: 2, Male: 1, Female: 2, Age0To14: 1, Age15To44: 2, Age65Plus: 0));
        byCode[Malaria].ShouldBe(new Change(Episodes: 2, Patients: 2, Male: 1, Female: 1, Age0To14: 0, Age15To44: 1, Age65Plus: 1));
        byCode.Keys.ShouldBe([Dengue, Malaria], ignoreOrder: true, "A differential diagnosis is not counted.");
        (codes.TotalEpisodes - codesBefore.TotalEpisodes).ShouldBe(4);

        // By block and by chapter, an episode with two codes of a group is one episode of it.
        var byBlock = Difference(blocksBefore, await ReportAsync("block"));
        (byBlock["A90-A99"].Episodes, byBlock["A90-A99"].Patients).ShouldBe((3, 2));
        (byBlock["B50-B64"].Episodes, byBlock["B50-B64"].Patients).ShouldBe((2, 2));
        var byChapter = Difference(chaptersBefore, await ReportAsync("chapter"));
        byChapter["I"].ShouldBe(new Change(Episodes: 4, Patients: 3, Male: 2, Female: 2, Age0To14: 1, Age15To44: 2, Age65Plus: 1));
    }

    [Fact]
    public async Task The_coded_index_counts_what_mrd_coded_with_the_patients_age_at_the_episode()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        var (from, episodeDate) = (today.AddDays(-180), today.AddDays(-120));
        var before = await IndexAsync(mrd, from, today, "code", "coded");

        // A girl who turned 15 a month ago was 14 at her malaria episode four months ago, which MRD codes now.
        var girl = await PatientAsync("Meera", Gender.Female, today.AddYears(-15).AddDays(-30));
        var malaria = await mrd.PostAsync<CodingDto>("/api/v1/mrd/codings",
            new CreateCodingRequest(girl, await EncounterAsync(doctor, girl, (Malaria, DiagnosisType.Final)), null, null, episodeDate, null));
        (await mrd.PutAsync<CodingDto>($"/api/v1/mrd/codings/{malaria.Id}",
                new SaveCodingRequest([new CodingLineRequest(CodingEntryKind.Diagnosis, Malaria, IsPrimary: true)], null, malaria.Version)))
            .Status.ShouldBe(CodingStatus.Coded);

        // An episode MRD has not coded yet is not in the coded index, whatever its doctor diagnosed.
        var boy = await PatientAsync("Rohan", Gender.Male, today.AddYears(-8));
        await mrd.PostAsync<CodingDto>("/api/v1/mrd/codings",
            new CreateCodingRequest(boy, await EncounterAsync(doctor, boy, (Dengue, DiagnosisType.Final)), null, null, episodeDate, null));

        var byCode = Difference(before, await IndexAsync(mrd, from, today, "code", "coded"));
        byCode.Keys.ShouldBe([Malaria]);
        byCode[Malaria].ShouldBe(new Change(Episodes: 1, Patients: 1, Male: 0, Female: 1, Age0To14: 1, Age15To44: 0, Age65Plus: 0),
            "She is counted at her age on the day of the episode.");
    }

    private sealed record Change(int Episodes, int Patients, int Male, int Female, int Age0To14, int Age15To44, int Age65Plus);

    private static Task<MorbidityReport> IndexAsync(ApiClient mrd, DateOnly from, DateOnly to, string groupBy, string source) =>
        mrd.GetAsync<MorbidityReport>($"/api/v1/mrd/reports/morbidity?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&groupBy={groupBy}&source={source}");

    /// <summary>What <paramref name="after"/> counts more than <paramref name="before"/>, per row; rows without a change left out.</summary>
    private static Dictionary<string, Change> Difference(MorbidityReport before, MorbidityReport after)
    {
        Change Of(MorbidityReport report, string key) => report.Rows.SingleOrDefault(r => r.Key == key) is { } r
            ? new Change(r.Episodes, r.Patients, r.Male, r.Female, r.Age0To14, r.Age15To44, r.Age65Plus)
            : new Change(0, 0, 0, 0, 0, 0, 0);

        return after.Rows.Select(r => r.Key).Union(before.Rows.Select(r => r.Key))
            .Select(key => (Key: key, A: Of(after, key), B: Of(before, key)))
            .Select(x => (x.Key, Change: new Change(x.A.Episodes - x.B.Episodes, x.A.Patients - x.B.Patients, x.A.Male - x.B.Male,
                x.A.Female - x.B.Female, x.A.Age0To14 - x.B.Age0To14, x.A.Age15To44 - x.B.Age15To44, x.A.Age65Plus - x.B.Age65Plus)))
            .Where(x => x.Change != new Change(0, 0, 0, 0, 0, 0, 0))
            .ToDictionary(x => x.Key, x => x.Change);
    }

    /// <summary>An out-patient encounter of the doctor's with these diagnoses, the first one primary.</summary>
    private static async Task<Guid> EncounterAsync(ApiClient doctor, Guid patientId, params (string Code, DiagnosisType Type)[] diagnoses)
    {
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, VisitId: null, AdmissionId: null, EncounterType.OutPatient, DepartmentId: null, PractitionerId: null, TemplateId: null,
            ChiefComplaint: "Fever"));
        foreach (var (code, type) in diagnoses)
        {
            await doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{encounter.Id}/diagnoses", new AddDiagnosisRequest(
                code, Title: null, type, IsPrimary: code == diagnoses[0].Code, OnsetDate: null, DiagnosisStatus.Active, Notes: null));
        }

        return encounter.Id;
    }

    private async Task<Guid> PatientAsync(string firstName, Gender gender, DateOnly dateOfBirth)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        // A patient of this test alone: the unique names and mobile number keep the duplicate check quiet.
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: gender == Gender.Female ? "Ms." : "Mr.", FirstName: TestPatients.FirstName(firstName), MiddleName: null, LastName: "Index" + Letters(8), Gender: gender,
            DateOfBirth: dateOfBirth, AgeYears: null, AgeMonths: null, Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
            PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false,
            Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
