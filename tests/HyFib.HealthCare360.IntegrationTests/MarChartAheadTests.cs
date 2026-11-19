using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A course the IP pharmacy issues at once for several days stays on the ward's medication rounds for every day of it:
/// the issue charts its first day, and the job that keeps the chart a day ahead (<see cref="MarChartAheadJob"/>) the rest,
/// round by round to the course's end and never past it. A dose it charts is on the ward's due list, falls overdue and is
/// recorded as missed as any other. The days pass on a server of their own whose clock the test turns
/// (<see cref="MidnightInIndiaServer"/>); the job runs as it does every quarter of an hour.
/// </summary>
[Collection(MidnightInIndiaCollection.Name)]
[OnTestClock]
public sealed class MarChartAheadTests(MidnightInIndiaServer midnight)
{
    private static readonly TimeOnly SignedAt = new(9, 50);
    private static readonly TimeOnly JobRuns = new(10, 0);

    private HospitalServer Server => midnight.Server;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_five_day_course_issued_at_once_stays_on_the_rounds_to_its_end()
    {
        // An evening and the seven days after it, none of them New Year's Eve, which MidnightInIndiaTests keeps for the year's
        // turn; the course starts the morning after the evening taken.
        var evening = midnight.NextEvening(day => Enumerable.Range(0, 8).All(i => day.AddDays(i) is not { Month: 12, Day: 31 }));
        var first = evening.AddDays(1);
        midnight.At(first, SignedAt);
        var frontOffice = await Server.SignInAsync(Users.FrontOffice);
        var doctor = await Server.SignInAsync(Users.Doctor);
        var storeKeeper = await Server.SignInAsync(Users.HospitalAdmin);
        var pharmacist = await Server.SignInAsync(Users.Pharmacist);
        var suffix = Letters(6);

        // Ten vials of ceftriaxone in the IP pharmacy; a patient admitted to the male general ward.
        var store = (await storeKeeper.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "IPPH").Id;
        var ceftriaxone = await MedicineAsync(storeKeeper, $"Ceftriaxone 1 g Injection {suffix}");
        await ReceiveAsync(storeKeeper, store, ceftriaxone, 10m);
        var patient = (await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice)).Id;
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var admission = await AdmitAsync(frontOffice, doctor, patient, ward.Id);

