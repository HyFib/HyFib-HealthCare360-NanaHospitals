using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Diet charges to the in-patient bill over the real API (DIET-05). With the facility setting
/// <c>supportservices.diet.chargesEnabled</c> on, generating the day's meal plan charges an admitted patient's diet to his
/// IP account once a day, and a day already charged under an earlier diet order is not charged again when the diet
/// changes during the day. Off, as it is unless an administrator turns it on, nothing is charged: many hospitals include
/// food in the bed tariff.
/// </summary>
public sealed class DietChargeTests(HospitalServer server)
{
    private const string GeneratePath = "/api/v1/dietary/meal-plans/generate";
    private const string OrdersPath = "/api/v1/dietary/orders";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_diet_charges_on_an_admitted_patients_diet_is_charged_once_a_day_even_when_it_changes()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var today = await HospitalDayAsync();
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");

        // He was admitted yesterday morning (entered late), and the doctor wrote a normal diet for him then.
        var admission = await AdmitAsync(IndiaClock.AtLocal(today.AddDays(-1), new TimeOnly(10, 0)));
        var normal = await DietTypeAsync(doctor, "NORMAL");
        var normalOrder = await doctor.PostAsync<DietOrderDto>(OrdersPath, NewOrder(admission.Id, normal.Id));
        await StartOnAsync(normalOrder.Id, today.AddDays(-1));

        // The hospital bills food separately: the administrator turns diet charges on for the facility.
        await SetDietChargesAsync(admin, facilityId, enabled: true);
        try
        {
            // The kitchen's plan for today charges the day to his in-patient account, at the normal diet's service.
            await nurse.PostAsync<MealPlanResult>(GeneratePath, new GenerateMealPlanRequest(today));
            var charge = (await DietChargesAsync(billing, admission.Id)).ShouldHaveSingleItem();
            charge.ServiceCode.ShouldBe(normal.ChargeServiceCode);
            charge.SourceReference.ShouldBe(DietChargeKeys.For(normalOrder.Id, today));
            IndiaClock.LocalDate(charge.ServiceDate).ShouldBe(today);
            charge.Status.ShouldBe(ChargeStatus.Unbilled);
            var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
            var running = await billing.GetAsync<RunningBill>($"/api/v1/billing/ip-accounts/{account.Id}/running-bill");
            running.Lines.ShouldContain(l => l.ChargeId == charge.Id, "the diet charge is on the running in-patient bill");

            // The plan is generated again every hour: the day is not charged twice.
            await nurse.PostAsync<MealPlanResult>(GeneratePath, new GenerateMealPlanRequest(today));
            (await DietChargesAsync(billing, admission.Id)).ShouldHaveSingleItem().Id.ShouldBe(charge.Id);

            // At noon the doctor changes him to a diabetic diet. Today was charged under the normal diet already, so the
            // regenerated plan does not charge the day again (the diabetic diet is charged from tomorrow).
            var diabetic = await DietTypeAsync(doctor, "DIABETIC");
            diabetic.ChargeServiceCode.ShouldNotBe(normal.ChargeServiceCode, "a therapeutic diet costs more, so a second charge would show");
            await doctor.PostAsync<DietOrderDto>(OrdersPath, NewOrder(admission.Id, diabetic.Id));
            await nurse.PostAsync<MealPlanResult>(GeneratePath, new GenerateMealPlanRequest(today));
            (await DietChargesAsync(billing, admission.Id)).ShouldHaveSingleItem().Id.ShouldBe(charge.Id);
        }
        finally
        {
            await SetDietChargesAsync(admin, facilityId, enabled: false);
        }
    }

    [Fact]
    public async Task With_diet_charges_off_as_they_are_by_default_no_diet_is_charged()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var today = await HospitalDayAsync();

        // The diet has a charge service; only the setting keeps it off the bill.
        var admission = await AdmitAsync(admittedAt: null);
        var normal = await DietTypeAsync(doctor, "NORMAL");
        normal.ChargeServiceCode.ShouldNotBeNull();
        await doctor.PostAsync<DietOrderDto>(OrdersPath, NewOrder(admission.Id, normal.Id));

        var plan = await nurse.PostAsync<MealPlanResult>(GeneratePath, new GenerateMealPlanRequest(today));
        plan.ChargesPosted.ShouldBe(0);
        (await DietChargesAsync(billing, admission.Id)).ShouldBeEmpty();
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        (await billing.GetAsync<RunningBill>($"/api/v1/billing/ip-accounts/{account.Id}/running-bill")).Lines
            .ShouldNotContain(l => l.ServiceCode == normal.ChargeServiceCode);
    }

    /// <summary>
    /// Front office: registers a new patient and admits him, self-paying, to a private room under the doctor;
    /// <paramref name="admittedAt"/> is a late entry's admission time (null: now).
    /// </summary>
    private async Task<AdmissionCreated> AdmitAsync(DateTimeOffset? admittedAt)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var consultantId = (await doctor.GetAsync<AuthUser>("/api/v1/auth/me")).PractitionerId
                           ?? throw new InvalidOperationException("The doctor's account is not linked to a practitioner.");
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Gopal", MiddleName: null, LastName: "Diet" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1955, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));

        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "PVT");
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patient.Id}&wardId={ward.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Uncontrolled type 2 diabetes", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: admittedAt, Notes: null));
    }

    private static CreateDietOrderRequest NewOrder(Guid admissionId, Guid dietTypeId) =>
        new(admissionId, dietTypeId, FoodPreference.Vegetarian, null, null, null, null, null, null, null, null);

    private static async Task<DietTypeDto> DietTypeAsync(ApiClient api, string code) =>
        (await api.GetAsync<List<DietTypeDto>>("/api/v1/dietary/diet-types")).Single(t => t.Code == code);

    /// <summary>The admission's diet charges, as the billing desk sees them.</summary>
    private static async Task<List<ChargeDto>> DietChargesAsync(ApiClient billing, Guid admissionId) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?admissionId={admissionId}&pageSize=200")).Items
            .Where(c => c.SourceModule == DietChargeKeys.Source)
            .ToList();

    /// <summary>Moves a diet order's start to an earlier day, as if it had been written then (the API starts orders today).</summary>
    private async Task StartOnAsync(Guid dietOrderId, DateOnly startDate)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<DietOrder>().IgnoreQueryFilters()
            .Where(o => o.Id == dietOrderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.StartDate, startDate), Ct);
        updated.ShouldBe(1);
    }

    private static Task SetDietChargesAsync(ApiClient admin, Guid facilityId, bool enabled) =>
        admin.PutAsync($"/api/v1/admin/settings/{MealPlanService.DietChargesSetting}", new SaveSettingRequest(JsonValue.Create(enabled), facilityId, false, null));

    /// <summary>
    /// Today in the hospital's calendar (IST). Diet charges are per calendar day, so the test must not straddle midnight:
    /// started in the last two minutes of a day, it waits for the next.
    /// </summary>
    private async Task<DateOnly> HospitalDayAsync()
    {
        var clock = server.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var untilMidnight = IndiaClock.StartOfDayUtc(IndiaClock.LocalDate(now).AddDays(1)) - now;
        if (untilMidnight < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(untilMidnight + TimeSpan.FromSeconds(1), clock, Ct);
        }

        return IndiaClock.LocalDate(clock.GetUtcNow());
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
