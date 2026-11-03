using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A doctor still booked for OPD, with patients admitted under them or surgeries booked with them is taken out of use only
/// once the person has seen what is ahead: out of use, nobody can book or assign them again. The doctors are the test's
/// own, so no other test's bookings are counted.
/// </summary>
public sealed class PractitionerOutOfUseTests(HospitalServer server)
{
    private const string Practitioners = "/api/v1/masters/practitioners";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_doctor_with_work_ahead_goes_out_of_use_only_once_the_person_has_seen_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var surgeon = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (doctorId, doctorName) = await DoctorFeeStatementTests.DoctorAsync(admin);

        // Next week's OPD appointment, a video consultation booked without one, a patient admitted under them, and a
        // surgery booked with them as surgeon.
        var outpatient = await RegisterPatientAsync(frontOffice);
        var appointment = await AppointmentAsync(doctorId, outpatient, DateTimeOffset.UtcNow.AddDays(7));
        await VideoConsultationAsync(doctorId, outpatient, DateTimeOffset.UtcNow.AddDays(3));
        var admission = await AdmitAsync(frontOffice, doctorId, await RegisterPatientAsync(frontOffice));
        var surgery = await BookSurgeryAsync(surgeon, doctorId, await RegisterPatientAsync(frontOffice));

        // Taking them out of use is refused, naming each of these; the doctor stays in use.
        var problem = await RefusedAsync(admin, await SaveAsync(admin, doctorId, isActive: false, anyway: false));
        problem.GetProperty("code").GetString().ShouldBe(PractitionerWorkSources.WorkAheadCode);
        problem.GetProperty("detail").GetString()!.ShouldSatisfyAllConditions(
            d => d.ShouldStartWith(doctorName),
            d => d.ShouldContain("1 OPD appointment(s) booked from"),
            d => d.ShouldContain("1 video consultation(s) from"),
            d => d.ShouldContain("1 patient(s) admitted under them"),
            d => d.ShouldContain("1 surgery booking(s) with them as surgeon or anaesthetist"));
        var work = problem.GetProperty("data").GetProperty("workAhead").EnumerateArray()
            .ToDictionary(w => w.GetProperty("kind").GetString()!, w => w.GetProperty("numbers").EnumerateArray().Select(n => n.GetString()).ToList());
        work.Keys.ShouldBe(["appointments", "telehealth", "admissions", "surgeries"], ignoreOrder: true);
        work["appointments"].ShouldBe([appointment]);
        work["telehealth"].ShouldBeEmpty();
        work["admissions"].ShouldBe([admission.AdmissionNumber]);
        work["surgeries"].ShouldBe([surgery.BookingNumber]);
        (await admin.GetAsync<PractitionerDto>($"{Practitioners}/{doctorId}")).IsActive.ShouldBeTrue();
        (await OutOfUseAuditsAsync(doctorId)).ShouldBeEmpty();

        // Saying to go ahead anyway takes them out of use, and the audit trail keeps what was ahead and who went ahead.
        using (var anyway = await SaveAsync(admin, doctorId, isActive: false, anyway: true))
        {
            anyway.StatusCode.ShouldBe(HttpStatusCode.NoContent, await anyway.Content.ReadAsStringAsync(Ct));
        }

        (await admin.GetAsync<PractitionerDto>($"{Practitioners}/{doctorId}")).IsActive.ShouldBeFalse();
        var audit = (await OutOfUseAuditsAsync(doctorId)).ShouldHaveSingleItem();
        audit.ShouldSatisfyAllConditions(
            a => a.Category.ShouldBe(AuditCategories.Admin),
            a => a.UserName.ShouldNotBeNullOrEmpty(),
            a => a.Summary!.ShouldStartWith($"{doctorName} taken out of use with 1 OPD appointment(s)"),
            a => a.Details!.ShouldContain(admission.AdmissionNumber),
            a => a.Details!.ShouldContain(surgery.BookingNumber),
            a => a.Details!.ShouldContain(appointment));

