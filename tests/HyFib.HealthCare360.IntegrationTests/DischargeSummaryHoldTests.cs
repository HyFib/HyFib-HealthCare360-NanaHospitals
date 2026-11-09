using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.DischargeStays;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A planned discharge waits for the signed discharge summary (<see cref="DischargeSummaryDischargeHold"/>), over the real
/// API and PostgreSQL: refused while the summary is unwritten or a draft, made once the doctor signs it; a patient leaving
/// against advice is not held by it. The rule is on by default; the run's other tests discharge with it off hospital-wide
/// (HospitalServer), so each test here turns it on for its facility and takes that away afterwards.
/// </summary>
public sealed class DischargeSummaryHoldTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_planned_discharge_is_refused_until_the_discharge_summary_is_signed()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        await using var on = await RuleOnAsync(doctor);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        await InitiateAndClearAsync(server, nurse, stay.Id);
        var home = new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home in a stable condition", NotifyPatient: false);

        // No summary written yet.
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), home)).ShouldBe((HttpStatusCode.UnprocessableEntity, DischargeSummaryDischargeHold.Code));

        // A draft is not enough, for a referral either.
        var draft = await doctor.PutAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/by-admission/{stay.Id}", Draft(patient.Id, null));
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), home)).ShouldBe((HttpStatusCode.UnprocessableEntity, DischargeSummaryDischargeHold.Code));
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), home with { DischargeType = DischargeType.Referred }))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, DischargeSummaryDischargeHold.Code));
        (await doctor.GetAsync<JsonElement>($"{Admissions}/{stay.Id}")).GetProperty("status").GetString().ShouldBe(nameof(AdmissionStatus.DischargeInitiated),
            "the refused discharge changed nothing");

        // Signed by the consultant: the patient goes home.
        (await doctor.PostAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/{draft.Id}/sign", new SignDischargeSummaryRequest(draft.Version)))
            .Status.ShouldBe(DischargeSummaryStatus.Signed);
        (await nurse.PostAsync<DischargeResult>(Complete(stay.Id), home)).Status.ShouldBe(AdmissionStatus.Discharged);
        await CleanBedAsync(nurse, stay.Id);
    }

    [Fact]
    public async Task A_patient_leaving_against_advice_is_not_held_by_an_unsigned_summary()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        await using var on = await RuleOnAsync(doctor);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        await InitiateAndClearAsync(server, nurse, stay.Id);
        var witness = await server.WitnessAsync(doctor, Users.Nurse, DischargeService.AgainstAdviceWitness.Key, patient.Id);
        var lama = new CompleteDischargeRequest(DischargeType.Lama, null, "Left against advice after the risks of stopping antibiotics were explained",
            NotifyPatient: false, AgainstAdvice: new AgainstAdviceRequest(AgainstAdviceSignatory.Patient, null, null, null, witness.ConfirmationId, null));

        (await doctor.PostAsync<DischargeResult>(Complete(stay.Id), lama)).Status.ShouldBe(AdmissionStatus.Lama);
        await CleanBedAsync(nurse, stay.Id);
    }

    private static SaveDischargeSummaryRequest Draft(Guid patientId, uint? version) => new(
        patientId, null, null, null, null, "Community-acquired pneumonia", [], "Fever and cough", "Treated with IV antibiotics; afebrile for 48 hours", null, null,
        "Stable, ambulant", "Amoxicillin-clavulanate 625 mg BD x 5 days", "Plenty of fluids", null, null, version);

    /// <summary>The rule on for the doctor's facility (stricter: no second administrator), removed afterwards.</summary>
    private async Task<IAsyncDisposable> RuleOnAsync(ApiClient doctor)
    {
        var facilityId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(DischargeSummaryDischargeHold.Setting, true, facilityId, cancellationToken: Ct);
        }

        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(DischargeSummaryDischargeHold.Setting, facilityId, Ct);
        });
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
