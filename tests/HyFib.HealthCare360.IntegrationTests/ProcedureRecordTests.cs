using System.Globalization;
using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Procedures;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Structured procedures on the longitudinal record (EHR-01) and the timeline's kind-of-care filter (EHR-02), over the
/// real API and PostgreSQL: procedures are recorded, corrected and voided on the chart, operations the theatre completes
/// arrive on their own, and the timeline narrows to OP, IP, ER, telehealth or check-up care.
/// </summary>
public sealed class ProcedureRecordTests(HospitalServer server)
{
    [Fact]
    public async Task A_doctor_records_corrects_and_voids_a_procedure_and_the_timeline_follows()
    {
        var patientId = await RegisterPatientAsync();
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var path = $"/api/v1/emr/patients/{patientId}/procedures";
        var performedAt = DateTimeOffset.UtcNow.AddHours(-2);

        var recorded = await doctor.PostAsync<ProcedureDto>(path, new RecordProcedureRequest(
            null, performedAt, "0dtj4zz", null, null, null, ProcedureSide.NotApplicable, null, "Uneventful", null));

        recorded.Code.ShouldBe("0DTJ4ZZ", "The catalogue's code is stored, whatever the case it was typed in.");
        recorded.CodeSystem.ShouldBe(CodeReleases.Icd10Pcs);
        recorded.Display.ShouldStartWith("Laparoscopic appendicectomy", Case.Sensitive, "Without a description the catalogue's title is used.");
        recorded.PerformerPractitionerId.ShouldNotBeNull("Without a named performer, the recording doctor did it.");
        recorded.PerformerName.ShouldNotBeNullOrWhiteSpace();
        recorded.Status.ShouldBe(ProcedureStatus.Completed);
        recorded.Source.ShouldBe(ProcedureSource.Recorded);

        var corrected = await doctor.PutAsync<ProcedureDto>($"/api/v1/emr/procedures/{recorded.Id}", new CorrectProcedureRequest(
            performedAt, null, "Incision and drainage of abscess", null, "Dr. Rao (visiting)", ProcedureSide.Right, "Axilla", "Pus drained", null,
            "Wrong procedure picked"));
        corrected.Code.ShouldBeNull();
        corrected.Display.ShouldBe("Incision and drainage of abscess");
        corrected.PerformerPractitionerId.ShouldBeNull();
        corrected.PerformerName.ShouldBe("Dr. Rao (visiting)");
        corrected.Status.ShouldBe(ProcedureStatus.Amended);
        corrected.StatusReason.ShouldBe("Wrong procedure picked");

        var timeline = await doctor.GetAsync<PagedResult<TimelineEntry>>($"/api/v1/emr/patients/{patientId}/timeline?types=Procedure");
        var entry = timeline.Items.ShouldHaveSingleItem();
        entry.Id.ShouldBe(recorded.Id);
        entry.Title.ShouldBe("Incision and drainage of abscess");
        entry.Summary.ShouldBe("Pus drained");

        await doctor.PostAsync<ProcedureDto>($"/api/v1/emr/procedures/{recorded.Id}/entered-in-error", new EnteredInErrorRequest("Wrong patient"));
        (await doctor.GetAsync<List<ProcedureDto>>(path)).ShouldBeEmpty();
        (await doctor.GetAsync<List<ProcedureDto>>(path + "?includeEnteredInError=true")).ShouldHaveSingleItem()
            .Status.ShouldBe(ProcedureStatus.EnteredInError);
        (await doctor.GetAsync<PagedResult<TimelineEntry>>($"/api/v1/emr/patients/{patientId}/timeline?types=Procedure")).Items
            .ShouldBeEmpty("A record entered in error leaves the timeline.");

        using (var again = await doctor.SendRawAsync(HttpMethod.Put, $"/api/v1/emr/procedures/{recorded.Id}", new CorrectProcedureRequest(
                   performedAt, null, "Something else", null, null, ProcedureSide.NotApplicable, null, null, null, "Second thoughts")))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "A record entered in error cannot be corrected back.");
        }

        (await AccessesAsync(patientId)).ShouldContain("Viewed procedures", "Each read of the procedure list is in the audit trail.");
    }

    [Fact]
    public async Task Codes_must_come_from_the_catalogue_and_only_doctors_record_procedures()
    {
        var patientId = await RegisterPatientAsync();
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var path = $"/api/v1/emr/patients/{patientId}/procedures";
        var request = new RecordProcedureRequest(null, DateTimeOffset.UtcNow, "ZZZ9999", null, null, null, ProcedureSide.NotApplicable, null, null, null);

        using (var unknown = await doctor.SendRawAsync(HttpMethod.Post, path, request))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("procedure catalogue");
        }

        using (var future = await doctor.SendRawAsync(HttpMethod.Post, path, request with { Code = null, Display = "Suturing", PerformedAt = DateTimeOffset.UtcNow.AddDays(1) }))
        {
            future.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var byNurse = await nurse.SendRawAsync(HttpMethod.Post, path, request with { Code = null, Display = "Suturing" }))
        {
            byNurse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await nurse.GetAsync<List<ProcedureDto>>(path)).ShouldBeEmpty("Nurses read the chart, procedures included.");
    }

    [Fact]
    public async Task The_timeline_narrows_to_a_kind_of_care_and_what_was_recorded_in_it()
    {
        var patientId = await RegisterPatientAsync();
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var outpatient = await AddEncounterAsync(patientId, EncounterType.OutPatient, DateTimeOffset.UtcNow.AddDays(-3));
        var inpatient = await AddEncounterAsync(patientId, EncounterType.InPatient, DateTimeOffset.UtcNow.AddDays(-2));
        var emergency = await AddEncounterAsync(patientId, EncounterType.Emergency, DateTimeOffset.UtcNow.AddDays(-1));
        var path = $"/api/v1/emr/patients/{patientId}/procedures";
        var opProcedure = await doctor.PostAsync<ProcedureDto>(path, new RecordProcedureRequest(
            outpatient, DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(20), null, "Ear wax removal", null, null, ProcedureSide.Left, null, null, null));
        var ipProcedure = await doctor.PostAsync<ProcedureDto>(path, new RecordProcedureRequest(
            inpatient, DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(20), null, "Central line insertion", null, null, ProcedureSide.Right, "Neck", null, null));
        var unlinked = await doctor.PostAsync<ProcedureDto>(path, new RecordProcedureRequest(
            null, DateTimeOffset.UtcNow.AddHours(-1), null, "Dressing", null, null, ProcedureSide.NotApplicable, null, null, null));
        ipProcedure.AdmissionId.ShouldBeNull("The test encounter belongs to no admission.");
        var timeline = $"/api/v1/emr/patients/{patientId}/timeline";

        var everything = await doctor.GetAsync<PagedResult<TimelineEntry>>(timeline);
        everything.Items.Select(e => e.Id).ShouldBe([unlinked.Id, emergency, ipProcedure.Id, inpatient, opProcedure.Id, outpatient], ignoreOrder: true);

        var ip = await doctor.GetAsync<PagedResult<TimelineEntry>>(timeline + "?encounterTypes=InPatient");
        ip.Items.Select(e => e.Id).ShouldBe([ipProcedure.Id, inpatient], ignoreOrder: true,
            "IP care is the IP encounter and what was recorded in it; nothing recorded outside an encounter.");
        ip.TotalCount.ShouldBe(2);

        var opAndEr = await doctor.GetAsync<PagedResult<TimelineEntry>>(timeline + "?encounterTypes=outpatient,Emergency&types=Encounter");
        opAndEr.Items.Select(e => e.Id).ShouldBe([emergency, outpatient], "Kinds of care combine with the entry types, newest first.");

        (await doctor.GetAsync<PagedResult<TimelineEntry>>(timeline + "?encounterTypes=Telehealth")).Items.ShouldBeEmpty();

        using var unknown = await doctor.SendRawAsync(HttpMethod.Get, timeline + "?encounterTypes=Ward");
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("encounterTypes");
    }

    [Fact]
    public async Task An_operation_completed_in_the_theatre_is_recorded_once_on_the_patients_record()
    {
        var patientId = await RegisterPatientAsync();
        var facilityId = await FacilityAsync();
        var admissionId = Guid.CreateVersion7();
        var encounterId = await AddEncounterAsync(patientId, EncounterType.InPatient, DateTimeOffset.UtcNow.AddHours(-6), admissionId);
        var surgeonId = await DoctorsPractitionerAsync();
        var startedAt = DateTimeOffset.UtcNow.AddHours(-2);

        // A surgery under way, its WHO Sign-out done: the theatre's steps before completion are covered by their own tests.
        var booking = new SurgeryBooking
        {
            FacilityId = facilityId,
            BookingNumber = "OT-T" + Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture),
            PatientId = patientId,
            AdmissionId = admissionId,
            ProcedureCode = "0FT44ZZ",
            ProcedureName = "Laparoscopic cholecystectomy",
            Laterality = Laterality.NotApplicable,
            SurgeonId = surgeonId,
            AnaesthesiaType = AnaesthesiaType.GA,
            Status = SurgeryStatus.InProgress,
            StartedAt = startedAt,
            SignOutCompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.Add(booking);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var saved = await db.Set<SurgeryBooking>().IgnoreQueryFilters().SingleAsync(b => b.Id == booking.Id, TestContext.Current.CancellationToken);
            saved.Complete(DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var procedure = (await doctor.GetAsync<List<ProcedureDto>>($"/api/v1/emr/patients/{patientId}/procedures")).ShouldHaveSingleItem();
        procedure.Source.ShouldBe(ProcedureSource.OperationTheatre);
        procedure.Code.ShouldBe("0FT44ZZ", "The booking's code is in the procedure catalogue, so it is kept.");
        procedure.Display.ShouldBe("Laparoscopic cholecystectomy");
        procedure.PerformerPractitionerId.ShouldBe(surgeonId);
        procedure.EncounterId.ShouldBe(encounterId, "The operation belongs to the admission's encounter.");
        procedure.AdmissionId.ShouldBe(admissionId);
        procedure.PerformedAt.ShouldBe(startedAt, TimeSpan.FromMilliseconds(1));

        // A redelivered completion records nothing twice.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var procedures = scope.ServiceProvider.GetRequiredService<ProcedureService>();
            await procedures.RecordSurgeryAsync(new SurgeryCompleted(booking.Id, booking.BookingNumber, facilityId, patientId, admissionId, null, "0FT44ZZ",
                "Laparoscopic cholecystectomy", ProcedureSide.NotApplicable, surgeonId, startedAt, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await doctor.GetAsync<List<ProcedureDto>>($"/api/v1/emr/patients/{patientId}/procedures")).Count.ShouldBe(1);
        var ip = await doctor.GetAsync<PagedResult<TimelineEntry>>($"/api/v1/emr/patients/{patientId}/timeline?encounterTypes=InPatient&types=Procedure");
        ip.Items.ShouldHaveSingleItem().Title.ShouldBe("0FT44ZZ Laparoscopic cholecystectomy");
    }

    private async Task<Guid> AddEncounterAsync(Guid patientId, EncounterType type, DateTimeOffset startedAt, Guid? admissionId = null)
    {
        var encounter = new Encounter
        {
            FacilityId = await FacilityAsync(),
            PatientId = patientId,
            AdmissionId = admissionId,
            PractitionerId = await DoctorsPractitionerAsync(),
            Type = type,
            StartedAt = startedAt,
        };
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(encounter);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return encounter.Id;
    }

    private async Task<Guid> FacilityAsync()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        return (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
    }

    private async Task<Guid> DoctorsPractitionerAsync()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        return (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
    }

    /// <summary>What the audit trail says the doctor looked at in this patient's record.</summary>
    private async Task<List<string?>> AccessesAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.UserName == HospitalServer.Users.Doctor && e.Category == AuditCategories.PhiAccess)
            .Select(e => e.Summary)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Kavya", MiddleName: null, LastName: "Procedure" + Letters(), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(15_000)), AgeYears: null, AgeMonths: null,
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
