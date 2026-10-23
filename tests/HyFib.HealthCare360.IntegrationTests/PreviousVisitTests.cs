using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The previous visit summary on the encounter workspace (EMR-02..07), over the real API and database: while the doctor
/// writes an encounter, its previous visit is the patient's latest signed encounter (signed, or amended since) that started
/// before it, at any facility of the hospital. Never the encounter itself, a later one, one still being written, or another
/// patient's; with no earlier signed encounter there is none.
/// </summary>
public sealed class PreviousVisitTests(HospitalServer server)
{
    private const string Encounters = "/api/v1/emr/encounters";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Is_the_latest_signed_encounter_before_this_one()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await RegisterAsync();
        var someoneElse = await RegisterAsync();

        // Dengue, diagnosed and signed; its follow-up, signed and later amended; then a visit whose note was never signed.
        var dengue = await StartAsync(patient, "Fever for two days with body ache");
        await doctor.PostAsync<DiagnosisDto>($"{Encounters}/{dengue.Id}/diagnoses",
            new AddDiagnosisRequest("A90", null, DiagnosisType.Final, IsPrimary: true, null, DiagnosisStatus.Active, null));
        dengue = await SignAsync(dengue);
        var followUp = await SignAsync(await StartAsync(patient, "Review after dengue"));
        followUp = await doctor.PostAsync<EncounterDetail>($"{Encounters}/{followUp.Id}/amend", new AmendEncounterRequest(
            "Platelet count from the lab report added", "Review after dengue", null, null, "Recovering; platelets 1.8 lakh", null, null,
            null, null, null, null, null, followUp.Version));
        followUp.Status.ShouldBe(EncounterStatus.Amended);
        var unsigned = await StartAsync(patient, "Cough for a week");

        // Then the patient was seen and the note signed at the hospital's annexe, and another patient was seen here.
        var annexe = await SignedAtAnotherFacilityAsync(patient, dengue.PractitionerId, "Seen at the annexe for a cough");
        await SignAsync(await StartAsync(someoneElse, "Headache"));

        // Today's consultation is being written; a later one is already signed.
        var today = await StartAsync(patient, "Cough not better");
        var evening = await SignAsync(await StartAsync(patient, "Seen again in the evening"));

        // Today's previous visit is the annexe's, the latest signed before it, not the evening's.
        (await PreviousVisitAsync(today)).ShouldNotBeNull().Id.ShouldBe(annexe);

        // The evening's is the annexe's too: today's note, the latest before it, is not signed yet.
        (await PreviousVisitAsync(evening)).ShouldNotBeNull().Id.ShouldBe(annexe);

        // Before the unsigned note, the amended follow-up was the latest signed visit.
        (await PreviousVisitAsync(unsigned)).ShouldNotBeNull().ShouldSatisfyAllConditions(
            v => v.Id.ShouldBe(followUp.Id),
            v => v.Status.ShouldBe(EncounterStatus.Amended));

        // The follow-up's previous visit is the dengue visit, as the chart shows it.
        var previous = (await PreviousVisitAsync(followUp)).ShouldNotBeNull();
        previous.ShouldSatisfyAllConditions(
            v => v.Id.ShouldBe(dengue.Id),
            v => v.Type.ShouldBe(EncounterType.OutPatient),
            v => v.Status.ShouldBe(EncounterStatus.Signed),
            v => v.StartedAt.ShouldBe(dengue.StartedAt),
            v => v.PractitionerName.ShouldBe(dengue.PractitionerName),
            v => v.DepartmentName.ShouldBe(dengue.DepartmentName),
            v => v.ChiefComplaint.ShouldBe("Fever for two days with body ache"),
            v => v.Diagnoses.ShouldBe(["A90 Dengue fever [classical dengue]"]));

        // Nothing was signed before the first visit.
        (await PreviousVisitAsync(dengue)).ShouldBeNull();
    }

    private async Task<RecentEncounter?> PreviousVisitAsync(EncounterDetail encounter)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        return (await doctor.GetAsync<PreviousVisitResponse>($"{Encounters}/{encounter.Id}/previous-visit")).Previous;
    }

    private async Task<EncounterDetail> StartAsync(Guid patientId, string complaint)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        return await doctor.PostAsync<EncounterDetail>(Encounters,
            new StartEncounterRequest(patientId, null, null, EncounterType.OutPatient, null, null, null, complaint));
    }

    private async Task<EncounterDetail> SignAsync(EncounterDetail encounter)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var signed = await doctor.PostAsync<EncounterDetail>($"{Encounters}/{encounter.Id}/sign", new SignEncounterRequest(encounter.Version));
        signed.Status.ShouldBe(EncounterStatus.Signed);
        return signed;
    }

    /// <summary>A signed encounter of the patient at another facility of the group, which the hospital administrator adds.</summary>
    private async Task<Guid> SignedAtAnotherFacilityAsync(Guid patientId, Guid practitionerId, string complaint)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var annexe = await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            Letters(6).ToUpperInvariant(), "Nana Hospitals Annexe " + Letters(4), null, new Address { City = "Chennai", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, IsActive: false, null));

        var encounter = new Encounter
        {
            FacilityId = annexe.Id,
            PatientId = patientId,
            PractitionerId = practitionerId,
            Type = EncounterType.OutPatient,
            StartedAt = DateTimeOffset.UtcNow,
        };
        encounter.Update(new EncounterContent(complaint, null, null, null, null, null, null, null, null, null, null)).IsSuccess.ShouldBeTrue();
        encounter.Sign(null, practitionerId, DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(encounter);
        await db.SaveChangesAsync(Ct);
        return encounter.Id;
    }

    private async Task<Guid> RegisterAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Previous", MiddleName: null, LastName: "Visit" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(10_000)), AgeYears: null, AgeMonths: null,
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
