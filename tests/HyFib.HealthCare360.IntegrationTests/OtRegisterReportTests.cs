using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Analytics;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The OT register (OT-08) over the real API and PostgreSQL: an emergency operation written by the theatre's own records
/// (the booking's times, the signed operation note and anaesthesia record, the team from the practitioner master) and the
/// ICU bed the patient went to after it, read as each member of staff may see it.
/// </summary>
public sealed class OtRegisterReportTests(HospitalServer server)
{
    private const string Bookings = "/api/v1/ot/bookings";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_operation_is_written_in_the_register_from_its_own_records_with_the_icu_it_went_to_and_as_each_viewer_may_see_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var management = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (patientId, uhid, phone) = await RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);
        var anaesthetist = await PractitionerAsync(management, "MD (Anaesthesia)");
        var assistant = await PractitionerAsync(management, "MS (General Surgery)");

        // The surgeon books an emergency hernia repair under general anaesthesia, with an anaesthetist and an assistant.
        var surgeonId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var hernia = (await doctor.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-HERNIA")).Single(s => s.Code == "PROC-HERNIA");
        var booking = await doctor.PostAsync<SurgeryCreated>(Bookings, new CreateSurgeryBookingRequest(
            patientId, AdmissionId: null, VisitId: null, hernia.Id, ProcedureCode: null, "Inguinal hernia repair with mesh", Laterality.Right,
            "Obstructed right inguinal hernia", surgeonId, [assistant.Id], anaesthetist.Id, AnaesthesiaType.GA, SurgeryPriority.Emergency,
            PreferredDate: null, TheatreId: null, ScheduledStart: null, ScheduledEnd: null, Notes: null));
        var (theatreId, wentIn) = await OnTheTableAsync(doctor, booking.Id);

        // The anaesthesia record and the operation note, each signed; then the patient leaves the theatre for the ICU.
        await doctor.PutAsync($"{Bookings}/{booking.Id}/anaesthesia", new SaveAnaesthesiaRecordRequest(
            AsaGrade.II, AsaEmergency: true, AnaesthesiaType.GA, AirwayDevice.EndotrachealTube, "Mallampati II", wentIn, null,
            [new AnaesthesiaDrug(wentIn.AddMinutes(4), "Fentanyl", "100 mcg", "IV"), new AnaesthesiaDrug(wentIn.AddMinutes(2), "Propofol", "120 mg", "IV")],
            Fluids: null, EstimatedBloodLossMl: 50, UrineOutputMl: null, Complications: null, RecoveryNotes: null, Sign: true, Version: null));
        await doctor.PutAsync($"{Bookings}/{booking.Id}/operation-note", new SaveOperationNoteRequest(
            "Obstructed right inguinal hernia", "Obstructed right inguinal hernia, bowel viable", "Emergency right inguinal hernia repair with mesh",
            "Loop of ileum in the sac, viable after release", "Lichtenstein repair", Specimens: null, Drains: null, EstimatedBloodLossMl: 50,
            Complications: "None", PostOpOrders: "Monitor in ICU overnight", Sign: true, Version: null));
        await doctor.PostAsync($"{Bookings}/{booking.Id}/complete");

        // A second case is wheeled in and signed in, then called off on the table: it stays on the register with its reason.
        var called = await doctor.PostAsync<SurgeryCreated>(Bookings, new CreateSurgeryBookingRequest(
            patientId, AdmissionId: null, VisitId: null, hernia.Id, ProcedureCode: null, "Inguinal hernia repair with mesh", Laterality.Left,
            "Left inguinal hernia", surgeonId, AssistantIds: null, AnaesthetistId: null, AnaesthesiaType.Local, SurgeryPriority.Elective,
            PreferredDate: null, TheatreId: null, ScheduledStart: null, ScheduledEnd: null, Notes: null));
        await SignedInAsync(called.Id, theatreId, wentIn.AddMinutes(1));
        await doctor.PostAsync($"{Bookings}/{called.Id}/cancel", new SurgeryReasonRequest("BP 210/110 on the table"));
        var icu = await TestBeds.AddAsync(server, "ICU");
        await nurse.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/transfer",
            new TransferPatientRequest(icu.BedId, "Post-operative monitoring", null, null, null));

        // Management, who may see contact details and open restricted records, reads the case as the paper register has it.
        var row = (await RegisterAsync(management, theatreId, wentIn)).Single(r => r["caseNumber"] == booking.BookingNumber);
        row["uhid"].ShouldBe(uhid);
        row["theatre"].ShouldBe("OT-3");
        row["timeFrom"].ShouldBe(Time(wentIn));
        row["ipNumber"].ShouldBe(admission.AdmissionNumber);
        row["transferredTo"].ShouldBe((await WardNameAsync("ICU")), "the ward the patient moved to from the theatre");
        row["mobile"].ShouldBe(phone);
        row["provisionalDiagnosis"].ShouldBe("Obstructed right inguinal hernia");
        row["surgery"].ShouldBe("Emergency right inguinal hernia repair with mesh, right");
        row["finalDiagnosis"].ShouldBe("Obstructed right inguinal hernia, bowel viable. Findings: Loop of ileum in the sac, viable after release");
        row["anaesthesia"].ShouldBe("General");
        row["drugs"].ShouldBe("Propofol 120 mg; Fentanyl 100 mcg");
        row["surgeon"].ShouldNotBeNull().ShouldContain("· operation note signed by ");
        row["anaesthetist"].ShouldNotBeNull().ShouldStartWith($"{anaesthetist.Name}, MD (Anaesthesia), Reg. {anaesthetist.Registration} (Tamil Nadu Medical Council) · anaesthesia record signed by ");
        row["assistants"].ShouldBe($"{assistant.Name}, MS (General Surgery), Reg. {assistant.Registration} (Tamil Nadu Medical Council)");
        row["remarks"].ShouldBe("Emergency. Complications: None");

        var cancelled = (await RegisterAsync(management, theatreId, wentIn)).Single(r => r["caseNumber"] == called.BookingNumber);
        cancelled["timeTo"].ShouldBe("Cancelled");
        cancelled["anaesthetist"].ShouldBe("Local by the surgeon");
        cancelled["remarks"].ShouldBe("Cancelled after Sign-in: BP 210/110 on the table");

        // Listing the patient is recorded in the audit trail for them, as for every patient-level report.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                    .CountAsync(e => e.Action == "patient.accessed" && e.PatientId == patientId && e.EntityId == "ot-register", Ct))
                .ShouldBeGreaterThan(0);
        }

        // A member of staff with the clinical reports, but neither contact details nor restricted records, sees the number
        // masked; once the record is put in a confidential category, its clinical columns are withheld from them.
        var analyst = await AnalystAsync();
        var masked = (await RegisterAsync(analyst, theatreId, wentIn)).Single(r => r["caseNumber"] == booking.BookingNumber);
        masked["mobile"].ShouldBe(Phone.Mask(phone));
        masked["surgery"].ShouldBe("Emergency right inguinal hernia repair with mesh, right");

        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().Where(p => p.Id == patientId)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.SensitiveCategories, SensitiveCategory.MentalHealth), Ct);
        }

        var closed = await RegisterAsync(analyst, theatreId, wentIn);
        closed.Single(r => r["caseNumber"] == called.BookingNumber)["remarks"].ShouldBe("Cancelled after Sign-in (reason withheld: confidential record)");
        var withheld = closed.Single(r => r["caseNumber"] == booking.BookingNumber);
        foreach (var key in new[] { "provisionalDiagnosis", "surgery", "finalDiagnosis", "drugs" })
        {
            withheld[key].ShouldBe("Withheld: confidential record", key);
        }

        withheld["anaesthesia"].ShouldBe("General");
        (await RegisterAsync(management, theatreId, wentIn)).Single(r => r["caseNumber"] == booking.BookingNumber)["surgery"]
            .ShouldBe("Emergency right inguinal hernia repair with mesh, right", "who may open restricted records reads it whole");
    }

    /// <summary>The register's rows for the day the patient went in, in the theatre, each as column key → text.</summary>
    private static async Task<List<Dictionary<string, string?>>> RegisterAsync(ApiClient reader, Guid theatreId, DateTimeOffset wentIn)
    {
        var day = DateOnly.FromDateTime(wentIn.ToOffset(TimeSpan.FromMinutes(330)).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var result = await reader.PostAsync<JsonElement>("/api/v1/analytics/reports/ot-register/run", new
        {
            Parameters = new Dictionary<string, string?> { ["from"] = day, ["to"] = day, ["theatreId"] = theatreId.ToString() },
            PageSize = 500,
        });
        var keys = result.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()!).ToList();
        return result.GetProperty("rows").EnumerateArray()
            .Select(row => keys.Zip(row.EnumerateArray(), (key, cell) => (key, cell.ValueKind == JsonValueKind.Null ? null : cell.ToString()))
                .ToDictionary(pair => pair.key, pair => pair.Item2))
            .ToList();
    }

    /// <summary>
    /// The surgery is under way in the minor theatre, its WHO Sign-in, Time-out and Sign-out done: the theatre's steps before
    /// completion are covered by their own tests. Returns the theatre and when the patient went in.
    /// </summary>
    private async Task<(Guid TheatreId, DateTimeOffset WentIn)> OnTheTableAsync(ApiClient doctor, Guid bookingId)
    {
        var theatreId = (await doctor.GetAsync<List<TheatreDto>>("/api/v1/ot/theatres")).Single(t => t.Code == "OT-3").Id;
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var booking = await db.Set<SurgeryBooking>().IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId, Ct);
        var now = DateTimeOffset.UtcNow;
        var wentIn = new DateTimeOffset(now.AddMinutes(-70).Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
        booking.TheatreId = theatreId;
        booking.Status = SurgeryStatus.InProgress;
        booking.SignInCompletedAt = booking.InRoomAt = wentIn;
        booking.TimeOutCompletedAt = wentIn.AddMinutes(5);
        booking.StartedAt = wentIn.AddMinutes(10);
        booking.SignOutCompletedAt = now.AddMinutes(-5);
        await db.SaveChangesAsync(Ct);
        return (theatreId, wentIn);
    }

    /// <summary>The patient is in the theatre and WHO Sign-in is done, the surgery not yet started.</summary>
    private async Task SignedInAsync(Guid bookingId, Guid theatreId, DateTimeOffset at)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var booking = await db.Set<SurgeryBooking>().IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId, Ct);
        booking.TheatreId = theatreId;
        booking.Status = SurgeryStatus.InPreOp;
        booking.SignInCompletedAt = booking.InRoomAt = at;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A member of staff who may run the clinical reports and nothing else.</summary>
    private async Task<ApiClient> AnalystAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var role = "Clinical audit " + suffix;
        await server.CreateRoleAsync(await server.SignInAsync(HospitalServer.Users.Admin),
            new SaveRoleRequest(role, "Reads the clinical reports (test)", [AnalyticsPermissions.ClinicalRead], null));
        var userName = "audit" + suffix;
        await server.CreateStaffAccountAsync(userName, role);
        return await server.SignInAsync(userName);
    }

    private async Task<string> WardNameAsync(string code)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Ward>().Where(w => w.Code == code).Select(w => w.Name).FirstAsync(Ct);
    }

    /// <summary>A doctor added to the practitioner master by management, with a qualification and a council registration.</summary>
    private static async Task<(Guid Id, string Name, string Registration)> PractitionerAsync(ApiClient management, string qualification)
    {
        var (lastName, registration) = ("Register" + Letters(6), Digits(6));
        var created = await management.PostAsync<JsonElement>("/api/v1/masters/practitioners", new SavePractitionerRequest(
            Code: $"OR{Letters(8)}", Title: "Dr.", FirstName: "Kavitha", LastName: lastName, Gender: Gender.Female, Qualification: qualification,
            RegistrationCouncil: "Tamil Nadu Medical Council", RegistrationNumber: registration, HprId: null, SpecialtyId: null, DepartmentId: null,
            Kind: PractitionerKind.Consultant, Phone: null, Email: null, ConsultationFeeNew: 0m, ConsultationFeeFollowUp: 0m, FollowUpValidityDays: 0,
            Languages: null, Bio: null, PhotoFileId: null, SignatureFileId: null, AvailableForOnlineBooking: false, AvailableForTelehealth: false,
            FacilityIds: null, IsActive: true, Version: null));
        return (created.GetProperty("id").GetGuid(), $"Dr. Kavitha {lastName}", registration);
    }

    /// <summary>Front office: admits the patient under the doctor to a bed of the male general ward added for it (<see cref="TestBeds"/>).</summary>
    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, "GWM");
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Obstructed right inguinal hernia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>A new patient; the random surname, date of birth and mobile number keep the duplicate check quiet.</summary>
    private static async Task<(Guid Id, string Uhid, string Phone)> RegisterPatientAsync(ApiClient frontOffice)
    {
        var surname = Letters(8);
        var mobile = "9" + Digits(9);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Murugan"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: mobile, AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return (created.Id, created.Uhid, "+91" + mobile);
    }

    private static string Time(DateTimeOffset instant)
    {
        var local = instant.ToOffset(TimeSpan.FromMinutes(330));
        var hour = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return $"{hour.ToString(CultureInfo.InvariantCulture)}:{local.Minute:00} {(local.Hour < 12 ? "am" : "pm")}";
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
