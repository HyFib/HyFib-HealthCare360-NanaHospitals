using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.VitalRegistration;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Births and deaths over the real API: recorded and certified by the staff allowed to, marking the patient deceased,
/// listed in the month's registers and reported to the Registrar, and an entry made on the wrong patient taken back.
/// </summary>
public sealed class BirthsAndDeathsTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_death_is_certified_by_the_doctor_marks_the_patient_deceased_and_is_reported_from_the_register()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await RegisterAsync(frontOffice, Gender.Male, new DateOnly(1950, 6, 1));
        var diedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var certificate = Certificate(patient.Id, diedAt);

        using (var notTheNurses = await nurse.SendRawAsync(HttpMethod.Post, "/api/v1/mrd/deaths", certificate))
        {
            notTheNurses.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var noPregnancyForAMan = await doctor.SendRawAsync(HttpMethod.Post, "/api/v1/mrd/deaths",
                   certificate with { Pregnancy = PregnancyAtDeath.NotPregnant }))
        {
            noPregnancyForAMan.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var death = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", certificate);
        (death.Status, death.UnderlyingCause, death.CertifierRegistrationNumber).ShouldBe((VitalReportStatus.Recorded, "Pneumonia", "TNMC 45120"));
        death.DueBy.ShouldBe(VitalRegistration.DueBy(VitalRegistration.LocalDate(diedAt)));
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).IsDeceased.ShouldBeTrue();

        using (var twice = await doctor.SendRawAsync(HttpMethod.Post, "/api/v1/mrd/deaths", certificate))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        // Medical records keeps the register and reports the death to the Registrar; then it cannot change here.
        var day = VitalRegistration.LocalDate(diedAt);
        var register = await mrd.GetAsync<DeathRegister>($"/api/v1/mrd/deaths?year={day.Year}&month={day.Month}");
        register.Rows.Single(r => r.Id == death.Id).Uhid.ShouldBe(patient.Uhid);
        using (var notTheDoctors = await doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/mrd/deaths?year={day.Year}&month={day.Month}"))
        {
            notTheDoctors.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        var reported = await mrd.PostAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}/report",
            new ReportToRegistrarRequest(today, "D-2026-TN-00417", death.Version));
        (reported.Status, reported.RegistrationNumber).ShouldBe((VitalReportStatus.Reported, "D-2026-TN-00417"));
        (await RefusalAsync(doctor, HttpMethod.Put, $"/api/v1/mrd/deaths/{death.Id}", certificate with { Version = reported.Version }))
            .ShouldBe("vital.reported");

        using (var export = await mrd.SendRawAsync(HttpMethod.Get, $"/api/v1/mrd/deaths/export?year={day.Year}&month={day.Month}"))
        {
            (await export.Content.ReadAsStringAsync(Ct)).ShouldContain("D-2026-TN-00417");
        }

        var printed = await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/mrd/deaths/{death.Id}/document");
        printed.Html.ShouldContain("FORM 4");
        printed.Html.ShouldContain("Septic shock");
    }

    [Fact]
    public async Task A_certificate_on_the_wrong_patient_is_taken_back_with_the_deceased_mark()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var woman = await RegisterAsync(frontOffice, Gender.Female, new DateOnly(1994, 2, 1));

        // A woman of 32: the certificate says whether she was pregnant.
        var certificate = Certificate(woman.Id, DateTimeOffset.UtcNow.AddHours(-2));
        using (var unanswered = await doctor.SendRawAsync(HttpMethod.Post, "/api/v1/mrd/deaths", certificate))
        {
            unanswered.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var death = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", certificate with { Pregnancy = PregnancyAtDeath.NotPregnant });
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{woman.Id}")).IsDeceased.ShouldBeTrue();

        var undone = await doctor.PostAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}/entered-in-error",
            new VitalEntryErrorRequest("Certified on the wrong patient", death.Version));
        undone.Status.ShouldBe(VitalReportStatus.EnteredInError);
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{woman.Id}")).IsDeceased.ShouldBeFalse();
    }

    [Fact]
    public async Task A_birth_is_recorded_by_the_nurse_and_a_still_birth_with_its_cause()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var mother = await RegisterAsync(frontOffice, Gender.Female, new DateOnly(1998, 8, 1));
        var bornAt = DateTimeOffset.UtcNow.AddHours(-4);

        var birth = await nurse.PostAsync<BirthRecordDto>("/api/v1/mrd/births", Birth(mother.Id, bornAt));
        (birth.Status, birth.MotherUhid, birth.Outcome).ShouldBe((VitalReportStatus.Recorded, mother.Uhid, BirthOutcome.LiveBirth));

        using (var noCause = await nurse.SendRawAsync(HttpMethod.Post, "/api/v1/mrd/births",
                   Birth(mother.Id, bornAt) with { Outcome = BirthOutcome.StillBirth, WeeksOfGestation = 34 }))
        {
            noCause.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var still = await nurse.PostAsync<BirthRecordDto>("/api/v1/mrd/births",
            Birth(mother.Id, bornAt) with { Outcome = BirthOutcome.StillBirth, WeeksOfGestation = 34, StillBirthCause = "Cord prolapse" });

        var day = VitalRegistration.LocalDate(bornAt);
        var register = await mrd.GetAsync<BirthRegister>($"/api/v1/mrd/births?year={day.Year}&month={day.Month}");
        register.Rows.ShouldContain(r => r.Id == birth.Id);
        register.Rows.ShouldContain(r => r.Id == still.Id && r.Outcome == BirthOutcome.StillBirth);
        register.StillBirths.ShouldBeGreaterThanOrEqualTo(1);

        var form3 = await mrd.GetAsync<PrintableDocumentDto>($"/api/v1/mrd/births/{still.Id}/document");
        form3.Html.ShouldContain("FORM 3");
        form3.Html.ShouldContain("Cord prolapse");
    }

    [Fact]
    public async Task A_restricted_patients_death_shows_in_the_register_with_initials_only()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var patient = await RegisterAsync(admin, Gender.Male, new DateOnly(1945, 3, 1), vip: true);
        var diedAt = DateTimeOffset.UtcNow.AddHours(-3);
        var death = await admin.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", Certificate(patient.Id, diedAt));

        var day = VitalRegistration.LocalDate(diedAt);
        var row = (await mrd.GetAsync<DeathRegister>($"/api/v1/mrd/deaths?year={day.Year}&month={day.Month}")).Rows.Single(r => r.Id == death.Id);
        (row.Uhid, row.UnderlyingCause, row.PatientName).ShouldBe(("Restricted", "Restricted", PatientNames.Restricted(death.PatientName)));
        (await admin.GetAsync<DeathRegister>($"/api/v1/mrd/deaths?year={day.Year}&month={day.Month}")).Rows.Single(r => r.Id == death.Id).Uhid
            .ShouldBe(patient.Uhid);
    }

    [Fact]
    public async Task A_stay_ending_with_the_patient_expired_marks_the_patient_deceased()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await RegisterAsync(frontOffice, Gender.Male, new DateOnly(1948, 1, 1));
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetServices<IDomainEventHandler<PatientDischarged>>().Single(h => h.GetType().Name == "ExpiredDischargeHandler");
            await handler.HandleAsync(new PatientDischarged(Guid.NewGuid(), patient.Id, Guid.NewGuid(), "Normal", at), Ct);
            await handler.HandleAsync(new PatientDischarged(Guid.NewGuid(), patient.Id, Guid.NewGuid(), "Expired", at), Ct);
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync(Ct);
        }

        var detail = await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}");
        (detail.IsDeceased, detail.DeceasedAt?.ToUnixTimeSeconds()).ShouldBe((true, (long?)at.ToUnixTimeSeconds()));
    }

    private static SaveDeathRequest Certificate(Guid patientId, DateTimeOffset diedAt) => new(
        patientId, AdmissionId: null, diedAt, AgeYears: null, [new CauseOfDeath("Septic shock", "6 hours"), new CauseOfDeath("Pneumonia", "5 days")],
        "Type 2 diabetes mellitus", DeathManner.Natural, MedicoLegal: false, PregnancyAtDeath.NotApplicable, "TNMC 45120");

    private static SaveBirthRequest Birth(Guid motherId, DateTimeOffset bornAt) => new(
        motherId, BabyPatientId: null, AdmissionId: null, bornAt, BirthOutcome.LiveBirth, BirthSex.Female, 3050, 39, DeliveryMethod.Natural,
        BirthAttendant.Doctor, ChildName: null, FatherName: "Suresh Kumar", MotherAgeYears: 28, LiveChildren: 1, "Labour room sister", StillBirthCause: null);

    private static async Task<string?> RefusalAsync(ApiClient api, HttpMethod method, string path, object? body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private static Task<PatientCreated> RegisterAsync(ApiClient api, Gender gender, DateOnly born, bool vip = false) =>
        api.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: gender == Gender.Male ? "Mr." : "Mrs.", FirstName: TestPatients.FirstName(gender == Gender.Male ? "Murugan" : "Lakshmi"), MiddleName: null,
            LastName: "Vital" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))), Gender: gender,
            DateOfBirth: born.AddDays(Random.Shared.Next(200)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture), AlternatePhone: null,
            Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: vip, Notes: null,
            ConfirmedNotDuplicate: true));
}
