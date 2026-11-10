using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Flags;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Maker-checker for prescribing (slice 5). A refill keeps the doctor's name but names who refilled it; a prescription
/// with a controlled drug is not refilled; a blocking alert found at refill is overridden only by the doctor who wrote the
/// prescription, and anyone else's refill goes back to that doctor. The prescribing-safety tables are tightened at once
/// and loosened only when the pharmacy and therapeutics committee approves, never the one who asked, and only over the row
/// as it was asked against.
/// </summary>
public sealed class PrescribingSafetyApprovalTests(HospitalServer server)
{
    private const string Prescriptions = "/api/v1/clinical/prescriptions";
    private const string Interactions = "/api/v1/clinical/drug-interactions";
    private const string DoseLimits = "/api/v1/clinical/dose-limits";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pharmacists_refill_carries_the_doctors_name_and_names_who_refilled_it()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var me = await MeAsync(doctor);
        var them = await MeAsync(pharmacist);
        var source = await SignedAsync(doctor, await PatientAsync(), refills: 1, Line("Paracetamol 500 mg", "Paracetamol", "Tablet", "Oral"));

        var refill = (await pharmacist.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{source.Id}/refill", Refill(null))).Prescription;

        ((Guid?)refill.PractitionerId, refill.RefillOfPrescriptionId, refill.SignedByUserId, refill.RefilledByName)
            .ShouldBe((me.PractitionerId, (Guid?)source.Id, (Guid?)them.Id, them.DisplayName));
        (await AuditAsync(nameof(Prescription), refill.Id)).ShouldContain(e => e.Action == "prescription.refilled"
            && e.Summary!.Contains(them.DisplayName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_prescription_with_a_controlled_drug_is_not_refilled()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var morphine = await DrugClassApprovalTests.CreateAsync(await server.SignInAsync(HospitalServer.Users.HospitalAdmin), "Morphine", DrugSchedule.X,
            controlled: true, highAlert: false);
        var source = await SignedAsync(doctor, await PatientAsync(), refills: 1,
            Line("Morphine 10 mg/ml Injection", "Morphine", "Injection", "IV") with { DrugItemId = morphine });

        (await RefusalAsync(pharmacist, $"{Prescriptions}/{source.Id}/refill", Refill(null) with { ConfirmControlledDrugs = true }))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "prescription.refill_controlled"));
        (await RefusalAsync(doctor, $"{Prescriptions}/{source.Id}/refill", Refill(null) with { ConfirmControlledDrugs = true }))
            .Code.ShouldBe("prescription.refill_controlled", "not by the doctor either: a controlled drug needs a new prescription");
        (await doctor.GetAsync<PrescriptionDto>($"{Prescriptions}/{source.Id}")).RefillsRemaining.ShouldBe(1, "nothing was used up");
    }

    [Fact]
    public async Task A_blocking_alert_at_refill_is_overridden_only_by_the_doctor_who_wrote_it_and_goes_back_to_them()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var secondDoctor = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var me = await MeAsync(doctor);
        var patient = await PatientAsync();
        var source = await SignedAsync(doctor, patient, refills: 2, Line("Cefotaxime 1 g", "Cefotaxime", "Injection", "IV"));

        // An allergy recorded since: the refill's safety check now blocks it.
        await nurse.PostAsync<JsonElement>($"/api/v1/emr/patients/{patient}/allergies",
            new RecordAllergyRequest("Cefotaxime", AllergyType.Drug, "Generalised urticaria", AllergySeverity.Severe, null, null));

        // Neither the pharmacist nor another doctor overrides it in the doctor's name: refused, and the doctor is told.
        const string reason = "Tolerated the last course without a reaction";
        (await RefusalAsync(pharmacist, $"{Prescriptions}/{source.Id}/refill", Refill(reason)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "prescription.refill_needs_prescriber"));
        (await RefusalAsync(secondDoctor, $"{Prescriptions}/{source.Id}/refill", Refill(reason))).Code.ShouldBe("prescription.refill_needs_prescriber");
        (await doctor.GetAsync<PrescriptionDto>($"{Prescriptions}/{source.Id}")).RefillsRemaining.ShouldBe(2, "a refused refill uses nothing up");
        (await NotificationsAsync(me.Id, "Refill held for your decision")).ShouldContain(n => n.Body!.Contains(source.PrescriptionNumber, StringComparison.Ordinal));
        (await AuditAsync(nameof(Prescription), source.Id)).ShouldContain(e => e.Action == "prescription.refill_blocked" && e.Severity == AuditSeverity.Warning);

        // The doctor who wrote it decides: without a reason it is blocked as at signing; with one it is refilled.
        (await RefusalAsync(doctor, $"{Prescriptions}/{source.Id}/refill", Refill(null))).Code.ShouldBe("prescription.safety_block");
        var refill = (await doctor.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{source.Id}/refill", Refill(reason))).Prescription;
        (refill.SafetyOverrideReason, refill.RefilledByName, (Guid?)refill.PractitionerId).ShouldBe((reason, me.DisplayName, me.PractitionerId));
    }

