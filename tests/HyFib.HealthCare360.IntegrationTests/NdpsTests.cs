using System.Net;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Ndps;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The NDPS accounts over the real API, from a store's controlled-drug register: the day's account of its morphine
/// (Form 3H), the patient's record (Form 3E), the year's return and next year's estimate (Form 3J), and the blind
/// two-person count at a change of shift, whose difference is explained and reported.
/// </summary>
public sealed class NdpsTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stores_morphine_is_accounted_for_by_day_by_patient_and_by_year()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var (store, morphine) = await StoreWithMorphineAsync();
        var day = new DateOnly(2019, 3, 12);
        var at = HospitalClock.StartOfDayUtc(day);

        // 20 ampoules received the day before; on the day, two dispensed to the patient and ten more received.
        await RegisterAsync(store, morphine, at.AddHours(-10), StockMovementType.Grn, quantityIn: 20, balance: 20, reference: "GRN-19-0042");
        await RegisterAsync(store, morphine, at.AddHours(9), StockMovementType.Dispense, quantityOut: 2, balance: 18, patient: patient);
        await RegisterAsync(store, morphine, at.AddHours(15), StockMovementType.Grn, quantityIn: 10, balance: 28, reference: "GRN-19-0051");

        var account = await pharmacist.GetAsync<NdpsDailyAccount>($"/api/v1/pharmacy/ndps/daily?storeId={store}&date={day:yyyy-MM-dd}");
        var drug = account.Drugs.ShouldHaveSingleItem();
        (drug.Opening, drug.Received, drug.Dispensed, drug.Closing, drug.Balanced).ShouldBe((20m, 10m, 2m, 28m, true));
        drug.Movements.Single(m => m.Type == StockMovementType.Dispense).PatientUhid.ShouldBe(patient.Uhid);

        var record = await pharmacist.GetAsync<NdpsPatientRecord>($"/api/v1/pharmacy/ndps/patients/{patient.Id}");
        record.Uhid.ShouldBe(patient.Uhid);
        record.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(2);

        var year = await pharmacist.GetAsync<NdpsAnnualReturn>("/api/v1/pharmacy/ndps/annual?year=2019");
        var annual = year.Drugs.Single(d => d.ItemId == morphine);
        (annual.Received, annual.Consumed, annual.Closing, annual.EstimateNextYear).ShouldBe((30m, 2m, 28m, 3m));

        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        using (var notTheNurses = await nurse.SendRawAsync(HttpMethod.Get, $"/api/v1/pharmacy/ndps/daily?storeId={store}&date={day:yyyy-MM-dd}"))
        {
            notTheNurses.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task The_shift_count_is_blind_witnessed_by_a_second_person_and_a_difference_is_explained_and_reported()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var (store, morphine) = await StoreWithMorphineAsync();
        await RegisterAsync(store, morphine, DateTimeOffset.UtcNow.AddHours(-3), StockMovementType.Grn, quantityIn: 12, balance: 12, reference: "GRN-26-0007");

        // The sheet names the drugs to count, not their balance.
        var sheet = await nurse.GetAsync<NarcoticCountSheet>($"/api/v1/pharmacy/ndps/counts/sheet?storeId={store}");
        sheet.Items.ShouldHaveSingleItem().ItemId.ShouldBe(morphine);

        using (var nothingCounted = await nurse.SendRawAsync(HttpMethod.Post, "/api/v1/pharmacy/ndps/counts",
                   new RecordNarcoticCountRequest(store, [new CountedDrug(Guid.NewGuid(), 1)], null)))
        {
            nothingCounted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // The night nurse counts 11; the register says 12.
        var count = await nurse.PostAsync<NarcoticCountDto>("/api/v1/pharmacy/ndps/counts",
            new RecordNarcoticCountRequest(store, [new CountedDrug(morphine, 11)], null));
        (count.Status, count.HasDifference).ShouldBe((NarcoticCountStatus.Counted, true));
        count.Lines.ShouldHaveSingleItem().Difference.ShouldBe(-1);

        // She cannot witness her own count; the pharmacist must explain the difference before witnessing it.
        using (var self = await nurse.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/ndps/counts/{count.Id}/witness",
                   new WitnessNarcoticCountRequest("Broken", count.Version)))
        {
            self.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        using (var unexplained = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/ndps/counts/{count.Id}/witness",
                   new WitnessNarcoticCountRequest(null, count.Version)))
        {
            unexplained.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var witnessed = await pharmacist.PostAsync<NarcoticCountDto>($"/api/v1/pharmacy/ndps/counts/{count.Id}/witness",
            new WitnessNarcoticCountRequest("One ampoule broken while drawing up at 05:40; remains kept for destruction", count.Version));
        witnessed.Status.ShouldBe(NarcoticCountStatus.Witnessed);

        // The pharmacist in charge is told, with the drug and the difference.
        var alert = await EventuallyAsync(async () => (await pharmacist.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
            .FirstOrDefault(n => n.Title == $"Narcotic count differs: {sheet.StoreName}"), "The pharmacist in charge was not told.");
        alert.Body.ShouldNotBeNull().ShouldContain("-1");
    }

    /// <summary>A store of its own for the test, so no other test's stock moves its balances.</summary>
    private async Task<(Guid Store, Guid Morphine)> StoreWithMorphineAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facility = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("No facility.");
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var morphine = await db.Set<Item>().IgnoreQueryFilters().Where(i => i.Code == "DRG-MORPH10").Select(i => i.Id).SingleAsync(Ct);
        var code = "NDPS" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var store = new Store { FacilityId = facility, Code = code, Name = $"Night pharmacy {code}", Type = StoreType.Pharmacy };
        db.Add(store);
        await db.SaveChangesAsync(Ct);
        return (store.Id, morphine);
    }

    private async Task RegisterAsync(
        Guid store, Guid item, DateTimeOffset at, StockMovementType type, decimal balance, decimal quantityIn = 0, decimal quantityOut = 0,
        string? reference = null, PatientCreated? patient = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var facility = await db.Set<Store>().IgnoreQueryFilters().Where(s => s.Id == store).Select(s => s.FacilityId).SingleAsync(Ct);
        db.Add(new ControlledDrugRegisterEntry
        {
            FacilityId = facility,
            OccurredAt = at,
            StoreId = store,
            ItemId = item,
            ItemName = "Morphine 10 mg/ml Injection",
            Schedule = DrugSchedule.X,
            IsControlled = true,
            BatchNumber = "MOR2401",
            MovementType = type,
            QuantityIn = quantityIn,
            QuantityOut = quantityOut,
            BalanceAfter = balance,
            PatientId = patient?.Id,
            PatientName = patient is null ? null : "Test patient",
            PatientUhid = patient?.Uhid,
            PrescriberName = patient is null ? null : "Dr. Anil Rao",
            ReferenceModule = "test",
            ReferenceNumber = reference,
            LedgerEntryId = Guid.NewGuid(),
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Polls until the outbox has delivered (it runs every two seconds), for at most a minute.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, string failure)
        where T : class
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(1); waited += TimeSpan.FromSeconds(1))
        {
            if (await read() is { } value)
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        throw new ShouldAssertException(failure);
    }
}
