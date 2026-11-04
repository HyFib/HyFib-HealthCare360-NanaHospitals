using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OtpNet;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A controlled drug given on the MAR is witnessed by a second nurse who proves who they are at the recording nurse's
/// screen: their own user name and password, and their authenticator code when they use one, checked as sign-in checks
/// them. The dose names the witness's account. The confirmation is good once, for that nurse and that patient, for a few
/// minutes, and no password ever reaches the MAR.
/// </summary>
public sealed class MarWitnessTests(HospitalServer server)
{
    private const string Witness = "/api/v1/auth/witness";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_controlled_dose_is_given_with_a_second_nurse_who_proves_who_they_are_at_the_screen()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await RecordingNurseAsync();
        var witness = await (await server.SignInAsync(HospitalServer.Users.SecondNurse)).GetAsync<AuthUser>("/api/v1/auth/me");
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id);
        var dose = await StatDoseAsync(nurse, admission.Id);

        // Not without a witness, and never the nurse herself.
        (await RefusalAsync(nurse, $"/api/v1/nursing/mar/{dose.Id}/record", Given(dose, null))).ShouldBe("mar.witness_required");
        (await RefusalAsync(nurse, Witness, Confirm(HospitalServer.Users.Nurse, patient.Id))).ShouldBe("witness.self");

        // A wrong password gets the answer an unknown account gets, and counts towards the witness's lockout as at sign-in.
        (await RefusalAsync(nurse, Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id, password: "not-her-password")))
            .ShouldBe("witness.invalid");
        (await RefusalAsync(nurse, Witness, Confirm("no-such-nurse", patient.Id))).ShouldBe("witness.invalid");
        (await FailedSignInsAsync(HospitalServer.Users.SecondNurse)).ShouldBe(1);

        // Someone who does not keep the MAR does not witness a dose on it.
        (await RefusalAsync(nurse, Witness, Confirm(HospitalServer.Users.FrontOffice, patient.Id))).ShouldBe("witness.not_allowed");

        // The second nurse confirms: her failures are forgotten, and the dose names her account, not a name typed.
        var confirmed = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id));
        (confirmed.UserId, confirmed.Name).ShouldBe((witness.Id, witness.DisplayName));
        (confirmed.ExpiresAt - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        (await FailedSignInsAsync(HospitalServer.Users.SecondNurse)).ShouldBe(0);

        // Only the nurse it was given to uses it.
        (await RefusalAsync(doctor, $"/api/v1/nursing/mar/{dose.Id}/record", Given(dose, confirmed.ConfirmationId)))
            .ShouldBe("witness.confirmation_unusable");
        var recorded = await nurse.PostAsync<MarEntryDto>($"/api/v1/nursing/mar/{dose.Id}/record", Given(dose, confirmed.ConfirmationId));
        (recorded.Status, recorded.WitnessName).ShouldBe((MarStatus.Given, witness.DisplayName));
        (await WitnessOfAsync(dose.Id)).ShouldBe(witness.Id);

        // Once: not again, for the next dose.
        var next = await StatDoseAsync(nurse, admission.Id);
        (await RefusalAsync(nurse, $"/api/v1/nursing/mar/{next.Id}/record", Given(next, confirmed.ConfirmationId)))
            .ShouldBe("witness.confirmation_unusable");

        // For this patient only.
        var other = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var forOther = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, other.Id));
        (await RefusalAsync(nurse, $"/api/v1/nursing/mar/{next.Id}/record", Given(next, forOther.ConfirmationId)))
            .ShouldBe("witness.confirmation_unusable");

        // Here, for a dose, and briefly: one given in another facility, for something else, or that has run out is not used.
        // A refusal does not spend it, so one confirmation is tried each way.
        var spare = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id));
        var record = $"/api/v1/nursing/mar/{next.Id}/record";
        Guid? here = null;
        await ChangeAsync<WitnessConfirmation>(c => c.Id == spare.ConfirmationId, c => (here, c.FacilityId) = (c.FacilityId, Guid.NewGuid()));
        (await RefusalAsync(nurse, record, Given(next, spare.ConfirmationId))).ShouldBe("witness.confirmation_unusable");
        await ChangeAsync<WitnessConfirmation>(c => c.Id == spare.ConfirmationId, c => (c.FacilityId, c.Purpose) = (here, "blood.bedside_check"));
        (await RefusalAsync(nurse, record, Given(next, spare.ConfirmationId))).ShouldBe("witness.confirmation_unusable");
        await ChangeAsync<WitnessConfirmation>(c => c.Id == spare.ConfirmationId, c => (c.Purpose, c.ExpiresAt) = ("mar.dose", DateTimeOffset.UtcNow.AddSeconds(-1)));
        (await RefusalAsync(nurse, record, Given(next, spare.ConfirmationId))).ShouldBe("witness.confirmation_unusable");

        // The audit trail: the witness confirming, for whom and for which patient; the wrong password against her account.
        var events = await AuditAsync(witness.Id);
        events.ShouldContain(e => e.Action == "auth.witness_confirmed" && e.PatientId == patient.Id);
        events.ShouldContain(e => e.Action == "auth.witness_failed" && e.Severity == AuditSeverity.Warning);
    }

    [Fact]
    public async Task An_extra_dose_of_a_controlled_drug_given_at_once_is_witnessed_too()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await RecordingNurseAsync();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id);
        var order = (await StatDoseAsync(nurse, admission.Id)).MedicationOrderId;
        var extra = $"/api/v1/nursing/admissions/{admission.Id}/mar";

        (await RefusalAsync(nurse, extra, Extra(order, null))).ShouldBe("mar.witness_required");
        var confirmed = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id));
        var given = await nurse.PostAsync<MarEntryDto>(extra, Extra(order, confirmed.ConfirmationId));
        (given.Status, given.WitnessName).ShouldBe((MarStatus.Given, confirmed.Name));
        (await WitnessOfAsync(given.Id)).ShouldBe(confirmed.UserId);
    }

    [Fact]
    public async Task A_medicine_the_drug_master_marks_high_alert_or_controlled_is_witnessed_whatever_the_order_said()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var nurse = await RecordingNurseAsync();
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id);
        var insulin = await DrugClassApprovalTests.CreateAsync(admin, "Insulin regular", DrugSchedule.H, controlled: false, highAlert: true);
        var morphine = await DrugClassApprovalTests.CreateAsync(admin, "Morphine", DrugSchedule.X, controlled: true, highAlert: false);
        var midazolam = await DrugClassApprovalTests.CreateAsync(admin, "Midazolam", DrugSchedule.H1, controlled: false, highAlert: false);

        // Ordered on the ward as neither controlled nor high-alert: the orders take what the master says. Schedule H1 is
        // kept in a register, so it is witnessed as a controlled drug is.
        var insulinDose = await WardDoseAsync(nurse, admission.Id, insulin, "Insulin regular 100 IU/ml", "8 IU", "SC");
        var morphineDose = await WardDoseAsync(nurse, admission.Id, morphine, "Morphine 10 mg/ml", "2 mg", "IV");
        await WardDoseAsync(nurse, admission.Id, midazolam, "Midazolam 1 mg/ml", "1 mg", "IV");
        var orders = await nurse.GetAsync<List<MedicationOrderDto>>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders");
        (orders.Single(o => o.DrugItemId == insulin).IsHighAlert, orders.Single(o => o.DrugItemId == insulin).IsControlledDrug).ShouldBe((true, false));
        orders.Single(o => o.DrugItemId == morphine).IsControlledDrug.ShouldBeTrue();
        orders.Single(o => o.DrugItemId == midazolam).IsControlledDrug.ShouldBeTrue();

        // The ward's medication round says so before anyone records the dose.
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "SPW");
        var round = await nurse.GetAsync<List<WardMarItem>>($"/api/v1/nursing/wards/{ward.Id}/mar/due?withinMinutes=60");
        round.Single(r => r.EntryId == insulinDose.Id).NeedsWitness.ShouldBeTrue();

        (await RefusalAsync(nurse, $"/api/v1/nursing/mar/{insulinDose.Id}/record", Given(insulinDose, null))).ShouldBe("mar.witness_required");
        (await RefusalAsync(nurse, $"/api/v1/nursing/mar/{morphineDose.Id}/record", Given(morphineDose, null))).ShouldBe("mar.witness_required");
        var confirmed = await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(HospitalServer.Users.SecondNurse, patient.Id));
        (await nurse.PostAsync<MarEntryDto>($"/api/v1/nursing/mar/{insulinDose.Id}/record", Given(insulinDose, confirmed.ConfirmationId)))
            .Status.ShouldBe(MarStatus.Given);

        // An extra dose of the high-alert order, given at once, as well.
        (await RefusalAsync(nurse, $"/api/v1/nursing/admissions/{admission.Id}/mar", Extra(insulinDose.MedicationOrderId, null)))
            .ShouldBe("mar.witness_required");

        // So does an order charted from a signed in-patient prescription that names the drug.
        var draft = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: admission.Id, IsInpatient: true, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    insulin, "Insulin regular 100 IU/ml", "Insulin regular", Strength: null, "Injection", Route: "SC", Dose: "6 IU", DoseFrequency.BD,
                    CustomFrequency: null, DosesPerDay: null, 2, Quantity: 4, Instructions: null, IsControlledDrug: false, ControlledSchedule: null),
            ]));
        await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version));
        (await nurse.GetAsync<List<MedicationOrderDto>>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders"))
            .Single(o => o.PrescriptionId == draft.Id).IsHighAlert.ShouldBeTrue();
    }

    [Fact]
    public async Task A_witness_whose_account_has_an_authenticator_gives_its_code_too_and_a_code_is_good_once()
    {
        const string name = "it-witness-code";
        await server.CreateStaffAccountAsync(name, SystemRoles.Nurse);
        var (own, _) = await server.SignInNewSessionAsync(name);
        var setup = await own.PostAsync<MfaSetupResponse>("/api/v1/auth/mfa/setup", new MfaSetupRequest(server.Password));
        await own.PostAsync<AuthResult>("/api/v1/auth/mfa/enable", new MfaEnableRequest(CodeAt(setup.Secret, DateTime.UtcNow)));
        var nurse = await RecordingNurseAsync();

        // The password is right: the code is asked for, and nothing is counted.
        (await RefusalAsync(nurse, Witness, Confirm(name, null))).ShouldBe("witness.code_required");
        (await FailedSignInsAsync(name)).ShouldBe(0);

        // A wrong code counts, as at sign-in; the next code confirms; the same code again does not.
        (await RefusalAsync(nurse, Witness, Confirm(name, null, code: CodeAt(setup.Secret, DateTime.UtcNow.AddMinutes(10))))).ShouldBe("witness.invalid");
        (await FailedSignInsAsync(name)).ShouldBe(1);
        var code = CodeAt(setup.Secret, DateTime.UtcNow.AddSeconds(30));
        (await nurse.PostAsync<WitnessConfirmed>(Witness, Confirm(name, null, code: code))).Name.ShouldNotBeNullOrWhiteSpace();
        (await RefusalAsync(nurse, Witness, Confirm(name, null, code: code))).ShouldBe("witness.invalid");
    }

    [Fact]
    public async Task A_locked_switched_off_or_unfinished_account_does_not_witness_even_with_its_password()
    {
        const string locked = "it-witness-locked";
        await server.CreateStaffAccountAsync(locked, SystemRoles.Nurse);
        var nurse = await RecordingNurseAsync();
        await ChangeAsync<User>(u => u.UserName == locked, u => u.LockoutEndsAt = DateTimeOffset.UtcNow.AddMinutes(10));
        (await RefusalAsync(nurse, Witness, Confirm(locked, null))).ShouldBe("witness.invalid");

        // An account switched off answers as an unknown one does.
        await ChangeAsync<User>(u => u.UserName == locked, u => (u.LockoutEndsAt, u.IsActive) = (null, false));
        (await RefusalAsync(nurse, Witness, Confirm(locked, null))).ShouldBe("witness.invalid");

        // An account that must still change its password witnesses nothing until it has.
        await ChangeAsync<User>(u => u.UserName == locked, u => (u.IsActive, u.MustChangePassword) = (true, true));
        (await RefusalAsync(nurse, Witness, Confirm(locked, null))).ShouldBe("witness.restricted");
    }

    [Fact]
    public async Task A_witness_who_does_not_work_in_this_facility_does_not_witness_here()
    {
        const string name = "it-witness-annexe";
        await server.CreateStaffAccountAsync(name, SystemRoles.Nurse);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var annexe = await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            Letters(6).ToUpperInvariant(), "Nana Hospitals Annexe " + Letters(4), null, new Address { City = "Chennai", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, IsActive: false, null));
        await WorksOnlyInAsync(name, annexe.Id);
        var nurse = await RecordingNurseAsync();

        (await RefusalAsync(nurse, Witness, Confirm(name, null))).ShouldBe("witness.not_allowed");
    }

    [Fact]
    public async Task Only_staff_ask_for_a_witness_and_only_for_what_a_module_lets_be_witnessed()
    {
        var nurse = await RecordingNurseAsync();
        using (var unknown = await nurse.SendRawAsync(HttpMethod.Post, Witness, Confirm(HospitalServer.Users.SecondNurse, null, purpose: "lab.result")))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await unknown.Content.ReadAsStringAsync(Ct));
        }

        // A patient signed in to the portal asks for no witness: not on the portal's address, nor on the staff app's.
        var patient = await server.SignInToPortalAsync("9" + Random.Shared.Next(100_000_000, 999_999_999));
        using (var onPortal = await patient.SendRawAsync(HttpMethod.Post, Witness, Confirm(HospitalServer.Users.SecondNurse, null)))
        {
            onPortal.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var onStaff = await server.FromOwnAddress(patient).SendRawAsync(HttpMethod.Post, Witness, Confirm(HospitalServer.Users.SecondNurse, null));
        onStaff.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await onStaff.Content.ReadAsStringAsync(Ct));
        (await onStaff.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("witness.staff_only");
    }

    [Fact]
    public async Task Each_password_typed_for_a_witness_counts_against_the_address_as_a_sign_in_does()
    {
        var limit = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.RateLimits.SignIn.PermitLimit;
        var nurse = await RecordingNurseAsync();

        var answers = new List<HttpStatusCode>();
        for (var attempt = 0; attempt <= limit; attempt++)
        {
            using var response = await nurse.SendRawAsync(HttpMethod.Post, Witness, Confirm("no-such-nurse", null));
            answers.Add(response.StatusCode);
        }

        // As many a minute from one address as sign-in allows (ClusterRateLimits.SignIn); then the screen waits.
        answers.Take(limit).ShouldAllBe(status => status == HttpStatusCode.UnprocessableEntity);
        answers[limit].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// The nurse who records the dose, from the test's own address: each witness's password counts against the address
    /// it is typed at, as a sign-in does, and the other tests sign in from the shared one. The second nurse's account is
    /// made on its first sign-in, so it is signed in here first, whichever test runs first.
    /// </summary>
    private async Task<ApiClient> RecordingNurseAsync()
    {
        await server.SignInAsync(HospitalServer.Users.SecondNurse);
        return server.FromOwnAddress(await server.SignInAsync(HospitalServer.Users.Nurse));
    }

    /// <summary>A witness's user name and password (the test accounts all have the same), as the witness types them.</summary>
    private object Confirm(string userName, Guid? patientId, string? password = null, string? code = null, string purpose = "mar.dose") =>
        new { userName, password = password ?? server.Password, code, purpose, patientId };

    private static RecordAdministrationRequest Given(MarEntryDto dose, Guid? witness) =>
        new(MarStatus.Given, AdministeredAt: null, Reason: null, BatchNumber: "B-NDPS-7", WitnessConfirmationId: witness, Notes: null, dose.Version);

    /// <summary>An extra dose of the order, given now.</summary>
    private static AddAdHocDoseRequest Extra(Guid? orderId, Guid? witness) => new(
        MedicationOrderId: orderId, DrugName: null, Dose: "2 mg", Route: "IV", ScheduledAt: null, GivenNow: true,
        Reason: null, BatchNumber: "B-NDPS-7", WitnessConfirmationId: witness, Notes: null);

    /// <summary>
    /// A drug of the master ordered on the ward to be given at once, as the form sends it: neither controlled nor
    /// high-alert. Its one dose is due now.
    /// </summary>
    private static async Task<MarEntryDto> WardDoseAsync(ApiClient nurse, Guid admissionId, Guid itemId, string name, string dose, string route)
    {
        var order = await nurse.PostAsync<MedicationOrderDto>($"/api/v1/nursing/admissions/{admissionId}/medication-orders", new AddMedicationOrderRequest(
            DrugItemId: itemId, DrugName: name, GenericName: null, Strength: null, Dose: dose, Route: route, Frequency: "STAT", DurationDays: 1,
            StartAt: null, Instructions: null, IsControlledDrug: false, OrderedByPractitionerId: null));
        var chart = await nurse.GetAsync<List<MarEntryDto>>($"/api/v1/nursing/admissions/{admissionId}/mar");
        return chart.Single(e => e.MedicationOrderId == order.Id && e.Status == MarStatus.Due);
    }

    /// <summary>A controlled drug ordered on the ward to be given at once: its one dose is due now.</summary>
    private static async Task<MarEntryDto> StatDoseAsync(ApiClient nurse, Guid admissionId)
    {
        var order = await nurse.PostAsync<MedicationOrderDto>($"/api/v1/nursing/admissions/{admissionId}/medication-orders", new AddMedicationOrderRequest(
            DrugItemId: null, DrugName: "Morphine 10 mg/ml Injection", GenericName: "Morphine", Strength: "10 mg/ml", Dose: "2 mg", Route: "IV",
            Frequency: "STAT", DurationDays: 1, StartAt: null, Instructions: null, IsControlledDrug: true, OrderedByPractitionerId: null));
        var chart = await nurse.GetAsync<List<MarEntryDto>>($"/api/v1/nursing/admissions/{admissionId}/mar");
        return chart.Single(e => e.MedicationOrderId == order.Id && e.Status == MarStatus.Due);
    }

    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, "SPW");
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Post-operative pain", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    private static async Task<string?> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private async Task<int> FailedSignInsAsync(string userName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>().IgnoreQueryFilters()
            .Where(u => u.UserName == userName).Select(u => u.FailedLoginCount).SingleAsync(Ct);
    }

    private async Task<Guid?> WitnessOfAsync(Guid doseId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<MedicationAdministration>().IgnoreQueryFilters()
            .Where(d => d.Id == doseId).Select(d => d.WitnessUserId).SingleAsync(Ct);
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid userId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.UserId == userId && e.Action.StartsWith("auth.witness_")).ToListAsync(Ct);
    }

    private async Task ChangeAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> which, Action<T> change) where T : class
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        change(await db.Set<T>().IgnoreQueryFilters().SingleAsync(which, Ct));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Moves the account <paramref name="userName"/> to work in <paramref name="facilityId"/> alone.</summary>
    private async Task WorksOnlyInAsync(string userName, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var userId = await db.Set<User>().IgnoreQueryFilters().Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync(Ct);
        await db.Set<UserFacility>().Where(f => f.UserId == userId).ExecuteDeleteAsync(Ct);
        db.Add(new UserFacility { UserId = userId, FacilityId = facilityId });
        await db.SaveChangesAsync(Ct);
    }

    private static string CodeAt(string secret, DateTime at) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);
}