    [Fact]
    public async Task Tightening_a_drug_safety_table_is_saved_at_once()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (a, b) = (Generic(), Generic());

        var added = await CreatedAsync<DrugInteractionDto>(doctor, Interactions, Interaction(a, b, InteractionSeverity.Moderate, active: true));
        var major = await doctor.PutAsync<DrugInteractionDto>($"{Interactions}/{added.Id}", Interaction(a, b, InteractionSeverity.Major, active: true, added.Version));
        major.Severity.ShouldBe(InteractionSeverity.Major);
        var off = await CreatedAsync<DrugInteractionDto>(doctor, Interactions, Interaction(Generic(), Generic(), InteractionSeverity.Minor, active: false));
        (await doctor.PutAsync<DrugInteractionDto>($"{Interactions}/{off.Id}", Interaction(off.DrugA, off.DrugB, InteractionSeverity.Minor, active: true, off.Version)))
            .IsActive.ShouldBeTrue();

        var limit = await CreatedAsync<DoseLimitDto>(doctor, DoseLimits, Limit(Generic(), 4000m, active: true));
        (await doctor.PutAsync<DoseLimitDto>($"{DoseLimits}/{limit.Id}", Limit(limit.GenericName, 3000m, active: true, limit.Version) with { Route = null }))
            .MaxDailyDoseMg.ShouldBe(3000m, "lowered, and checked on every route: stricter both ways");
        (await AuditAsync(nameof(DrugInteraction), added.Id)).ShouldContain(e => e.Action == "clinical.drug_interaction_saved");
    }

    [Fact]
    public async Task Loosening_an_interaction_waits_for_the_committee_and_says_what_it_switches_off()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var (a, b) = (Generic(), Generic());
        var major = await CreatedAsync<DrugInteractionDto>(doctor, Interactions, Interaction(a, b, InteractionSeverity.Major, active: true));
        var moderate = Interaction(major.DrugA, major.DrugB, InteractionSeverity.Moderate, active: true, major.Version);

        // A reason is asked for, and the change waits.
        using (var unexplained = await doctor.SendRawAsync(HttpMethod.Put, $"{Interactions}/{major.Id}", moderate))
        {
            unexplained.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await unexplained.Content.ReadAsStringAsync(Ct)).ShouldContain("reason");
        }

        var request = await AcceptedAsync(doctor, $"{Interactions}/{major.Id}", moderate with { Reason = "The committee graded it moderate in its last review" });
        (request.Kind, request.Status, request.SubjectId).ShouldBe(("clinical.drug_interaction", ApprovalStatus.Pending, major.Id));
        request.Details.ShouldContain(d => d.Label == "Now" && d.Value == $"{major.DrugA} + {major.DrugB}: Major, blocks signing");
        request.Details.ShouldContain(d => d.Label == "Asked for" && d.Value == $"{major.DrugA} + {major.DrugB}: Moderate, a warning");
        request.Details.ShouldContain(d => d.Label == "What it changes" && d.Value.Contains("no longer blocks signing", StringComparison.Ordinal));
        var waiting = await InteractionAsync(doctor, major.Id);
        (waiting.Severity, waiting.ApprovalWaitingSince.HasValue).ShouldBe((InteractionSeverity.Major, true));

        // One waits at a time; a doctor does not decide it; the committee does.
        (await RefusalAsync(doctor, $"{Interactions}/{major.Id}", moderate with { IsActive = false, Reason = "Off" }, HttpMethod.Put))
            .ShouldBe((HttpStatusCode.Conflict, "approval.already_requested"));
        (await RefusalAsync(doctor, $"/api/v1/approvals/{request.Id}/approve", new ApproveRequest("Agreed.", request.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        (await pharmacist.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{request.Id}/approve", new ApproveRequest("Agreed by the committee.", request.Version)))
            .Status.ShouldBe(ApprovalStatus.Approved);

        var changed = await InteractionAsync(doctor, major.Id);
        (changed.Severity, changed.ApprovalWaitingSince).ShouldBe((InteractionSeverity.Moderate, (DateTimeOffset?)null));
        (await AuditAsync(nameof(DrugInteraction), major.Id)).ShouldContain(e => e.Action == "clinical.drug_interaction_changed" && e.Severity == AuditSeverity.Warning);
    }

    [Fact]
    public async Task Raising_a_maximum_dose_is_never_approved_by_the_one_who_asked()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var limit = await CreatedAsync<DoseLimitDto>(admin, DoseLimits, Limit(Generic(), 3000m, active: true));

        var request = await AcceptedAsync(admin, $"{DoseLimits}/{limit.Id}", Limit(limit.GenericName, 5000m, active: true, limit.Version) with { Reason = "Renal unit protocol" });
        request.Kind.ShouldBe("clinical.dose_limit");
        request.Details.ShouldContain(d => d.Label == "What it changes" && d.Value.Contains("up to 5,000 mg", StringComparison.Ordinal));
        (await DoseLimitAsync(admin, limit.Id)).MaxDailyDoseMg.ShouldBe(3000m, "the limit stays until approved");

        // The hospital administrator holds both sides, and still does not approve their own request.
        (await RefusalAsync(admin, $"/api/v1/approvals/{request.Id}/approve", new ApproveRequest("Agreed.", request.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        await pharmacist.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{request.Id}/approve", new ApproveRequest("Agreed.", request.Version));
        (await DoseLimitAsync(admin, limit.Id)).MaxDailyDoseMg.ShouldBe(5000m);
    }

    [Fact]
    public async Task A_loosening_is_not_made_over_a_row_that_changed_after_it_was_asked_for()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var major = await CreatedAsync<DrugInteractionDto>(doctor, Interactions, Interaction(Generic(), Generic(), InteractionSeverity.Major, active: true));
        var request = await AcceptedAsync(doctor, $"{Interactions}/{major.Id}",
            Interaction(major.DrugA, major.DrugB, InteractionSeverity.Major, active: false, major.Version) with { Reason = "Withdrawn from the formulary" });

        // Reworded meanwhile (saved at once): the approval no longer matches the row.
        await doctor.PutAsync<DrugInteractionDto>($"{Interactions}/{major.Id}",
            Interaction(major.DrugA, major.DrugB, InteractionSeverity.Major, active: true, major.Version) with { Effect = "Serious bleeding" });
        (await RefusalAsync(pharmacist, $"/api/v1/approvals/{request.Id}/approve", new ApproveRequest("Agreed.", request.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        (await InteractionAsync(doctor, major.Id)).IsActive.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Guid> PatientAsync() =>
        (await TeleconsultationJourneyTests.RegisterPatientAsync(await server.SignInAsync(HospitalServer.Users.FrontOffice))).Id;

    private static async Task<PrescriptionDto> SignedAsync(ApiClient doctor, Guid patientId, int refills, PrescriptionItemRequest line)
    {
        var draft = await doctor.PostAsync<PrescriptionDto>(Prescriptions, new CreatePrescriptionRequest(
            patientId, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: refills, [line]));
        return (await doctor.PostAsync<SignPrescriptionResult>($"{Prescriptions}/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: true, draft.Version))).Prescription;
    }

    private static PrescriptionItemRequest Line(string name, string generic, string form, string route) => new(
        DrugItemId: null, name, generic, Strength: null, form, Route: route, Dose: "1", DoseFrequency.BD, CustomFrequency: null, DosesPerDay: null,
        DurationDays: 3, Quantity: 6, Instructions: null, IsControlledDrug: false, ControlledSchedule: null);

    private static RefillPrescriptionRequest Refill(string? overrideReason) => new(overrideReason, AcknowledgeWarnings: true, ConfirmControlledDrugs: false);

    private static SaveDrugInteractionRequest Interaction(string a, string b, InteractionSeverity severity, bool active, uint? version = null) =>
        new(a, b, severity, "Additive toxicity", "Avoid together", active, version);

    private static SaveDoseLimitRequest Limit(string generic, decimal max, bool active, uint? version = null) => new(generic, max, "oral", null, active, version);

    /// <summary>A generic name no other test uses (letters only, as the tables keep names).</summary>
    private static string Generic() => "drug" + string.Concat(Enumerable.Range(0, 10).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static async Task<T> CreatedAsync<T>(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(ApiClient.Json, Ct))!;
    }

    private static async Task<ApprovalRequestDto> AcceptedAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Put, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
    }

    private static async Task<DrugInteractionDto> InteractionAsync(ApiClient api, Guid id)
    {
        // Each test's drugs are named only once, so the row is found by its first drug.
        var all = await api.GetAsync<PagedResult<DrugInteractionDto>>($"{Interactions}?pageSize=200&search=drug");
        return all.Items.FirstOrDefault(i => i.Id == id) ?? (await api.GetAsync<PagedResult<DrugInteractionDto>>($"{Interactions}?pageSize=200")).Items.Single(i => i.Id == id);
    }

    private static async Task<DoseLimitDto> DoseLimitAsync(ApiClient api, Guid id) => (await api.GetAsync<List<DoseLimitDto>>(DoseLimits)).Single(l => l.Id == id);

    private static async Task<(Guid Id, Guid? PractitionerId, string DisplayName)> MeAsync(ApiClient api)
    {
        var me = await api.GetAsync<JsonElement>("/api/v1/auth/me");
        var practitioner = me.GetProperty("practitionerId");
        return (me.GetProperty("id").GetGuid(), practitioner.ValueKind == JsonValueKind.Null ? null : practitioner.GetGuid(), me.GetProperty("displayName").GetString()!);
    }

    private async Task<List<UserNotification>> NotificationsAsync(Guid userId, string title)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .Where(n => n.UserId == userId && n.Title == title).ToListAsync(Ct);
    }

    private async Task<List<AuditEvent>> AuditAsync(string entityType, Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var entityId = id.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.EntityType == entityType && e.EntityId == entityId).ToListAsync(Ct);
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body, HttpMethod? method = null)
    {
        using var response = await api.SendRawAsync(method ?? HttpMethod.Post, path, body);
        response.IsSuccessStatusCode.ShouldBeFalse(await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }
}
