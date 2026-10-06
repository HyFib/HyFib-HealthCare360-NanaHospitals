using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The verified coding an insurance claim takes (MB-03), as PostgreSQL finds it: the admission's for an in-patient
/// bill; for any other bill, the out-patient coding of the visit or of an encounter in it. Never a coding that a second
/// coder has not verified, nor another patient's.
/// </summary>
public sealed class VerifiedCodingSourceTests(HospitalServer server)
{
    private static readonly CodingLine Cellulitis =
        new(CodingEntryKind.Diagnosis, "L03.1", "Cellulitis of other parts of limb", IsPrimary: true);

    private static readonly CodingLine Hypertension =
        new(CodingEntryKind.Diagnosis, "I10", "Essential (primary) hypertension", IsPrimary: false);

    private static readonly CodingLine Suturing =
        new(CodingEntryKind.Procedure, "0HQ1XZZ", "Suturing of facial laceration", IsPrimary: false);

    [Fact]
    public async Task Finds_the_admissions_coding_with_the_primary_diagnosis_first_and_the_procedures_apart()
    {
        var patientId = await RegisterPatientAsync();
        var admissionId = Guid.CreateVersion7();
        await AddCodingAsync(
            patientId, c => c.AdmissionId = admissionId, verified: true, Hypertension, Suturing, Cellulitis);

        var coding = (await FindAsync(patientId, admissionId, visitId: null)).ShouldNotBeNull();

        coding.Diagnoses.ShouldBe([
            new CodedEntry("L03.1", "Cellulitis of other parts of limb", IsPrimary: true),
            new CodedEntry("I10", "Essential (primary) hypertension", IsPrimary: false),
        ]);
        coding.Procedures.ShouldBe([new CodedEntry("0HQ1XZZ", "Suturing of facial laceration", IsPrimary: false)]);
    }

    [Fact]
    public async Task Finds_nothing_a_second_coder_has_not_verified_nor_another_patients_coding()
    {
        var patientId = await RegisterPatientAsync();
        var otherPatientId = await RegisterPatientAsync();
        var codedOnly = Guid.CreateVersion7();
        var othersAdmission = Guid.CreateVersion7();
        await AddCodingAsync(patientId, c => c.AdmissionId = codedOnly, verified: false, Cellulitis);
        await AddCodingAsync(otherPatientId, c => c.AdmissionId = othersAdmission, verified: true, Cellulitis);

        (await FindAsync(patientId, codedOnly, visitId: null)).ShouldBeNull();
        (await FindAsync(patientId, othersAdmission, visitId: null)).ShouldBeNull();
        (await FindAsync(patientId, admissionId: null, visitId: null)).ShouldBeNull();
    }

    [Fact]
    public async Task Finds_the_out_patient_coding_of_the_visit_or_of_an_encounter_in_it()
    {
        var patientId = await RegisterPatientAsync();

        // A coding that names the visit.
        var visitId = Guid.CreateVersion7();
        await AddCodingAsync(patientId, c =>
        {
            c.EncounterId = Guid.CreateVersion7();
            c.VisitId = visitId;
        }, verified: true, Cellulitis);
        (await FindAsync(patientId, admissionId: null, visitId)).ShouldNotBeNull()
            .Diagnoses.ShouldHaveSingleItem().Code.ShouldBe("L03.1");

        // A coding that names only the encounter, found through the encounter's visit.
        var secondVisitId = Guid.CreateVersion7();
        var encounterId = await AddEncounterAsync(patientId, secondVisitId);
        var primaryHypertension = Hypertension with { IsPrimary = true };
        await AddCodingAsync(patientId, c => c.EncounterId = encounterId, verified: true, primaryHypertension);
        (await FindAsync(patientId, admissionId: null, secondVisitId)).ShouldNotBeNull()
            .Diagnoses.ShouldHaveSingleItem().Code.ShouldBe("I10");

        // The coding of an admission that began in a visit is not that visit's out-patient coding.
        var admittedVisitId = Guid.CreateVersion7();
        await AddCodingAsync(patientId, c =>
        {
            c.AdmissionId = Guid.CreateVersion7();
            c.VisitId = admittedVisitId;
        }, verified: true, Cellulitis);
        (await FindAsync(patientId, admissionId: null, admittedVisitId)).ShouldBeNull();
    }

    private async Task<VerifiedEpisodeCoding?> FindAsync(Guid patientId, Guid? admissionId, Guid? visitId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IVerifiedCodingSource>()
            .FindAsync(patientId, admissionId, visitId, TestContext.Current.CancellationToken);
    }

    /// <summary>A coding by one coder, verified by another when <paramref name="verified"/> is set.</summary>
    private async Task AddCodingAsync(
        Guid patientId, Action<EncounterCoding> episode, bool verified, params CodingLine[] lines)
    {
        var now = DateTimeOffset.UtcNow;
        var coding = new EncounterCoding { FacilityId = await FacilityAsync(), PatientId = patientId };
        episode(coding);
        coding.Code(lines, coderUserId: Guid.CreateVersion7(), now).IsSuccess.ShouldBeTrue();
        if (verified)
        {
            coding.Verify(userId: Guid.CreateVersion7(), now).IsSuccess.ShouldBeTrue();
        }

        await SaveAsync(coding);
    }

    private async Task<Guid> AddEncounterAsync(Guid patientId, Guid visitId)
    {
        var encounter = new Encounter
        {
            FacilityId = await FacilityAsync(),
            PatientId = patientId,
            VisitId = visitId,
            PractitionerId = Guid.CreateVersion7(),
            Type = EncounterType.OutPatient,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await SaveAsync(encounter);
        return encounter.Id;
    }

    private async Task SaveAsync(object entity)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> FacilityAsync()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        return (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
    }

    private async Task<Guid> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Coded", MiddleName: null, LastName: "Claim" + Letters(), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(15_000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture),
            AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
            Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return patient.Id;
    }

    private static string Letters() =>
        string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
