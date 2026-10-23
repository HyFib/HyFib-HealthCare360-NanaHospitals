using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The disease (morbidity) index from medical records' coding (MRD-06), over the real API and database: for each ICD code,
/// the discharges coded with it whose episode ended in the period, and the patients they were, by sex and by age at the end
/// of the period, the most frequent code first. A coding counts only once a second coder has verified it, and only the
/// facility's own; a secondary diagnosis counts as much as the primary one, a procedure never does. Each test codes
/// discharges in a month of the past that is its own, as no other test codes episodes that long ago.
/// </summary>
public sealed class MorbidityIndexTests(HospitalServer server)
{
    private const string Codings = "/api/v1/mrd/codings";

    /// <summary>A day of the past, new for every run, from which each test takes a month of its own.</summary>
    private static readonly DateOnly Epoch = new DateOnly(2000, 1, 1).AddDays(Random.Shared.Next(20 * 365));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Counts_each_codes_verified_discharges_and_their_patients_in_the_period_most_frequent_first()
    {
        var (from, to) = Period(0);
        var girl = await RegisterAsync(Gender.Female, years: 10, at: to);
        var man = await RegisterAsync(Gender.Male, years: 30, at: to);
        var woman = await RegisterAsync(Gender.Female, years: 50, at: to);
        var elder = await RegisterAsync(Gender.Male, years: 70, at: to);

        // The girl was in twice with dengue, discharged on the first and on the last day of the period; the second time
        // her diabetes was coded too.
        await CodedDischargeAsync(girl, from, verify: true, Primary("A90"));
        await CodedDischargeAsync(girl, to, verify: true, Primary("A90"), Secondary("E11.9"));

        // The man's appendix was taken out: the operation is coded, but the index is one of diseases.
        await CodedDischargeAsync(man, from.AddDays(3), verify: true, Primary("K35.8"), Procedure("0DTJ4ZZ"));
        await CodedDischargeAsync(woman, from.AddDays(7), verify: true, Primary("E11.9"), Secondary("I10"));
        await CodedDischargeAsync(elder, from.AddDays(9), verify: true, Primary("I10"));
        await CodedDischargeAsync(elder, from.AddDays(20), verify: true, Primary("J18.9"), Secondary("E11.9"));

        var report = await MorbidityAsync(from, to);

        report.Source.ShouldBe("coded");
        report.GroupBy.ShouldBe("code");
        report.TotalEpisodes.ShouldBe(6);

        // Most discharges first, then by code; ages are at the end of the period (0–14, 15–44, 45–64, 65 and over).
        report.Rows.ShouldBe([
            new MorbidityRow("E11.9", "Non-insulin-dependent diabetes mellitus without complications", "IV", Episodes: 3, Patients: 3,
                Male: 1, Female: 2, OtherOrUnknownGender: 0, Age0To14: 1, Age15To44: 0, Age45To64: 1, Age65Plus: 1, AgeUnknown: 0),
            new MorbidityRow("A90", "Dengue fever [classical dengue]", "I", Episodes: 2, Patients: 1,
                Male: 0, Female: 2, OtherOrUnknownGender: 0, Age0To14: 2, Age15To44: 0, Age45To64: 0, Age65Plus: 0, AgeUnknown: 0),
            new MorbidityRow("I10", "Essential (primary) hypertension", "IX", Episodes: 2, Patients: 2,
                Male: 1, Female: 1, OtherOrUnknownGender: 0, Age0To14: 0, Age15To44: 0, Age45To64: 1, Age65Plus: 1, AgeUnknown: 0),
            new MorbidityRow("J18.9", "Pneumonia, unspecified", "X", Episodes: 1, Patients: 1,
                Male: 1, Female: 0, OtherOrUnknownGender: 0, Age0To14: 0, Age15To44: 0, Age45To64: 0, Age65Plus: 1, AgeUnknown: 0),
            new MorbidityRow("K35.8", "Acute appendicitis, other and unspecified", "XI", Episodes: 1, Patients: 1,
                Male: 1, Female: 0, OtherOrUnknownGender: 0, Age0To14: 0, Age15To44: 1, Age45To64: 0, Age65Plus: 0, AgeUnknown: 0),
        ]);
    }

    [Fact]
    public async Task Counts_a_coding_only_once_a_second_coder_has_verified_it()
    {
        var (from, to) = Period(1);
        var patient = await RegisterAsync(Gender.Female, years: 40, at: to);
        await CodedDischargeAsync(patient, from.AddDays(1), verify: true, Primary("B54"));

        // Queued for coding at discharge, but not coded yet.
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        (await mrd.PostAsync<CodingDto>(Codings, Episode(patient, from.AddDays(2)))).Status.ShouldBe(CodingStatus.Pending);

        // Coded, but no second coder has verified it.
        await CodedDischargeAsync(patient, from.AddDays(3), verify: false, Primary("B54"));

        // Verified, then coded again: the new coding waits for a verification of its own.
        var recoded = await CodedDischargeAsync(patient, from.AddDays(4), verify: true, Primary("B54"));
        recoded = await mrd.PutAsync<CodingDto>($"{Codings}/{recoded.Id}",
            new SaveCodingRequest([Primary("B54"), Secondary("I10")], null, recoded.Version));
        recoded.Status.ShouldBe(CodingStatus.Coded);

        var report = await MorbidityAsync(from, to);

        report.TotalEpisodes.ShouldBe(1);
        var malaria = report.Rows.ShouldHaveSingleItem();
        malaria.Key.ShouldBe("B54");
        malaria.Episodes.ShouldBe(1);
        malaria.Patients.ShouldBe(1);
    }

