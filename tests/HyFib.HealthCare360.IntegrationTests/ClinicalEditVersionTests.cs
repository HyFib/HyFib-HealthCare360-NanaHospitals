using System.Net;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Physio;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS REL-04 on the specialty-care clinical records: a donation's TTI results, a dialysis profile and session, a
/// physiotherapy plan and session, and a surgery's pre-op checklist, anaesthesia record and operation note. A save made to a
/// version someone has since saved over, or a "first" save over a record that already exists, is refused (409) and the
/// newer save stands.
/// </summary>
public sealed class ClinicalEditVersionTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_donations_test_results_saved_over_a_newer_save_are_refused()
    {
        var officer = await NewStaffAsync(SystemRoles.BloodBankOfficer);
        var donor = await officer.PostAsync<Created>("/api/v1/blood-bank/donors", new SaveDonorRequest(
            FirstName: "Karthik", LastName: "Donor" + Letters(8), Gender: Gender.Male, DateOfBirth: new DateOnly(1985, 1, 1).AddDays(Random.Shared.Next(3650)),
            BloodGroup: null, Phone: "9" + Digits(9), Email: null, Address: null, Occupation: null, ConsentToNotify: true, Notes: null));
        var screening = await officer.PostAsync<DonorScreeningDto>($"/api/v1/blood-bank/donors/{donor.Id}/screenings", new ScreenDonorRequest(
            HaemoglobinGdl: 14.2m, WeightKg: 68m, Systolic: 122, Diastolic: 78, Pulse: 74, TemperatureC: 36.8m,
            Answers: DonorEligibility.Questions.Select(q => new ScreeningAnswer(q.Key, Yes: false)).ToList()));
        var donation = await officer.PostAsync<Created>($"/api/v1/blood-bank/donors/{donor.Id}/donations", new CollectDonationRequest(
            ScreeningId: screening.Id, BagNumber: null, BagType: BloodBagType.Triple, VolumeMl: 450, Type: DonationType.Voluntary,
            ReplacementForPatientId: null, CollectedAt: null, DonorReaction: null, Notes: null));
        var path = $"/api/v1/blood-bank/donations/{donation.Id}";

        // Results are entered as the markers come back; those still running stay pending.
        RecordLabResultsRequest Panel(uint version, TtiResult hiv = TtiResult.Pending, TtiResult hbsAg = TtiResult.Pending,
            TtiResult hcv = TtiResult.Pending, TtiResult syphilis = TtiResult.Pending, TtiResult malaria = TtiResult.Pending) =>
            new(BloodGroup.BPositive, hiv, hbsAg, hcv, syphilis, malaria, version);

        var loaded = await officer.GetAsync<DonationDetail>(path);
        await officer.PutAsync($"{path}/lab-results", Panel(loaded.Version, hiv: TtiResult.NonReactive, hbsAg: TtiResult.NonReactive));

        // A second officer, whose screen still shows every marker pending, enters HCV: saved, it would put HIV and HBsAg back
        // to pending.
        using (var stale = await officer.SendRawAsync(HttpMethod.Put, $"{path}/lab-results", Panel(loaded.Version, hcv: TtiResult.NonReactive)))
        {
            await ShouldBeRefusedAsStaleAsync(stale);
        }

        var reloaded = await officer.GetAsync<DonationDetail>(path);
        (reloaded.Tti.Hiv, reloaded.Tti.HbsAg, reloaded.Tti.Hcv).ShouldBe((TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.Pending));

        // Reloaded, the panel is completed.
        await officer.PutAsync($"{path}/lab-results", Panel(
            reloaded.Version, TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.NonReactive));
        (await officer.GetAsync<DonationDetail>(path)).Tti.AllNonReactive.ShouldBeTrue();
    }

    [Fact]
    public async Task A_dialysis_profile_or_a_sessions_machine_saved_over_a_newer_save_is_refused()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patient = await RegisterPatientAsync("Suresh", Gender.Male);
        var profilePath = $"/api/v1/dialysis/patients/{patient.Id}";
        SaveDialysisProfileRequest Profile(decimal dryWeightKg, uint? version) => new(
            DryWeightKg: dryWeightKg, VascularAccess: VascularAccessType.AVF, AccessSite: "Left radiocephalic", AccessCreatedOn: null,
            ScheduleDays: [Weekday.Tuesday, Weekday.Friday], Shift: DialysisShift.Morning, SessionMinutes: 240, SeroStatus: SeroStatus.Negative,
            SeroTestedOn: DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-1), ReusePolicy: DialyzerReusePolicy.Reuse, MaxDialyzerUses: 8,
            NephrologistId: null, PrimaryDiagnosis: "CKD stage 5", AnticoagulationNotes: null, IsActive: true, Version: version);

        // Enrolment creates the profile, so there was nothing to load and no version to send. A second enrolment, by
        // someone who never saw the first, would overwrite it unseen.
        await doctor.PutAsync(profilePath, Profile(62m, null));
        using (var unseen = await doctor.SendRawAsync(HttpMethod.Put, profilePath, Profile(64m, null)))
        {
            await ShouldBeRefusedAsStaleAsync(unseen);
        }

        var enrolled = await doctor.GetAsync<DialysisProfileDto>(profilePath);
        enrolled.DryWeightKg.ShouldBe(62m);
        await doctor.PutAsync(profilePath, Profile(61.5m, enrolled.Version));
        using (var stale = await doctor.SendRawAsync(HttpMethod.Put, profilePath, Profile(63m, enrolled.Version)))
        {
            await ShouldBeRefusedAsStaleAsync(stale);
        }

        (await doctor.GetAsync<DialysisProfileDto>(profilePath)).DryWeightKg.ShouldBe(61.5m);

        // A session far ahead, where no other session holds the machines in its shift. The charge nurse puts it on one
        // machine; a second nurse, still looking at the board as it was, would move it to another.
        var machines = (await nurse.GetAsync<List<DialysisMachineDto>>("/api/v1/dialysis/machines"))
            .Where(m => m.SeroDedication == SeroStatus.Negative).ToList();
        machines.Count.ShouldBeGreaterThanOrEqualTo(2);
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(400 + Random.Shared.Next(300));
        var session = await nurse.PostAsync<Created>("/api/v1/dialysis/sessions",
            new ScheduleDialysisRequest(patient.Id, date, DialysisShift.Morning, null, null, null));
        var sessionPath = $"/api/v1/dialysis/sessions/{session.Id}";
        var loaded = await nurse.GetAsync<DialysisSessionDetail>(sessionPath);

        await nurse.PutAsync($"{sessionPath}/machine", new AssignDialysisMachineRequest(machines[0].Id, loaded.Version));
        using (var stale = await nurse.SendRawAsync(HttpMethod.Put, $"{sessionPath}/machine", new AssignDialysisMachineRequest(machines[1].Id, loaded.Version)))
        {
            await ShouldBeRefusedAsStaleAsync(stale);
        }

        (await nurse.GetAsync<DialysisSessionDetail>(sessionPath)).MachineId.ShouldBe(machines[0].Id);
    }

    [Fact]
    public async Task A_physiotherapy_plan_or_session_saved_over_a_newer_save_is_refused()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var physio = await NewStaffAsync(SystemRoles.Physiotherapist);
        var patient = await RegisterPatientAsync("Lakshmi", Gender.Female);
        var referral = await doctor.PostAsync<Created>("/api/v1/physio/referrals", new CreatePhysioReferralRequest(
            PatientId: patient.Id, VisitId: null, AdmissionId: null, ReferredByPractitionerId: null, Priority: OrderPriority.Routine,
            Reason: "Frozen right shoulder: restore movement", SessionsOrdered: 6));
        var path = $"/api/v1/physio/referrals/{referral.Id}";

        // No treatment without an assessment: the therapist assesses before planning.
        await physio.PostAsync($"{path}/assessments", new RecordPhysioAssessmentRequest(
            Type: PhysioAssessmentType.Initial, ChiefComplaint: "Pain and stiffness of the right shoulder for three months", PainScore: 6,
            PainLocation: "Right shoulder", RangeOfMotion: null, Strength: null, FunctionalStatus: "Cannot reach overhead", Goals: null, Notes: null));

        var startDate = DateOnly.FromDateTime(DateTime.UtcNow);
        SaveTreatmentPlanRequest Plan(int sessions, uint? version) => new(
            Modalities: [PhysioModality.IFT, PhysioModality.Exercises], ExerciseProgramme: "Pendulum and wall-climbing exercises",
            SessionsPlanned: sessions, SessionsPerWeek: 3, SessionMinutes: 30, StartDate: startDate, Precautions: null,
            Goals: "Reach overhead without pain", Version: version);

        // The first save makes the plan; a second "first" save, from a therapist who never saw it, is refused.
        await physio.PutAsync($"{path}/plan", Plan(6, null));
        using (var unseen = await physio.SendRawAsync(HttpMethod.Put, $"{path}/plan", Plan(10, null)))
        {
            await ShouldBeRefusedAsStaleAsync(unseen);
        }

        var planned = (await physio.GetAsync<PhysioReferralDetail>(path)).Plan.ShouldNotBeNull();
        planned.SessionsPlanned.ShouldBe(6);
        await physio.PutAsync($"{path}/plan", Plan(8, planned.Version));
        using (var stale = await physio.SendRawAsync(HttpMethod.Put, $"{path}/plan", Plan(10, planned.Version)))
        {
            await ShouldBeRefusedAsStaleAsync(stale);
        }

        var revised = (await physio.GetAsync<PhysioReferralDetail>(path)).Plan.ShouldNotBeNull();
        (revised.SessionsPlanned, revised.Revision).ShouldBe((8, 2));

        // Two sessions from two days ahead. The first is moved to the afternoon; moving it again from the list as it was is
        // refused.
        var firstAt = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(4), TimeSpan.Zero);
        var booked = await physio.PostAsync<List<Guid>>($"{path}/sessions", new SchedulePhysioSessionsRequest(firstAt, 2, null));
        var session = (await physio.GetAsync<PhysioReferralDetail>(path)).Sessions.Single(s => s.Id == booked[0]);
        var sessionPath = $"/api/v1/physio/sessions/{session.Id}";

        await physio.PutAsync(sessionPath, new ReschedulePhysioSessionRequest(firstAt.AddHours(5), session.Version));
        using (var stale = await physio.SendRawAsync(HttpMethod.Put, sessionPath, new ReschedulePhysioSessionRequest(firstAt.AddDays(1), session.Version)))
        {
            await ShouldBeRefusedAsStaleAsync(stale);
        }

        (await physio.GetAsync<PhysioReferralDetail>(path)).Sessions.Single(s => s.Id == session.Id).ScheduledAt.ShouldBe(firstAt.AddHours(5));
    }

    [Fact]
    public async Task A_surgerys_checklist_anaesthesia_record_or_operation_note_saved_over_a_newer_save_is_refused()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patient = await RegisterPatientAsync("Meena", Gender.Female);
        var surgeonId = (await doctor.GetAsync<AuthUser>("/api/v1/auth/me")).PractitionerId.ShouldNotBeNull();
        var anaesthetistId = (await doctor.GetAsync<PagedResult<PractitionerDto>>("/api/v1/masters/practitioners?kind=Consultant&active=true")).Items
            .First(p => p.Id != surgeonId).Id;
        var theatre = (await doctor.GetAsync<List<TheatreDto>>("/api/v1/ot/theatres")).First();

        // Booked straight into a slot far ahead, where nothing else holds the theatre, the surgeon or the anaesthetist.
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(400 + Random.Shared.Next(300)).AddHours(3), TimeSpan.Zero);
        var booking = await doctor.PostAsync<SurgeryCreated>("/api/v1/ot/bookings", new CreateSurgeryBookingRequest(
            PatientId: patient.Id, AdmissionId: null, VisitId: null, ProcedureServiceItemId: null, ProcedureCode: null, ProcedureName: "Excision of lipoma",
            Laterality: Laterality.Left, Diagnosis: "Lipoma, left forearm", SurgeonId: surgeonId, AssistantIds: null, AnaesthetistId: anaesthetistId,
            AnaesthesiaType: AnaesthesiaType.GA, Priority: SurgeryPriority.Elective, PreferredDate: null, TheatreId: theatre.Id,
            ScheduledStart: start, ScheduledEnd: start.AddHours(1), Notes: null));
        booking.Status.ShouldBe(SurgeryStatus.Scheduled);

        var path = $"/api/v1/ot/bookings/{booking.Id}";
        var loaded = await nurse.GetAsync<SurgeryDetail>(path);
        var checkedItems = loaded.PreOpChecklist.Select(i => new ChecklistInput(i.Key, ChecklistItemState.Done, null)).ToList();

        // The pre-op checklist is kept on the booking, so a save must say which version of the booking it was made to.
        using (var unversioned = await nurse.SendRawAsync(HttpMethod.Put, $"{path}/pre-op-checklist", new RecordChecklistRequest(checkedItems)))
        {
            unversioned.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await unversioned.Content.ReadAsStringAsync(Ct)).ShouldContain("\"version\"");
        }

        // The ward nurse completes it; a second nurse, from the booking as it was, would reopen the nil-by-mouth check.
        await nurse.PutAsync($"{path}/pre-op-checklist", new RecordChecklistRequest(checkedItems, loaded.Version));
        using (var stale = await nurse.SendRawAsync(HttpMethod.Put, $"{path}/pre-op-checklist",
                   new RecordChecklistRequest([new ChecklistInput("npo", ChecklistItemState.Pending, null)], loaded.Version)))
        {
            await ShouldBeRefusedAsStaleAsync(stale);
        }

        (await nurse.GetAsync<SurgeryDetail>(path)).PreOpComplete.ShouldBeTrue();

        // In theatre: the WHO Sign-in and Time-out, then knife to skin, after which the operation note can be written.
        foreach (var (phase, route) in new[] { (SafetyPhase.SignIn, "sign-in"), (SafetyPhase.TimeOut, "time-out") })
        {
            var confirmed = loaded.SafetyChecklist.Single(p => p.Phase == phase).Items
                .Select(i => new ChecklistInput(i.Key, ChecklistItemState.Done, null)).ToList();
            await nurse.PostAsync($"{path}/safety-checklist/{route}", new RecordChecklistRequest(confirmed));
        }

        await doctor.PostAsync($"{path}/start");

        // The anaesthesia record and the operation note are made by their first save, which has no version to send.
        var anaesthesiaPath = $"{path}/anaesthesia";
        SaveAnaesthesiaRecordRequest Anaesthesia(string airwayNotes, uint? version) => new(
            AsaGrade: AsaGrade.II, AsaEmergency: false, Technique: AnaesthesiaType.GA, Airway: AirwayDevice.EndotrachealTube, AirwayNotes: airwayNotes,
            AnaesthesiaStartAt: null, AnaesthesiaEndAt: null, Drugs: null, Fluids: null, EstimatedBloodLossMl: null, UrineOutputMl: null,
            Complications: null, RecoveryNotes: null, Sign: false, Version: version);

        await doctor.PutAsync(anaesthesiaPath, Anaesthesia("Mallampati II", null));
        using (var unseen = await doctor.SendRawAsync(HttpMethod.Put, anaesthesiaPath, Anaesthesia("Mallampati III", null)))
        {
            await ShouldBeRefusedAsStaleAsync(unseen);
        }

        var charted = await doctor.GetAsync<AnaesthesiaRecordDto>(anaesthesiaPath);
        charted.AirwayNotes.ShouldBe("Mallampati II");
        await doctor.PutAsync(anaesthesiaPath, Anaesthesia("Mallampati II; 7.5 mm cuffed tube at the first attempt", charted.Version));
        using (var stale = await doctor.SendRawAsync(HttpMethod.Put, anaesthesiaPath, Anaesthesia("Mallampati III", charted.Version)))
        {
            await ShouldBeRefusedAsStaleAsync(stale);
        }

        (await doctor.GetAsync<AnaesthesiaRecordDto>(anaesthesiaPath)).AirwayNotes.ShouldBe("Mallampati II; 7.5 mm cuffed tube at the first attempt");

        var notePath = $"{path}/operation-note";
        SaveOperationNoteRequest Note(string findings, uint? version) => new(
            PreOperativeDiagnosis: null, PostOperativeDiagnosis: "Lipoma, left forearm", ProcedurePerformed: "Excision of lipoma", Findings: findings,
            ProcedureDetails: null, Specimens: null, Drains: null, EstimatedBloodLossMl: 20, Complications: null, PostOpOrders: null, Sign: false,
            Version: version);

        await doctor.PutAsync(notePath, Note("Encapsulated lipoma, 4 x 3 cm", null));
        using (var unseen = await doctor.SendRawAsync(HttpMethod.Put, notePath, Note("Lipoma, 5 x 4 cm", null)))
        {
            await ShouldBeRefusedAsStaleAsync(unseen);
        }

        var written = await doctor.GetAsync<OperationNoteDto>(notePath);
        written.Findings.ShouldBe("Encapsulated lipoma, 4 x 3 cm");
        await doctor.PutAsync(notePath, Note("Encapsulated lipoma, 4 x 3 cm, excised whole", written.Version));
        using (var stale = await doctor.SendRawAsync(HttpMethod.Put, notePath, Note("Lipoma, 5 x 4 cm", written.Version)))
        {
            await ShouldBeRefusedAsStaleAsync(stale);
        }

        (await doctor.GetAsync<OperationNoteDto>(notePath)).Findings.ShouldBe("Encapsulated lipoma, 4 x 3 cm, excised whole");
    }

    /// <summary>
    /// REL-04's answer to a save over a newer version, or to a "first" save over a record that exists: 409 with the problem
    /// code <c>concurrency</c>. A conflict the record's own rules raise (a machine already booked that shift) does not pass
    /// for one.
    /// </summary>
    private static async Task ShouldBeRefusedAsStaleAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body);
        body.ShouldContain("\"concurrency\"");
    }

    /// <summary>A new member of staff in <paramref name="role"/>, which the demonstration seed has no account for, signed in.</summary>
    private async Task<ApiClient> NewStaffAsync(string role)
    {
        var userName = "it-rel04-" + Letters(8);
        await server.CreateStaffAccountAsync(userName, role);
        return await server.SignInAsync(userName);
    }

    private async Task<PatientCreated> RegisterPatientAsync(string firstName, Gender gender)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: TestPatients.FirstName(firstName), MiddleName: null, LastName: "Rel" + Letters(8), Gender: gender,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(10000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private sealed record Created(Guid Id);
}
