using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using BillingChargeStatus = HyFib.HealthCare360.Modules.Billing.Domain.ChargeStatus;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Dialysis session charges over the real API (DIA-05), as Billing records them: completing a session charges the
/// session, and the dialyzer only on its first use. Charges are idempotent on their source line, so retrying a session's
/// charges, as staff do after a Billing outage, never charges anything twice.
/// </summary>
public sealed class DialysisChargeTests(HospitalServer server)
{
    [Fact]
    public async Task A_session_is_charged_with_a_new_dialyzer_only_on_its_first_use_and_a_retry_charges_nothing_twice()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var today = server.Services.GetRequiredService<TimeProvider>().Today();

        // The nephrologist enrols a sero-negative patient whose dialyzer is reprocessed and reused up to 8 times.
        var patient = await RegisterPatientAsync();
        await doctor.PutAsync<JsonElement>($"/api/v1/dialysis/patients/{patient.Id}", new SaveDialysisProfileRequest(
            DryWeightKg: 62m, VascularAccess: VascularAccessType.AVF, AccessSite: "Left radiocephalic", AccessCreatedOn: null,
            ScheduleDays: [Weekday.Monday, Weekday.Thursday], Shift: DialysisShift.Morning, SessionMinutes: 240, SeroStatus: SeroStatus.Negative,
            SeroTestedOn: today.AddMonths(-1), ReusePolicy: DialyzerReusePolicy.Reuse, MaxDialyzerUses: 8, NephrologistId: null,
            PrimaryDiagnosis: "CKD stage 5 (diabetic nephropathy)", AnticoagulationNotes: null, IsActive: true, Version: null));
        var machine = (await nurse.GetAsync<List<DialysisMachineDto>>("/api/v1/dialysis/machines"))
            .First(m => m.SeroDedication == SeroStatus.Negative && m.IsActive && m.Status == DialysisMachineStatus.Available);

        // Morning: his first session, on a new dialyzer opened at the chair.
        var first = await RunSessionAsync(nurse, patient.Id, machine, today, DialysisShift.Morning,
            new StartDialysisRequest(machine.Id, null, new RegisterDialyzerRequest("FX80-" + Letters(8).ToUpperInvariant(), "Fresenius", "FX80", 1.8m), 2000, 500));
        first.DialyzerUseNumber.ShouldBe(1);
        var dialyzerId = first.DialyzerId.ShouldNotBeNull();

        // Billing has the session and the new dialyzer.
        var charged = await DialysisChargesAsync(billing, patient.Id);
        charged.Select(c => (c.ServiceCode, c.SourceReference)).ShouldBe(
            [(DialysisServiceCodes.Session, $"{first.Id}:session"), (DialysisServiceCodes.Dialyzer, $"dialyzer:{dialyzerId}")], ignoreOrder: true);
        charged.ShouldAllBe(c => c.Quantity == 1m && c.Status == BillingChargeStatus.Unbilled);

        // Afternoon: an extra session on the same, reprocessed dialyzer. Only the session is charged.
        var second = await RunSessionAsync(nurse, patient.Id, machine, today, DialysisShift.Afternoon,
            new StartDialysisRequest(machine.Id, dialyzerId, null, 2000, 500));
        (second.DialyzerId, second.DialyzerUseNumber).ShouldBe((dialyzerId, 2));
        var afterReuse = await DialysisChargesAsync(billing, patient.Id);
        afterReuse.Select(c => (c.ServiceCode, c.SourceReference)).ShouldBe(
            [
                (DialysisServiceCodes.Session, $"{first.Id}:session"), (DialysisServiceCodes.Dialyzer, $"dialyzer:{dialyzerId}"),
                (DialysisServiceCodes.Session, $"{second.Id}:session"),
            ], ignoreOrder: true);

        // Retrying the charges of either session, the dialyzer's first use included, posts nothing again.
        foreach (var session in new[] { first, second })
        {
            var retried = await nurse.PostAsync<ChargeOutcome>($"/api/v1/dialysis/sessions/{session.Id}/charges/retry");
            (retried.Status, retried.Error).ShouldBe((ChargeStatus.Posted, null));
        }

        var afterRetries = await DialysisChargesAsync(billing, patient.Id);
        afterRetries.Select(c => c.Id).ShouldBe(afterReuse.Select(c => c.Id), ignoreOrder: true);
    }

    /// <summary>
    /// Nurse: schedules the session on the machine, disinfects the machine, records the pre-dialysis assessment, connects
    /// the patient, and completes the session, which posts its charges.
    /// </summary>
    private static async Task<DialysisSessionDetail> RunSessionAsync(
        ApiClient nurse, Guid patientId, DialysisMachineDto machine, DateOnly date, DialysisShift shift, StartDialysisRequest start)
    {
        var id = (await nurse.PostAsync<JsonElement>("/api/v1/dialysis/sessions", new ScheduleDialysisRequest(patientId, date, shift, machine.Id, null, null)))
            .GetProperty("id").GetGuid();
        await nurse.PostAsync($"/api/v1/dialysis/machines/{machine.Id}/disinfection", new RecordDisinfectionRequest("Heat disinfection with citric acid"));
        await nurse.PostAsync($"/api/v1/dialysis/sessions/{id}/pre-assessment", new PreDialysisRequest(64.5m, 150, 90, 82, 36.8m, 2500));
        await nurse.PostAsync($"/api/v1/dialysis/sessions/{id}/start", start);
        await nurse.PostAsync($"/api/v1/dialysis/sessions/{id}/complete", new CompleteDialysisRequest(62.1m, 130, 80, 78, null, [], null));

        var session = await nurse.GetAsync<DialysisSessionDetail>($"/api/v1/dialysis/sessions/{id}");
        session.Status.ShouldBe(DialysisSessionStatus.Completed);
        (session.ChargeStatus, session.ChargeError).ShouldBe((ChargeStatus.Posted, null));
        return session;
    }

    /// <summary>The patient's charges that dialysis posted, as the billing desk sees them.</summary>
    private static async Task<List<ChargeDto>> DialysisChargesAsync(ApiClient billing, Guid patientId) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?patientId={patientId}&pageSize=200")).Items
            .Where(c => c.SourceModule == ChargeSources.Dialysis)
            .ToList();

    private async Task<PatientCreated> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Anand", MiddleName: null, LastName: "Dialysis" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
