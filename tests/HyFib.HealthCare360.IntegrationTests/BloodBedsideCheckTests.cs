using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A unit of blood is started on the ward only after two staff check it at the bedside: the patient's wristband
/// and the bag are read and compared with the issue, the unit must still be usable, the patient must have consented for
/// this stay, and the second member of staff proves who they are at the first one's screen. The transfusion is then
/// observed and ended, and what became of the unit is on the record: a unit started is not stock again, and the stay
/// does not end while one runs.
/// </summary>
public sealed class BloodBedsideCheckTests(HospitalServer server)
{
    private const string Bank = "/api/v1/blood-bank";
    private const string Witness = "/api/v1/auth/witness";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TransfusionObservationValues Vitals = new(36.8m, 84, 118, 76, 16, 98);

    [Fact]
    public async Task A_unit_is_started_only_after_two_staff_check_the_patient_and_the_bag_at_the_bedside()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var nurse = await RecordingNurseAsync();
        var officer = await BloodBankOfficerAsync();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id);
        var issue = await IssueUnitAsync(officer.Client, doctor, patient.Id, admission.Id, BloodGroup.BPositive, emergency: false);
        var start = $"{Bank}/issues/{issue.Id}/transfusion";

        // The ward sees the unit, by when it must be started, and that no consent is recorded for this stay yet; the read is on
        // the patient's audit trail.
        var unit = (await nurse.GetAsync<PatientBloodUnits>($"{Bank}/patients/{patient.Id}/units")).Units.Single(u => u.IssueId == issue.Id);
        (unit.UnitNumber, unit.ConsentCapturedAt, unit.Transfusion).ShouldBe((issue.UnitNumber, (DateTimeOffset?)null, (TransfusionDto?)null));
        unit.StartBy.ShouldBe(issue.IssuedAt.AddHours(4), TimeSpan.FromMilliseconds(1));
        (await PatientReadsAsync(patient.Id)).ShouldContain(e => e.Summary == "Viewed the patient's blood units on the ward");

        // Only someone who may start a transfusion witnesses one (not the front office, nor the blood bank, which issues but
        // does not transfuse), and never the nurse starting it.
        (await RefusalAsync(nurse, Witness, Confirm(HospitalServer.Users.FrontOffice, patient.Id))).ShouldBe("witness.not_allowed");
        (await RefusalAsync(nurse, Witness, Confirm(officer.UserName, patient.Id))).ShouldBe("witness.not_allowed");
        (await RefusalAsync(nurse, Witness, Confirm(HospitalServer.Users.Nurse, patient.Id))).ShouldBe("witness.self");
        var witness = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id));

        // Another patient's wristband, or another bag: nothing is started, and each near miss is on the audit trail.
        (await RefusalAsync(nurse, start, Start("UH-NOT-HIM", issue.UnitNumber, witness))).ShouldBe("bloodbank.wrong_patient");
        (await RefusalAsync(nurse, start, Start(patient.Uhid, issue.UnitNumber + "9", witness))).ShouldBe("bloodbank.wrong_unit");
        (await AuditAsync(nameof(BloodIssue), issue.Id)).Count(e => e.Action == "bloodbank.bedside_mismatch" && e.Severity == AuditSeverity.Warning)
            .ShouldBe(2);

        // A unit out of the blood bank for more than 4 hours cannot be given in time.
        await ChangeAsync<BloodIssue>(i => i.Id == issue.Id, i => i.IssuedAt = DateTimeOffset.UtcNow.AddHours(-5));
        (await RefusalAsync(nurse, start, Start(patient.Uhid, issue.UnitNumber, witness))).ShouldBe("bloodbank.out_of_storage_too_long");
        await ChangeAsync<BloodIssue>(i => i.Id == issue.Id, i => i.IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-10));

        // No consent for this stay: none at all, one withdrawn, or one given before the admission (an hour before: it would
        // have counted for blood asked for outside a stay).
        (await RefusalAsync(nurse, start, Start(patient.Uhid, issue.UnitNumber, witness))).ShouldBe("bloodbank.consent_required");
        var withdrawn = await ConsentAsync(frontOffice, patient.Id);
        await frontOffice.PostAsync($"/api/v1/patients/consents/{withdrawn.Id}/revoke", new RevokeConsentRequest("The patient changed his mind"));
        (await RefusalAsync(nurse, start, Start(patient.Uhid, issue.UnitNumber, witness))).ShouldBe("bloodbank.consent_required");
        var earlier = await ConsentAsync(frontOffice, patient.Id);
        await ChangeAsync<PatientConsent>(c => c.Id == earlier.Id, c => c.CapturedAt = DateTimeOffset.UtcNow.AddHours(-1));
        (await RefusalAsync(nurse, start, Start(patient.Uhid, issue.UnitNumber, witness))).ShouldBe("bloodbank.consent_required");
        var consent = await ConsentAsync(frontOffice, patient.Id);
        (await nurse.GetAsync<PatientBloodUnits>($"{Bank}/patients/{patient.Id}/units")).Units.Single(u => u.IssueId == issue.Id).ConsentCapturedAt
            .ShouldNotBeNull();

        // The blood bank, which may read the unit, may not start it.
        using (var byOfficer = await officer.Client.SendRawAsync(HttpMethod.Post, start, Start(patient.Uhid, issue.UnitNumber, witness)))
        {
            byOfficer.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // A witness confirms for one patient: a confirmation given at another patient's bedside does not start this unit.
        var otherPatient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var forAnother = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, otherPatient.Id));
        (await RefusalAsync(nurse, start, Start(patient.Uhid, issue.UnitNumber, forAnother))).ShouldBe("witness.confirmation_unusable");

        // The check passes (the wristband read in lower case is the same UHID): started, the witness named, the baseline kept.
        // The witness confirms again: a confirmation is good for five minutes.
        witness = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id));
        var started = await nurse.PostAsync<TransfusionDto>(start, Start(patient.Uhid.ToLowerInvariant(), issue.UnitNumber, witness));
        (started.Status, started.ConsentId).ShouldBe((TransfusionStatus.InProgress, (Guid?)consent.Id));
        started.WitnessName.ShouldBe(witness.Name);
        started.Observations.Single().Kind.ShouldBe(TransfusionObservationKind.Baseline);
        (await AuditAsync(nameof(Transfusion), started.Id)).ShouldContain(e => e.Action == "bloodbank.transfusion_started");

        // Not started twice; not returned to stock once started.
        (await RefusalAsync(nurse, start, Start(patient.Uhid, issue.UnitNumber, witness))).ShouldBe("bloodbank.transfusion_started");
        (await RefusalAsync(officer.Client, $"{Bank}/issues/{issue.Id}/return", new ReturnBloodRequest(TemperatureMaintained: true, Notes: null)))
            .ShouldBe("bloodbank.transfusion_started");

        // The stay does not end while it runs.
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/initiate",
            new InitiateDischargeRequest(null, "Haemoglobin corrected"));
        await ApproveDuesAsync(billing, admission.Id);
        (await RefusalAsync(nurse, $"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete", Discharge())).ShouldBe("discharge.transfusion_running");

        // Observed at 15 minutes, then completed with the volume given and the last observations.
        var observed = await nurse.PostAsync<TransfusionDto>($"{Bank}/transfusions/{started.Id}/observations",
            new RecordTransfusionObservationRequest(TransfusionObservationKind.FifteenMinutes, null, Vitals, started.Version));
        using (var staleObservation = await nurse.SendRawAsync(HttpMethod.Post, $"{Bank}/transfusions/{started.Id}/observations",
                   new RecordTransfusionObservationRequest(TransfusionObservationKind.Later, null, Vitals, started.Version)))
        {
            staleObservation.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        // Observations or an end saved over the newer record (from a screen opened before the observations) are refused.
        using (var stale = await nurse.SendRawAsync(HttpMethod.Post, $"{Bank}/transfusions/{started.Id}/end",
                   new EndTransfusionRequest(null, Stopped: false, StopReason: null, VolumeMl: 280, Vitals, started.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var ended = await nurse.PostAsync<TransfusionDto>($"{Bank}/transfusions/{started.Id}/end",
            new EndTransfusionRequest(null, Stopped: false, StopReason: null, VolumeMl: 280, Vitals, observed.Version));
        (ended.Status, ended.VolumeMl).ShouldBe((TransfusionStatus.Completed, 280));
        ended.Observations.Select(o => o.Kind)
            .ShouldBe([TransfusionObservationKind.Baseline, TransfusionObservationKind.FifteenMinutes, TransfusionObservationKind.End]);

        // Then the patient goes home.
        (await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete", Discharge())).Status
            .ShouldBe(AdmissionStatus.Discharged);
    }

    [Fact]
    public async Task A_unit_released_in_an_emergency_may_start_before_consent_only_with_the_reason_on_the_record()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await RecordingNurseAsync();
        var officer = await BloodBankOfficerAsync();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id);
        var issue = await IssueUnitAsync(officer.Client, doctor, patient.Id, admission.Id, BloodGroup.ONegative, emergency: true);
        var start = $"{Bank}/issues/{issue.Id}/transfusion";
        var witness = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id));

        // Not cross-matched, and no consent: refused without a reason, started with one.
        (await RefusalAsync(nurse, start, Start(patient.Uhid, issue.UnitNumber, witness))).ShouldBe("bloodbank.consent_required");
        var started = await nurse.PostAsync<TransfusionDto>(start,
            Start(patient.Uhid, issue.UnitNumber, witness) with { ConsentDeferredReason = "Unconscious after the accident, no relative present yet" });

        (started.ConsentId, started.ConsentDeferredReason).ShouldBe(((Guid?)null, "Unconscious after the accident, no relative present yet"));
        (await AuditAsync(nameof(Transfusion), started.Id)).ShouldContain(e => e.Action == "bloodbank.consent_deferred" && e.Severity == AuditSeverity.Warning);

        // Stopped for a reaction: the reason is kept and the stop is a warning on the trail.
        var stopped = await nurse.PostAsync<TransfusionDto>($"{Bank}/transfusions/{started.Id}/end",
            new EndTransfusionRequest(null, Stopped: true, StopReason: "Fever and rigors at 20 minutes", VolumeMl: 60, Vitals, started.Version));
        (stopped.Status, stopped.StopReason, stopped.VolumeMl).ShouldBe((TransfusionStatus.Stopped, "Fever and rigors at 20 minutes", 60));
        (await AuditAsync(nameof(Transfusion), started.Id)).ShouldContain(e => e.Action == "bloodbank.transfusion_stopped" && e.Severity == AuditSeverity.Warning);
    }

    [Fact]
    public async Task The_wards_list_of_a_patients_blood_is_closed_on_a_confidential_record()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        await ChangeAsync<Patient>(p => p.Id == patient.Id, p => p.SensitiveCategories = SensitiveCategory.Mtp);

        using var closed = await nurse.SendRawAsync(HttpMethod.Get, $"{Bank}/patients/{patient.Id}/units");
        closed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await closed.Content.ReadAsStringAsync(Ct)).ShouldContain("patient.restricted");
    }

    [Fact]
    public async Task A_unit_is_not_started_once_the_stay_has_ended()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var nurse = await RecordingNurseAsync();
        var officer = await BloodBankOfficerAsync();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id);
        var issue = await IssueUnitAsync(officer.Client, doctor, patient.Id, admission.Id, BloodGroup.APositive, emergency: false);
        await ConsentAsync(frontOffice, patient.Id);

        // A unit issued but never started does not hold the discharge: it goes back to the blood bank.
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/initiate",
            new InitiateDischargeRequest(null, "Haemoglobin corrected without the unit"));
        await ApproveDuesAsync(billing, admission.Id);
        (await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete", Discharge())).Status
            .ShouldBe(AdmissionStatus.Discharged);

        // A bedside dialog left open from before the discharge cannot start it now.
        var witness = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id));
        (await RefusalAsync(nurse, $"{Bank}/issues/{issue.Id}/transfusion", Start(patient.Uhid, issue.UnitNumber, witness))).ShouldBe("bloodbank.stay_ended");
        (await nurse.GetAsync<PatientBloodUnits>($"{Bank}/patients/{patient.Id}/units")).Units.Single(u => u.IssueId == issue.Id).Transfusion.ShouldBeNull();
    }

    [Fact]
    public async Task A_unit_taken_back_to_stock_while_its_start_waits_is_not_started_too()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await RecordingNurseAsync();
        var officer = await BloodBankOfficerAsync();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id);
        var issue = await IssueUnitAsync(officer.Client, doctor, patient.Id, admission.Id, BloodGroup.BNegative, emergency: false);
        await ConsentAsync(frontOffice, patient.Id);
        var witness = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id));

        // The test holds the stay's lock, as a discharge being saved would: the start reads the unit, then waits for it.
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var holding = await db.Database.BeginTransactionAsync(Ct);
        var key = StayLockKey(admission.Id);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", Ct);
        var starting = nurse.SendRawAsync(HttpMethod.Post, $"{Bank}/issues/{issue.Id}/transfusion", Start(patient.Uhid, issue.UnitNumber, witness));
        await WaitForLockWaiterAsync(key);

        // Meanwhile the blood bank takes the unit back to stock, which the start has already read as issued.
        using (var returned = await officer.Client.SendRawAsync(HttpMethod.Post, $"{Bank}/issues/{issue.Id}/return",
                   new ReturnBloodRequest(TemperatureMaintained: true, Notes: null)))
        {
            returned.EnsureSuccessStatusCode();
        }

        await holding.CommitAsync(Ct);
        using var started = await starting;
        started.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the unit went back to stock while the start waited, so only the return is kept");
        var unit = (await nurse.GetAsync<PatientBloodUnits>($"{Bank}/patients/{patient.Id}/units")).Units.Single(u => u.IssueId == issue.Id);
        (unit.ReturnedAt is not null, unit.Transfusion).ShouldBe((true, (TransfusionDto?)null));
    }

    /// <summary>The advisory lock key of a stay, as <c>EncounterLocks</c> takes it.</summary>
    private static long StayLockKey(Guid admissionId) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"encounter:{admissionId:N}")));

    /// <summary>Waits until a session waits for the stay's lock (PostgreSQL shows a bigint key as its high and low halves).</summary>
    private async Task WaitForLockWaiterAsync(long key)
    {
        var (high, low) = ((long)(uint)(key >> 32), (long)(uint)key);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await db.Database.SqlQuery<int>(
                   $"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND classid::bigint = {high} AND objid::bigint = {low} AND objsubid = 1")
                   .SingleAsync(Ct) == 0)
        {
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "the start should wait for the stay's lock");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>
    /// A unit of <paramref name="group"/> packed red cells collected, tested and verified by a second officer, asked for,
    /// cross-matched by a second officer (or released in an emergency by the doctor) and issued.
    /// </summary>
    [Fact]
    public async Task A_transfusion_reaction_is_also_an_incident_in_the_quality_register()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await RecordingNurseAsync();
        var officer = await BloodBankOfficerAsync();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id);
        var issue = await IssueUnitAsync(officer.Client, doctor, patient.Id, admission.Id, BloodGroup.APositive, emergency: false);

        var reaction = await nurse.PostAsync<Created>($"{Bank}/issues/{issue.Id}/reactions", new ReportReactionRequest(
            DateTimeOffset.UtcNow, [ReactionSymptom.Fever, ReactionSymptom.ChillsRigors], "T 38.9 °C, P 112", true, 60, "Stopped, saline running",
            ReactionSeverity.Moderate));

        await using var scope = server.Services.CreateAsyncScope();
        var incident = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Modules.Quality.Domain.Incident>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.SourceRecordId == reaction.Id, Ct);
        (incident.Type, incident.Severity, incident.PatientId, incident.SourceModule, incident.SourceReference)
            .ShouldBe((Modules.Quality.Domain.IncidentType.TransfusionReaction, Modules.Quality.Domain.IncidentSeverity.Moderate, (Guid?)patient.Id, "SpecialtyCare",
                $"Unit {issue.UnitNumber}"));
    }

    private async Task<BloodIssueDto> IssueUnitAsync(ApiClient officer, ApiClient doctor, Guid patientId, Guid admissionId, BloodGroup group, bool emergency)
    {
        var donor = await officer.PostAsync<Created>($"{Bank}/donors", new SaveDonorRequest(
            FirstName: "Ravi", LastName: "Donor" + Letters(8), Gender: Gender.Male, DateOfBirth: new DateOnly(1985, 1, 1).AddDays(Random.Shared.Next(3650)),
            BloodGroup: null, Phone: "9" + Digits(9), Email: null, Address: null, Occupation: null, ConsentToNotify: true, Notes: null));
        var screening = await officer.PostAsync<DonorScreeningDto>($"{Bank}/donors/{donor.Id}/screenings", new ScreenDonorRequest(
            HaemoglobinGdl: 14.2m, WeightKg: 68m, Systolic: 122, Diastolic: 78, Pulse: 74, TemperatureC: 36.8m,
            Answers: DonorEligibility.Questions.Select(q => new ScreeningAnswer(q.Key, Yes: false)).ToList()));
        var donation = await officer.PostAsync<Created>($"{Bank}/donors/{donor.Id}/donations", new CollectDonationRequest(
            ScreeningId: screening.Id, BagNumber: null, BagType: BloodBagType.Triple, VolumeMl: 450, Type: DonationType.Voluntary,
            ReplacementForPatientId: null, CollectedAt: null, DonorReaction: null, Notes: null));
        var units = await officer.PostAsync<List<BloodUnitDto>>($"{Bank}/donations/{donation.Id}/components",
            new SeparateComponentsRequest([new ComponentPlan(BloodComponentType.PRBC, 280, "Blood bank fridge 1")]));
        var loaded = await officer.GetAsync<DonationDetail>($"{Bank}/donations/{donation.Id}");
        await officer.PutAsync($"{Bank}/donations/{donation.Id}/lab-results", new RecordLabResultsRequest(
            group, TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.NonReactive, TtiResult.NonReactive, loaded.Version));
        await server.VerifyDonationAsync(donation.Id);
        var unitId = units.Single().Id;

        var request = await doctor.PostAsync<Created>($"{Bank}/requests", new CreateBloodRequestRequest(
            patientId, admissionId, VisitId: null, BloodComponentType.PRBC, Units: 1,
            emergency ? BloodRequestUrgency.Emergency : BloodRequestUrgency.Routine, "Symptomatic anaemia, Hb 6.8 g/dl",
            RequiredBy: null, HaemoglobinGdl: 6.8m, PlateletCount: null));
        if (emergency)
        {
            await server.AuthoriseEmergencyReleaseAsync(request.Id, "Massive bleeding after a road accident, no time to group and cross-match");
        }
        else
        {
            // The officer groups the patient and a second officer cross-matches, so the unit does not go out on one officer's checks.
            await officer.PostAsync($"{Bank}/requests/{request.Id}/grouping", new GroupPatientRequest(group));
            await server.CrossMatchAsSecondOfficerAsync(request.Id, unitId);
        }

        return await officer.PostAsync<BloodIssueDto>($"{Bank}/requests/{request.Id}/issues", new IssueBloodRequest(unitId, "Staff nurse Asha"));
    }

    /// <summary>
    /// The nurse who starts the unit, from the test's own address: each witness's password counts against the address it is
    /// typed at. The second nurse's account is made on its first sign-in, so it is signed in here first.
    /// </summary>
    private async Task<ApiClient> RecordingNurseAsync()
    {
        await server.SignInAsync(HospitalServer.Users.SecondNurse);
        return server.FromOwnAddress(await server.SignInAsync(HospitalServer.Users.Nurse));
    }

    private async Task<(ApiClient Client, string UserName)> BloodBankOfficerAsync()
    {
        var userName = "it-bloodbank-" + Letters(8);
        await server.CreateStaffAccountAsync(userName, SystemRoles.BloodBankOfficer);
        return (await server.SignInAsync(userName), userName);
    }

    private static Task<Created> ConsentAsync(ApiClient frontOffice, Guid patientId) =>
        frontOffice.PostAsync<Created>($"/api/v1/patients/{patientId}/consents",
            new CaptureConsentRequest(ConsentType.BloodTransfusion, null, true, "Ward sister", null, null, null, null));

    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, "SPW");
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Symptomatic anaemia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    private async Task ApproveDuesAsync(ApiClient billing, Guid admissionId)
    {
        var management = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admissionId}");
        await management.PostAsync($"/api/v1/billing/ip-accounts/{account.Id}/discharge-approval",
            new ApproveDischargeWithDuesRequest("Family to pay at the follow-up visit", account.Version));
    }

    private static CompleteDischargeRequest Discharge() => new(DischargeType.Normal, null, "Discharged home", NotifyPatient: false);

    private static StartTransfusionRequest Start(string wristband, string bag, WitnessConfirmed witness) =>
        new(wristband, bag, witness.ConfirmationId, Vitals, ConsentDeferredReason: null);

    private object Confirm(string userName, Guid patientId) =>
        new { userName, password = server.Password, code = (string?)null, purpose = "blood.bedside_check", patientId };

    private static async Task<string?> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        ((int)response.StatusCode).ShouldBeInRange(400, 499, text);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private async Task<List<AuditEvent>> AuditAsync(string entityType, Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var key = id.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.EntityType == entityType && e.EntityId == key).ToListAsync(Ct);
    }

    private async Task<List<AuditEvent>> PatientReadsAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Action == "patient.accessed").ToListAsync(Ct);
    }

    private async Task ChangeAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> which, Action<T> change) where T : class
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        change(await db.Set<T>().IgnoreQueryFilters().SingleAsync(which, Ct));
        await db.SaveChangesAsync(Ct);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private sealed record Created(Guid Id);
}