        // The doctor prescribes Inj. Ceftriaxone 1 g IV BD for 5 days at 09:50; the pharmacist issues all ten vials against it.
        var draft = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patient, EncounterId: null, VisitId: null, AdmissionId: admission.Id, IsInpatient: true, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    ceftriaxone, $"Ceftriaxone 1 g Injection {suffix}", "Ceftriaxone", "1 g", "Injection", Route: "IV", Dose: "1 g", DoseFrequency.BD,
                    CustomFrequency: null, DosesPerDay: null, 5, Quantity: 10, Instructions: null, IsControlledDrug: false, ControlledSchedule: null),
            ]));
        var signed = (await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version))).Prescription;
        var waiting = (await pharmacist.GetAsync<PagedResult<QueueListItem>>($"/api/v1/pharmacy/queue?search={signed.PrescriptionNumber}")).Items.ShouldHaveSingleItem();
        var line = (await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{waiting.Id}?storeId={store}")).Lines.ShouldHaveSingleItem();
        await pharmacist.PostAsync<IpIssueCreated>("/api/v1/pharmacy/ip-issues", new IpIssueRequest(
            store, null, waiting.Id, null, null, null, [new IpIssueLineInput(ceftriaxone, 10m, null, null, line.Id, null, false)], ConfirmControlledDrugs: false));

        // The issue charts its first day: 21:00 and 09:00 the next morning.
        var order = await OrderAsync(signed.Id);
        (await DosesAsync(order.Id)).Select(d => d.ScheduledAt).ShouldBe([At(first, 21), At(first.AddDays(1), 9)]);

        // The next morning the job charts the day ahead.
        midnight.At(first.AddDays(1), JobRuns);
        await RunJobAsync();
        (await DosesAsync(order.Id)).Select(d => d.ScheduledAt).ShouldBe([At(first, 21), At(first.AddDays(1), 9), At(first.AddDays(1), 21), At(first.AddDays(2), 9)]);

        // A dose the job charted is on the ward's due list and overdue at 09:40; the nurse records it as missed.
        midnight.At(first.AddDays(2), new TimeOnly(9, 40));
        Server.ForgetSignIns();
        var nurse = await Server.SignInAsync(Users.Nurse);
        var due = (await nurse.GetAsync<List<WardMarItem>>($"/api/v1/nursing/wards/{ward.Id}/mar/due?withinMinutes=60"))
            .Single(i => i.AdmissionId == admission.Id && i.ScheduledAt == At(first.AddDays(2), 9));
        due.IsOverdue.ShouldBeTrue();
        due.MinutesLate.ShouldBeGreaterThanOrEqualTo(40);
        var missed = await nurse.PostAsync<MarEntryDto>($"/api/v1/nursing/mar/{due.EntryId}/record",
            new RecordAdministrationRequest(MarStatus.Missed, null, "Patient was in the CT scanner", null, null, null, due.Version));
        missed.Status.ShouldBe(MarStatus.Missed);

        // Each morning after, to the day after the course ends: every round of it, ten doses for the ten vials, the last at 09:00
        // on the sixth day, before the course ends at 09:50. Nothing after the end.
        for (var day = 2; day <= 6; day++)
        {
            midnight.At(first.AddDays(day), JobRuns);
            await RunJobAsync();
        }

        var doses = await DosesAsync(order.Id);
        doses.Select(d => d.ScheduledAt).ShouldBe(
        [
            At(first, 21),
            At(first.AddDays(1), 9), At(first.AddDays(1), 21),
            At(first.AddDays(2), 9), At(first.AddDays(2), 21),
            At(first.AddDays(3), 9), At(first.AddDays(3), 21),
            At(first.AddDays(4), 9), At(first.AddDays(4), 21),
            At(first.AddDays(5), 9),
        ]);
        doses.ShouldAllBe(d => d.ScheduledAt < order.EndAt!.Value);
        doses.Where(d => d.ScheduledAt != At(first.AddDays(2), 9)).ShouldAllBe(d => d.Status == MarStatus.Due && !d.IsAdHoc);
    }

    // ------------------------------------------------------------------ steps

    /// <summary>The job's run, as it runs every quarter of an hour.</summary>
    private Task<int> RunJobAsync() => Server.Services.GetServices<IHostedService>().OfType<MarChartAheadJob>().Single().RunOnceAsync(Ct);

    private static DateTimeOffset At(DateOnly day, int hour) => new(day.ToDateTime(new TimeOnly(hour, 0)), MidnightInIndiaServer.Ist);

    /// <summary>A medicine of Schedule H (neither controlled nor high-alert), counted by the vial.</summary>
    private static async Task<Guid> MedicineAsync(ApiClient storeKeeper, string name) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: name, GenericName: "Ceftriaxone", BrandName: null, Type: ItemType.Drug, CategoryId: null, Strength: "1 g",
            DosageForm: null, PackSize: null, BaseUnit: "VIAL", PurchaseUnit: "VIAL", PurchaseUnitFactor: 1, HsnCode: "300490", GstRate: 5m, Schedule: DrugSchedule.H,
            IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential, ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0,
            Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null)))
        .GetProperty("id").GetGuid();

    /// <summary>The store receives <paramref name="units"/> at ₹60 each, dated the server's today; someone else posts it.</summary>
    private async Task ReceiveAsync(ApiClient storeKeeper, Guid storeId, Guid itemId, decimal units)
    {
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = Server.Today;
        await GoodsReceipts.IntoStockAsync(Server, storeKeeper, new CreateGoodsReceiptRequest(
            vendor.Id, storeId, null, $"INV-{Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(itemId, null, $"B{Letters(8)}", null, today.AddYears(2), units, 0m, 30m, 60m, 0m, null, AcceptShortExpiry: false)],
            SendForApproval: true));
    }

    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId, Guid wardId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(Server, wardId);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Community-acquired pneumonia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>The chart line the signed prescription became.</summary>
    private async Task<MedicationOrder> OrderAsync(Guid prescriptionId)
    {
        await using var scope = Server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<MedicationOrder>().AsNoTracking()
            .SingleAsync(o => o.PrescriptionId == prescriptionId, Ct);
    }

    /// <summary>The order's doses on the MAR, by the time they are due.</summary>
    private async Task<List<MedicationAdministration>> DosesAsync(Guid orderId)
    {
        await using var scope = Server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<MedicationAdministration>().AsNoTracking()
            .Where(d => d.MedicationOrderId == orderId)
            .OrderBy(d => d.ScheduledAt)
            .ToListAsync(Ct);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