    [Fact]
    public async Task Counts_only_the_facilitys_own_discharges_that_ended_in_the_period()
    {
        var (from, to) = Period(2);
        var patient = await RegisterAsync(Gender.Male, years: 60, at: to);
        await CodedDischargeAsync(patient, from.AddDays(10), verify: true, Primary("A90"));

        // Verified, but discharged the day before the period began, or the day after it ended.
        await CodedDischargeAsync(patient, from.AddDays(-1), verify: true, Primary("A90"));
        await CodedDischargeAsync(patient, to.AddDays(1), verify: true, Primary("A90"));

        // Verified and in the period, but the patient was discharged from another facility of the group.
        await OtherFacilitysVerifiedCodingAsync(patient, from.AddDays(11),
            new CodingLine(CodingEntryKind.Diagnosis, "A90", "Dengue fever [classical dengue]", IsPrimary: true));

        var report = await MorbidityAsync(from, to);

        report.TotalEpisodes.ShouldBe(1);
        var dengue = report.Rows.ShouldHaveSingleItem();
        dengue.Key.ShouldBe("A90");
        dengue.Episodes.ShouldBe(1);
        dengue.Male.ShouldBe(1);
        dengue.Age45To64.ShouldBe(1);
    }

    /// <summary>The <paramref name="test"/>th month of the run's own stretch of the past, with a fortnight between months.</summary>
    private static (DateOnly From, DateOnly To) Period(int test)
    {
        var from = Epoch.AddDays(test * 45);
        return (from, from.AddDays(29));
    }

    private static CodingLineRequest Primary(string code) => new(CodingEntryKind.Diagnosis, code, IsPrimary: true);

    private static CodingLineRequest Secondary(string code) => new(CodingEntryKind.Diagnosis, code, IsPrimary: false);

    private static CodingLineRequest Procedure(string code) => new(CodingEntryKind.Procedure, code, IsPrimary: false);

    /// <summary>The coding record of an admission that ended on <paramref name="discharged"/>, as the discharge opens it.</summary>
    private static CreateCodingRequest Episode(Guid patientId, DateOnly discharged) =>
        new(patientId, EncounterId: null, AdmissionId: Guid.CreateVersion7(), VisitId: null, discharged, Notes: null);

    /// <summary>
    /// Medical records codes an admission that ended on <paramref name="discharged"/>; with <paramref name="verify"/>, a
    /// second coder verifies the coding.
    /// </summary>
    private async Task<CodingDto> CodedDischargeAsync(Guid patientId, DateOnly discharged, bool verify, params CodingLineRequest[] lines)
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var coding = await mrd.PostAsync<CodingDto>(Codings, Episode(patientId, discharged));
        coding = await mrd.PutAsync<CodingDto>($"{Codings}/{coding.Id}", new SaveCodingRequest(lines, null, coding.Version));
        if (verify)
        {
            var verifier = await server.SignInAsync(HospitalServer.Users.MrdVerifier);
            coding = await verifier.PostAsync<CodingDto>($"{Codings}/{coding.Id}/verify", new VerifyCodingRequest(coding.Version));
        }

        coding.Status.ShouldBe(verify ? CodingStatus.Verified : CodingStatus.Coded);
        return coding;
    }

    /// <summary>A verified coding of the patient's discharge from another facility, which the hospital administrator adds.</summary>
    private async Task OtherFacilitysVerifiedCodingAsync(Guid patientId, DateOnly discharged, CodingLine line)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var annexe = await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            Letters(6).ToUpperInvariant(), "Nana Hospitals Annexe " + Letters(4), null, new Address { City = "Chennai", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, IsActive: false, null));

        var now = DateTimeOffset.UtcNow;
        var coding = new EncounterCoding { FacilityId = annexe.Id, PatientId = patientId, AdmissionId = Guid.CreateVersion7(), EpisodeDate = discharged };
        coding.Code([line], coderUserId: Guid.CreateVersion7(), now).IsSuccess.ShouldBeTrue();
        coding.Verify(userId: Guid.CreateVersion7(), now).IsSuccess.ShouldBeTrue();
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(coding);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<MorbidityReport> MorbidityAsync(DateOnly from, DateOnly to)
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        return await mrd.GetAsync<MorbidityReport>(string.Create(CultureInfo.InvariantCulture,
            $"/api/v1/mrd/reports/morbidity?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&groupBy=code&source=coded"));
    }

    /// <summary>A new patient who is <paramref name="years"/> old on <paramref name="at"/>.</summary>
    private async Task<Guid> RegisterAsync(Gender gender, int years, DateOnly at)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Morbidity", MiddleName: null, LastName: "Index" + Letters(8), Gender: gender,
            DateOfBirth: at.AddYears(-years).AddMonths(-6), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture),
            AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
            Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return patient.Id;
    }

    private static string Letters(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);
}
