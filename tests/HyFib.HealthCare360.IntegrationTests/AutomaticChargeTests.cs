using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Charges a module posts by itself, day after day, never stop without anyone knowing (the documented gap "taking a
/// service out of use isn't checked against what charges it"), through the real API: the services master refuses to
/// take a service out of use while a ward's beds, a stay in progress or a diet are charged by it; and a day billing
/// refuses all the same (the service taken out of use outside the master) is kept, warned of on System health, holds the
/// stay's final bill and its discharge until it is charged, is posted again however long ago the stay ended, and is not
/// owed by a stay cancelled. The wards, beds, services and diets are the test's own.
/// </summary>
public sealed class AutomaticChargeTests(HospitalServer server)
{
    private const string Health = "/api/v1/admin/system/health";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_service_a_ward_a_stay_in_progress_or_a_diet_is_charged_by_is_not_taken_out_of_use()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var bedCharge = await BedServiceAsync(admin);
        var ward = await WardAsync(admin, bedCharge);
        var admission = await AdmitAsync(frontOffice, doctor, ward.Id, null);

        // Refused, naming the ward and the stay: billing would refuse their days.
        var refused = await OutOfUseRefusedAsync(admin, bedCharge);
        refused.ShouldContain($"the ward {ward.Name} charges its beds' days by it");
        refused.ShouldContain(admission.AdmissionNumber);

        // The ward re-pointed to another service: the stay keeps the charge its bed was taken with, so still refused.
        var other = await BedServiceAsync(admin);
        await admin.PutAsync($"/api/v1/inpatient/wards/{ward.Id}", WardRequest(ward.Code, ward.Name, other, ward.Version));
        refused = await OutOfUseRefusedAsync(admin, bedCharge);
        refused.ShouldNotContain("the ward");
        refused.ShouldContain($"1 stay(s) in progress are in beds charged by it, and keep that charge until they move bed or end ({admission.AdmissionNumber})");

        // The patient moved to a bed of a ward charged otherwise: nothing charges by it any more, and it goes out of use at once.
        var moved = await TestBeds.AddAsync(admin, ward.Id);
        await frontOffice.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/transfer", new TransferPatientRequest(moved.BedId, "Moved to the new bed class", null, null, null));
        (await OutOfUseAsync(admin, bedCharge)).IsActive.ShouldBeFalse();

        // A diet charged by a service: refused too, naming the diet.
        var dietCharge = await BedServiceAsync(admin, ServiceCategory.Dietary);
        var diet = await admin.PostAsync<DietTypeDto>("/api/v1/dietary/diet-types", new SaveDietTypeRequest(
            "DT" + Tag(), $"Renal diet {Tag()}", null, DietCategory.Therapeutic, dietCharge.Id, MealSlots.Standard, 99, true, null));
        (await OutOfUseRefusedAsync(admin, dietCharge)).ShouldContain($"the diet {diet.Name} charges its days by it");
    }

    [Fact]
    public async Task A_day_billing_refuses_is_warned_of_and_holds_the_final_bill_until_it_is_charged()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var system = await server.SignInAsync(HospitalServer.Users.Admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var bedCharge = await BedServiceAsync(admin);
        var ward = await WardAsync(admin, bedCharge);

        // Admitted yesterday morning, so yesterday's bed day is due.
        var today = await HospitalDayAsync();
        var yesterday = today.AddDays(-1);
        var admission = await AdmitAsync(frontOffice, doctor, ward.Id, IndiaClock.AtLocal(yesterday, new TimeOnly(9, 0)));

        // The bed's service taken out of use outside the services master (a direct change to the database, a restore):
        // billing refuses the day. It is not charged, it is kept, and System health names the stay.
        await SetInUseAsync(bedCharge.Id, false);
        await PostDueBedDaysAsync(admission.Id);
        await PostDueBedDaysAsync(admission.Id);
        (await ChargesAsync(billing, admission.Id)).ShouldNotContain(c => c.ServiceItemId == bedCharge.Id);
        var refusal = (await RefusalsAsync(admission.Id)).ShouldHaveSingleItem();
        (refusal.Code, refusal.Attempts, BillingTime.LocalDate(refusal.ServiceDate!.Value)).ShouldBe(("billing.service_inactive", 2, yesterday));
        var warning = (await system.GetAsync<SystemHealthReport>(Health)).Warnings.Single(w => w.Code == ChargeRefusals.RefusedCode);
        warning.Message.ShouldContain(admission.AdmissionNumber);
        warning.Message.ShouldContain(bedCharge.Name);

        // The discharge begins; the final bill is not finalized over the day.
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/initiate",
            new InitiateDischargeRequest(today, "Discharge advised on the ward round"));
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        var final = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/final-bill");
        using (var held = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{final.Id}/finalize", new BillVersionRequest(final.Version)))
        {
            held.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var text = await held.Content.ReadAsStringAsync(Ct);
            text.ShouldContain(ChargeRefusals.RefusedCode);
            Detail(text).ShouldContain($"Bed charges for {yesterday.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture)} ({bedCharge.Name} is no longer offered.)");
        }

        // Nor is the stay cleared for discharge over it, whatever else would clear it.
        var clearance = await nurse.GetAsync<DischargeClearanceDto>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/clearance");
        clearance.Cleared.ShouldBeFalse();
        clearance.Reason.ShouldNotBeNull().ShouldStartWith("Billing refused 1 automatic charge(s) of this stay");

        // Its cause fixed, the day is charged: the refusal goes, System health no longer names the stay, and the final
        // bill, refreshed, is finalized with the day on it.
        await SetInUseAsync(bedCharge.Id, true);
        await PostDueBedDaysAsync(admission.Id);
        (await ChargesAsync(billing, admission.Id)).ShouldContain(c => c.ServiceItemId == bedCharge.Id);
        (await RefusalsAsync(admission.Id)).ShouldBeEmpty();
        (await system.GetAsync<SystemHealthReport>(Health)).Warnings.ShouldNotContain(w => w.Message.Contains(admission.AdmissionNumber));
        final = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/final-bill");
        final = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{final.Id}/finalize", new BillVersionRequest(final.Version));
        final.Lines.ShouldContain(l => l.ServiceItemId == bedCharge.Id);
        (await AuditCountAsync("billing.charge_refused", admission.PatientId)).ShouldBe(1, "the first refusal is audited, not each retry");
        (await AuditCountAsync("billing.charge_refusal_cleared", admission.PatientId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_day_refused_for_a_stay_that_ended_is_posted_again_however_long_ago_it_ended()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var system = await server.SignInAsync(HospitalServer.Users.Admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var bedCharge = await BedServiceAsync(admin);
        var ward = await WardAsync(admin, bedCharge);
        var today = await HospitalDayAsync();
        var admission = await AdmitAsync(frontOffice, doctor, ward.Id, IndiaClock.AtLocal(today.AddDays(-1), new TimeOnly(9, 0)));

        // The patient absconds: the stay ends first, and its days are posted after. Billing refuses yesterday's (the bed's
        // service out of use outside the services master).
        await SetInUseAsync(bedCharge.Id, false);
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Absconded, null, "Left the ward unseen; the family and the police were told", NotifyPatient: false));
        (await RefusalsAsync(admission.Id)).ShouldHaveSingleItem().ServiceItemId.ShouldBe(bedCharge.Id);

        // As if it ended five days ago, past the days a discharged stay is revisited: its days are posted again all the
        // same, and System health names it.
        var endedAt = await EndedAtAsync(admission.Id);
        await SetEndedAtAsync(admission.Id, endedAt.AddDays(-5));
        (await StaysDueAsync()).ShouldContain(admission.Id);
        (await system.GetAsync<SystemHealthReport>(Health)).Warnings.Single(w => w.Code == ChargeRefusals.RefusedCode).Message.ShouldContain(admission.AdmissionNumber);

        // Its cause fixed and the day charged, the stay ended five days ago is posted no more.
        await SetEndedAtAsync(admission.Id, endedAt);
        await SetInUseAsync(bedCharge.Id, true);
        await PostDueBedDaysAsync(admission.Id);
        (await RefusalsAsync(admission.Id)).ShouldBeEmpty();
        await SetEndedAtAsync(admission.Id, endedAt.AddDays(-5));
        (await StaysDueAsync()).ShouldNotContain(admission.Id);
    }

    [Fact]
    public async Task A_stay_cancelled_owes_none_of_its_refused_days()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var bedCharge = await BedServiceAsync(admin);
        var ward = await WardAsync(admin, bedCharge);
        var today = await HospitalDayAsync();
        var admission = await AdmitAsync(frontOffice, doctor, ward.Id, IndiaClock.AtLocal(today.AddDays(-1), new TimeOnly(9, 0)));
        await SetInUseAsync(bedCharge.Id, false);
        await PostDueBedDaysAsync(admission.Id);
        (await RefusalsAsync(admission.Id)).ShouldHaveSingleItem();

        // Admitted in error and cancelled: the refused day is not owed, and the stay's days are posted no more.
        await frontOffice.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted under the wrong patient's record"));
        (await RefusalsAsync(admission.Id)).ShouldBeEmpty();
        (await StaysDueAsync()).ShouldNotContain(admission.Id);
        (await AuditCountAsync("billing.charge_refusal_cleared", admission.PatientId)).ShouldBe(1);
    }

    /// <summary>A bed charge of the test's own, in use at ₹1,500 a day (its price approved by the other administrator).</summary>
    private async Task<ServiceItemDto> BedServiceAsync(ApiClient admin, ServiceCategory category = ServiceCategory.BedCharge)
    {
        var tag = Tag();
        return await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            "BED-" + tag, $"Bed class {tag}", null, category, null, 1500m, 0m, null,
            IsDiscountAllowed: false, IsPriceEditable: false, Description: null, IsActive: true, Version: null));
    }

    /// <summary>A general ward of the test's own, its beds charged by the service, as the ward master reads it back.</summary>
    private static async Task<WardDto> WardAsync(ApiClient admin, ServiceItemDto bedCharge)
    {
        var tag = Tag();
        var created = await admin.PostAsync<JsonElement>("/api/v1/inpatient/wards", WardRequest("W" + tag, $"Ward {tag}", bedCharge, null));
        return await admin.GetAsync<WardDto>($"/api/v1/inpatient/wards/{created.GetProperty("id").GetGuid()}");
    }

    private static SaveWardRequest WardRequest(string code, string name, ServiceItemDto bedCharge, uint? version) => new(
        code, name, WardType.General, null, null, WardGenderPolicy.Any, null, null, null, bedCharge.Id, 99, true, null, version);

    /// <summary>Front office: admits a new patient under the doctor to a new bed of the ward.</summary>
    private async Task<AdmittedStay> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid wardId, DateTimeOffset? admittedAt)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patientId = (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Arjun"), MiddleName: null, LastName: "Bed" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1958, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;
        var bed = await TestBeds.AddAsync(server, wardId);
        var admitted = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Community-acquired pneumonia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: admittedAt, Notes: null));
        return new AdmittedStay(admitted.Id, admitted.AdmissionNumber, patientId);
    }

    /// <summary>Asks to take the service out of use; refused (409), the refusal's words.</summary>
    private static async Task<string> OutOfUseRefusedAsync(ApiClient admin, ServiceItemDto service)
    {
        var current = await admin.GetAsync<ServiceItemDto>($"/api/v1/masters/services/{service.Id}");
        using var response = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/masters/services/{service.Id}", OutOfUse(current));
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, text);
        text.ShouldContain(ServiceChargeSources.InUseCode);
        return Detail(text);
    }

    /// <summary>A problem's words as written, not as JSON escapes them.</summary>
    private static string Detail(string problem) => JsonDocument.Parse(problem).RootElement.GetProperty("detail").GetString() ?? string.Empty;

    /// <summary>Takes the service out of use through the services master; the service as the master reads it back.</summary>
    private static async Task<ServiceItemDto> OutOfUseAsync(ApiClient admin, ServiceItemDto service)
    {
        var current = await admin.GetAsync<ServiceItemDto>($"/api/v1/masters/services/{service.Id}");
        await admin.PutAsync($"/api/v1/masters/services/{service.Id}", OutOfUse(current));
        return await admin.GetAsync<ServiceItemDto>($"/api/v1/masters/services/{service.Id}");
    }

    private static SaveServiceItemRequest OutOfUse(ServiceItemDto s) => new(
        s.Code, s.Name, s.ShortName, s.Category, s.DepartmentId, s.BasePrice, s.TaxRate, s.SacCode, s.IsDiscountAllowed, s.IsPriceEditable,
        s.Description, IsActive: false, Version: s.Version);

    /// <summary>Puts the service in or out of use in the database itself, outside the services master and its checks.</summary>
    private async Task SetInUseAsync(Guid serviceItemId, bool inUse)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ServiceItem>()
            .Where(s => s.Id == serviceItemId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, inUse), Ct);
    }

    /// <summary>Charges the bed and nursing days due so far, as the hourly bed-day job does.</summary>
    private async Task PostDueBedDaysAsync(Guid admissionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<InpatientChargeService>().PostDueBedDaysAsync(admissionId, null, Ct);
    }

    /// <summary>The stays the hourly bed-day job posts the days of.</summary>
    private async Task<IReadOnlyList<Guid>> StaysDueAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InpatientChargeService>().StaysDueAsync(Ct);
    }

    /// <summary>When the stay ended, as recorded.</summary>
    private async Task<DateTimeOffset> EndedAtAsync(Guid admissionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var endedAt = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Admission>()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(a => a.Id == admissionId)
            .Select(a => a.DischargedAt)
            .SingleAsync(Ct);
        return endedAt.ShouldNotBeNull();
    }

    /// <summary>Records the stay as ended at another time, in the database itself (as if it ended that long ago).</summary>
    private async Task SetEndedAtAsync(Guid admissionId, DateTimeOffset endedAt)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Admission>()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(a => a.Id == admissionId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DischargedAt, (DateTimeOffset?)endedAt), Ct);
    }

    private async Task<List<ChargeRefusal>> RefusalsAsync(Guid admissionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ChargeRefusal>().AsNoTracking()
            .Where(r => r.AdmissionId == admissionId).ToListAsync(Ct);
    }

    private async Task<int> AuditCountAsync(string action, Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == action && e.PatientId == patientId, Ct);
    }

    private static async Task<List<ChargeDto>> ChargesAsync(ApiClient billing, Guid admissionId) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?admissionId={admissionId}&pageSize=200"))
        .Items.Where(c => c.Status != ChargeStatus.Cancelled).ToList();

    /// <summary>Today in the hospital's calendar (IST), waiting out the last two minutes of a day so no midnight falls inside the test.</summary>
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

    private static string Tag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record AdmittedStay(Guid Id, string AdmissionNumber, Guid PatientId);
}