        // Saved again while out of use, or put back in use, nothing is asked; and a doctor with nothing ahead goes out of
        // use without a question.
        using (var again = await SaveAsync(admin, doctorId, isActive: false, anyway: false))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent, await again.Content.ReadAsStringAsync(Ct));
        }

        (await OutOfUseAuditsAsync(doctorId)).Count.ShouldBe(1);
        using (var back = await SaveAsync(admin, doctorId, isActive: true, anyway: false))
        {
            back.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var (idleId, _) = await DoctorFeeStatementTests.DoctorAsync(admin);
        using (var idle = await SaveAsync(admin, idleId, isActive: false, anyway: false))
        {
            idle.StatusCode.ShouldBe(HttpStatusCode.NoContent, await idle.Content.ReadAsStringAsync(Ct));
        }

        (await OutOfUseAuditsAsync(idleId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Work_that_is_over_or_cancelled_is_not_ahead()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (doctorId, _) = await DoctorFeeStatementTests.DoctorAsync(admin);
        var patient = await RegisterPatientAsync(frontOffice);

        // Yesterday's appointment that was never attended, yesterday's check-in nobody closed, and one cancelled for next week.
        await AppointmentAsync(doctorId, patient, DateTimeOffset.UtcNow.AddDays(-1));
        await AppointmentAsync(doctorId, patient, DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(30), AppointmentStatus.CheckedIn);
        await AppointmentAsync(doctorId, patient, DateTimeOffset.UtcNow.AddDays(7), AppointmentStatus.Cancelled);

        using var saved = await SaveAsync(admin, doctorId, isActive: false, anyway: false);
        saved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await saved.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The doctor's record saved as read, in use or not, saying to go ahead anyway or not.</summary>
    private static async Task<HttpResponseMessage> SaveAsync(ApiClient admin, Guid doctorId, bool isActive, bool anyway)
    {
        var p = await admin.GetAsync<PractitionerDto>($"{Practitioners}/{doctorId}");
        return await admin.SendRawAsync(HttpMethod.Put, $"{Practitioners}/{doctorId}", new SavePractitionerRequest(
            p.Code, p.Title, p.FirstName, p.LastName, p.Gender, p.Qualification, p.RegistrationCouncil, p.RegistrationNumber, p.HprId,
            p.SpecialtyId, p.DepartmentId, p.Kind, p.Phone, p.Email, p.ConsultationFeeNew, p.ConsultationFeeFollowUp, p.FollowUpValidityDays,
            p.Languages, p.Bio, p.PhotoFileId, p.SignatureFileId, p.AvailableForOnlineBooking, p.AvailableForTelehealth, p.FacilityIds,
            isActive, p.Version, anyway));
    }

    private static async Task<JsonElement> RefusedAsync(ApiClient admin, HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
            return await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        }
    }

    /// <summary>
    /// An OPD appointment with the doctor at <paramref name="at"/>, as the desk books one; the doctor is the test's own and
    /// has no session, so it is put straight on the books. Its number.
    /// </summary>
    private async Task<string> AppointmentAsync(Guid doctorId, Guid patientId, DateTimeOffset at, AppointmentStatus status = AppointmentStatus.Booked)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var facilityId = await db.Set<Facility>().OrderBy(f => f.Code).Select(f => f.Id).FirstAsync(Ct);
        var departmentId = await db.Set<Department>().OrderBy(d => d.Code).Select(d => d.Id).FirstAsync(Ct);
        var appointment = new Appointment
        {
            FacilityId = facilityId,
            Number = "APT-T" + Guid.CreateVersion7().ToString("N")[..12].ToUpperInvariant(),
            PatientId = patientId,
            PractitionerId = doctorId,
            DepartmentId = departmentId,
            StartsAt = at,
            EndsAt = at.AddMinutes(15),
            Status = status,
            Channel = AppointmentChannel.Phone,
            VisitType = AppointmentVisitType.New,
            BookedAt = DateTimeOffset.UtcNow.AddDays(-2),
        };
        db.Add(appointment);
        await db.SaveChangesAsync(Ct);
        return appointment.Number;
    }

    /// <summary>A video consultation with the doctor at <paramref name="at"/> that no OPD appointment stands for.</summary>
    private async Task VideoConsultationAsync(Guid doctorId, Guid patientId, DateTimeOffset at)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(new TelehealthSession
        {
            FacilityId = await db.Set<Facility>().OrderBy(f => f.Code).Select(f => f.Id).FirstAsync(Ct),
            PatientId = patientId,
            PractitionerId = doctorId,
            Status = TelehealthStatus.Scheduled,
            ScheduledStartAt = at,
            ScheduledEndAt = at.AddMinutes(15),
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Front office: admits the patient under the doctor to a bed of the male general ward added for it (<see cref="TestBeds"/>).</summary>
    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, Guid doctorId, Guid patientId)
    {
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: doctorId, ConsultantPractitionerId: doctorId,
            ProvisionalDiagnosis: "Cholelithiasis", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>The OT desk books a hernia repair under local anaesthesia with the doctor as surgeon, not yet given a slot.</summary>
    private static async Task<SurgeryCreated> BookSurgeryAsync(ApiClient desk, Guid surgeonId, Guid patientId)
    {
        var hernia = (await desk.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-HERNIA")).Single(s => s.Code == "PROC-HERNIA");
        return await desk.PostAsync<SurgeryCreated>("/api/v1/ot/bookings", new CreateSurgeryBookingRequest(
            patientId, AdmissionId: null, VisitId: null, hernia.Id, ProcedureCode: null, "Inguinal hernia repair with mesh", Laterality.Left,
            "Left inguinal hernia", surgeonId, AssistantIds: null, AnaesthetistId: null, AnaesthesiaType.Local, SurgeryPriority.Elective,
            PreferredDate: null, TheatreId: null, ScheduledStart: null, ScheduledEnd: null, Notes: null));
    }

    private async Task<List<AuditEvent>> OutOfUseAuditsAsync(Guid doctorId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = doctorId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == "practitioner.out_of_use_with_work_ahead" && e.EntityId == id)
            .ToListAsync(Ct);
    }

    /// <summary>
    /// A new male patient (the general ward the admissions use is the men's); the random surname, date of birth and mobile
    /// number keep the duplicate check quiet.
    /// </summary>
    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice)
    {
        var surname = Letters(8);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Senthil"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
