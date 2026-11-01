using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// GST on room rent, over the real API. Health care by a hospital is exempt, but since 18 July 2022 a room other than an
/// ICU, CCU, ICCU or NICU charged above ₹5,000 a day is taxed at 5% (2.5% CGST and 2.5% SGST) on the whole charge
/// (notification 12/2017-Central Tax (Rate) entry 74, as amended by 4/2022; 11/2017-CT(R) entry 31A, inserted by 3/2022).
/// The seeded wards: deluxe suites at ₹6,500 a day, the ICU at ₹8,000 and private rooms at ₹4,000.
/// </summary>
public sealed class RoomRentGstTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_day_in_a_room_above_five_thousand_is_taxed_at_five_percent_and_a_day_in_the_icu_or_a_cheaper_room_is_not()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var today = await HospitalDayAsync();
        var dayBefore = today.AddDays(-2);

        // Three patients admitted the day before yesterday, recorded as late entries: one to a deluxe suite, one to the
        // ICU and one to a private room.
        var stays = new Dictionary<string, AdmissionCreated>();
        foreach (var ward in new[] { "DLX", "ICU", "PVT" })
        {
            stays[ward] = await AdmitAsync(frontOffice, doctor, await RegisterPatientAsync(frontOffice), ward, IndiaClock.AtLocal(dayBefore, new TimeOnly(10, 0)));
        }

        try
        {
            // The bed-day job charges the two midnights each has spent. Meanwhile the ICU has been re-classed as a general
            // ward: a day keeps the class its bed had when the stay began there, as the consultant's visit does.
            await using (await WardTypeAsync("ICU", WardType.General))
            {
                foreach (var stay in stays.Values)
                {
                    await PostDueBedDaysAsync(stay.Id);
                }
            }

            // Deluxe suite, ₹6,500 a day: ₹325 of GST on each day.
            var deluxe = await BedDaysAsync(billing, stays["DLX"].Id);
            deluxe.Count.ShouldBe(2);
            deluxe.ShouldAllBe(c => c.UnitPrice == 6_500m && c.TaxAmount == 325m && c.NetAmount == 6_825m);

            // The ICU, ₹8,000 a day, is exempt whatever it costs; a private room, ₹4,000 a day, is under the line.
            (await BedDaysAsync(billing, stays["ICU"].Id)).ShouldAllBe(c => c.UnitPrice == 8_000m && c.TaxAmount == 0m);
            (await BedDaysAsync(billing, stays["PVT"].Id)).ShouldAllBe(c => c.UnitPrice == 4_000m && c.TaxAmount == 0m);

            // On the bill, each day of the suite is a line at 5%: CGST and SGST of ₹162.50 each.
            var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{stays["DLX"].Id}");
            var interim = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/interim-bills", new CreateInterimBillRequest(null));
            var days = interim.Lines.Where(l => deluxe.Select(c => c.Id).Contains(l.ChargeId)).ToList();
            days.Count.ShouldBe(2);
            days.ShouldAllBe(l => l.TaxRate == 5m && l.CgstAmount == 162.50m && l.SgstAmount == 162.50m);
            using (var discarded = await billing.SendRawAsync(HttpMethod.Delete, $"/api/v1/billing/bills/{interim.Id}?version={interim.Version}"))
            {
                discarded.EnsureSuccessStatusCode();
            }
        }
        finally
        {
            // The beds go back for the rest of the run, which shares them (the deluxe ward has four): the stays are cancelled,
            // and the nurse cleans the beds they left, as a bed is taken again only once housekeeping has made it vacant.
            foreach (var stay in stays.Values)
            {
                await frontOffice.PostAsync($"/api/v1/inpatient/admissions/{stay.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of room-rent GST"));
            }

            var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
            foreach (var task in (await nurse.GetAsync<List<HousekeepingTaskDto>>("/api/v1/beds/housekeeping?status=Pending"))
                     .Where(t => stays.Values.Any(s => s.Id == t.AdmissionId)))
            {
                await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
                await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned after the stay"));
            }
        }
    }

    private static async Task<List<ChargeDto>> BedDaysAsync(ApiClient billing, Guid admissionId) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?admissionId={admissionId}&pageSize=200"))
        .Items.Where(c => c.Status != ChargeStatus.Cancelled && c.Category == ServiceCategory.BedCharge).ToList();

    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice)
    {
        var surname = Letters(8);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Lakshmi"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    /// <summary>Front office: admits the patient, paying for herself, to a free bed of <paramref name="wardCode"/>.</summary>
    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId, string wardCode, DateTimeOffset admittedAt)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == wardCode);
        var bed = await TestBeds.AddAsync(server, ward.Id);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Community-acquired pneumonia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: admittedAt, Notes: null));
    }

    /// <summary>Charges the bed and nursing days due so far, as the hourly bed-day job (<c>BedDayChargeJob</c>) does.</summary>
    private async Task PostDueBedDaysAsync(Guid admissionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<InpatientChargeService>().PostDueBedDaysAsync(admissionId, null, Ct);
    }

    /// <summary>The ward's type set to <paramref name="type"/>, as if edited in its master; disposed, the type it had is put back.</summary>
    private async Task<IAsyncDisposable> WardTypeAsync(string code, WardType type)
    {
        var before = await SetWardTypeAsync(code, type);
        return new Restore(() => SetWardTypeAsync(code, before));
    }

    private async Task<WardType> SetWardTypeAsync(string code, WardType type)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var ward = await db.Set<Ward>().IgnoreQueryFilters().SingleAsync(w => w.Code == code && !w.IsDeleted, CancellationToken.None);
        var before = ward.Type;
        ward.Type = type;
        await db.SaveChangesAsync(CancellationToken.None);
        return before;
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }

    /// <summary>Today in the hospital's calendar (IST); started in the last two minutes of a day, it waits for the next.</summary>
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

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
