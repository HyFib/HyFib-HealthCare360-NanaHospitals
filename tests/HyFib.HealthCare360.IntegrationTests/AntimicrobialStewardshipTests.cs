using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Stewardship;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Features.Stewardship;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Antimicrobial stewardship over the real API. A doctor signs meropenem: it waits for the stewardship team's approval,
/// and the team is alerted. The doctor gives the justification. A day later, still unapproved, the pharmacy may not issue
/// it. The clinical microbiologist approves it (the prescriber may not); the pharmacy's refusal goes. The urine culture
/// comes back with Klebsiella sensitive to meropenem: the course falls due for review at once, with the antibiogram, and
/// the team narrows it.
/// </summary>
public sealed class AntimicrobialStewardshipTests(HospitalServer server)
{
    private const string Ams = "/api/v1/ams";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_restricted_antibiotic_waits_for_approval_and_the_culture_brings_its_review_forward()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var microbiologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var patientId = (await LabVerificationTests.RegisterPatientAsync(server)).Id;

        // Meropenem prescribed and signed: an approval waits, the prescription's only line.
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, null, null, EncounterType.OutPatient, null, null, null, "Fever with burning urination"));
        var meropenem = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Meropenem")).Single(d => d.Code == "DRG-MEROP1G");
        var draft = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patientId, encounter.Id, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null, RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    meropenem.Id, meropenem.Name, meropenem.GenericName, meropenem.Strength, meropenem.DosageForm, Route: "IV", "1 g", DoseFrequency.TDS,
                    CustomFrequency: null, DosesPerDay: null, 5, Quantity: null, Instructions: null, meropenem.IsControlled, meropenem.ControlledSchedule),
            ]));
        await doctor.PostAsync<SafetyCheckResponse>($"/api/v1/clinical/prescriptions/{draft.Id}/check");
        var signed = (await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: true, draft.Version))).Prescription;
        var approval = (await doctor.GetAsync<List<AntimicrobialApprovalDto>>($"{Ams}/approvals?queue=Pending&mine=true"))
            .Single(a => a.PrescriptionNumber == signed.PrescriptionNumber);
        (approval.Antimicrobial, approval.Group, approval.Status, approval.Route).ShouldBe(("Meropenem", AwareGroup.Watch, ApprovalStatus.Pending, "IV"));

        // The prescriber justifies it, but may not approve it.
        approval = await doctor.PostAsync<AntimicrobialApprovalDto>($"{Ams}/approvals/{approval.Id}/justification",
            new JustifyRequest(InfectionSite.UrinaryTract, "Urosepsis; ESBL Klebsiella on a culture last month", Empirical: true, CultureSent: true,
                approval.Version));
        using (var own = await doctor.SendRawAsync(HttpMethod.Post, $"{Ams}/approvals/{approval.Id}/decision", new DecideRequest(true, 5, null, approval.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // A day on, still unapproved: the pharmacy may not issue it.
        await BackdateAsync(approval.Id, TimeSpan.FromHours(25));
        var store = (await pharmacist.GetAsync<List<StoreDto>>("/api/v1/inventory/stores?type=Pharmacy")).Single(s => s.Code == "OPPH");
        var queue = await pharmacist.GetAsync<PagedResult<QueueListItem>>($"/api/v1/pharmacy/queue?search={signed.PrescriptionNumber}");
        QueueListItem waitingEntry = queue.Items.ShouldHaveSingleItem();
        var entry = await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{waitingEntry.Id}?storeId={store.Id}");
        var line = entry.Lines.ShouldHaveSingleItem();
        var dispense = new DispenseRequest(
            store.Id, [new DispenseLineRequest(line.Id, meropenem.Id, 1m, BatchId: null, SubstitutionReason: null, PrescriberConsent: false)],
            CounsellingNotes: null, DiscountPercent: 0m, [new SalePayment(PharmacyPaymentMode.Cash, 1500m, Reference: null)], ConfirmControlledDrugs: true);
        using (var waiting = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/queue/{entry.Id}/dispense", dispense))
        {
            (await ProblemCodeAsync(waiting)).ShouldBe("ams.approval_required");
        }

        var overdue = (await microbiologist.GetAsync<List<AntimicrobialApprovalDto>>($"{Ams}/approvals?queue=Pending")).Single(a => a.Id == approval.Id);
        overdue.ApprovalOverdue.ShouldBeTrue();
        (overdue.Indication, overdue.CultureSent).ShouldBe(("Urosepsis; ESBL Klebsiella on a culture last month", true));

        // The microbiologist approves it for five days; the pharmacy's refusal goes.
        approval = await microbiologist.PostAsync<AntimicrobialApprovalDto>($"{Ams}/approvals/{approval.Id}/decision",
            new DecideRequest(true, 5, "Approved pending culture", overdue.Version));
        (approval.Status, approval.ApprovedDays).ShouldBe((ApprovalStatus.Approved, 5));
        using (var approved = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/queue/{entry.Id}/dispense", dispense))
        {
            (await ProblemCodeAsync(approved)).ShouldNotBe("ams.approval_required");
        }

        // Not yet 72 hours, and no culture: no review.
        using (var early = await microbiologist.SendRawAsync(HttpMethod.Post, $"{Ams}/approvals/{approval.Id}/review",
                   new ReviewRequest(StewardshipDecision.DeEscalate, "Ertapenem", approval.Version)))
        {
            (await ProblemCodeAsync(early)).ShouldBe("ams.review_not_due");
        }

        // The urine culture: Klebsiella, sensitive to meropenem and to ertapenem.
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-CULTURE-URINE", patientId);
        var sheet = await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results");
        var entered = await lab.PostAsync<List<ResultSheetLine>>("/api/v1/lab/results", new EnterResultsRequest(
        [
            new ResultEntry(sheet.Lines.Single().ResultId, "Significant growth", null, "Klebsiella pneumoniae", "10^5 CFU/mL",
                [
                    new SusceptibilityDto("Meropenem", SusceptibilityInterpretation.Sensitive, "0.25"),
                    new SusceptibilityDto("Ertapenem", SusceptibilityInterpretation.Sensitive, null),
                    new SusceptibilityDto("Ceftriaxone", SusceptibilityInterpretation.Resistant, null),
                ]),
        ]));
        await microbiologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify",
            new VerifyResultsRequest([.. entered.Select(l => l.ResultId)], null, null, Release: true));

        var due = (await microbiologist.GetAsync<List<AntimicrobialApprovalDto>>($"{Ams}/approvals?queue=ReviewDue")).Single(a => a.Id == approval.Id);
        (due.Organism, due.Susceptibility, due.ReviewDue).ShouldBe(("Klebsiella pneumoniae", "Sensitive", true));
        var reviewed = await microbiologist.PostAsync<AntimicrobialApprovalDto>($"{Ams}/approvals/{approval.Id}/review",
            new ReviewRequest(StewardshipDecision.DeEscalate, "Ertapenem 1 g once daily, sensitive on the antibiogram", due.Version));
        reviewed.ReviewDecision.ShouldBe(StewardshipDecision.DeEscalate);

        var report = await microbiologist.GetAsync<StewardshipReport>($"{Ams}/report");
        report.ByGroup.Single(g => g.Group == AwareGroup.Watch).Approved.ShouldBeGreaterThanOrEqualTo(1);
        report.ReviewDecisions[StewardshipDecision.DeEscalate].ShouldBeGreaterThanOrEqualTo(1);
    }

    /// <summary>Moves the approval's prescribing time back, as if the hours had passed.</summary>
    private async Task BackdateAsync(Guid approvalId, TimeSpan by)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var approval = await db.Set<AntimicrobialApproval>().IgnoreQueryFilters().SingleAsync(a => a.Id == approvalId, Ct);
        approval.PrescribedAt -= by;
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var problem = JsonDocument.Parse(text).RootElement;
        return problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
