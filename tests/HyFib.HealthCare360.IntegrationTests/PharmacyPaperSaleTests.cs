using System.Net;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Downtime;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Ndps;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Registers;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Pharmacy sales written on paper during a downtime, over the real API: the medical records officer issues a block of
/// pharmacy sale numbers to the counter; the pharmacist enters each paper sale afterwards under the number on it, with the
/// time on it, once; the stock and the invoice are right; and a prescription dispensed on paper is not offered in the
/// dispensing queue again.
/// </summary>
public sealed class PharmacyPaperSaleTests(HospitalServer server)
{
    private const string Blocks = "/api/v1/downtime/number-blocks";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_paper_sale_keeps_its_downtime_number_and_time_and_takes_stock_once()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var block = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.PharmacySale, "OP pharmacy counter 1", 3));
        block.Block.KindName.ShouldBe("pharmacy sale");
        var onShelf = await AvailableAsync(pharmacist, medicine);

        // A sale made now does not take a number of the block.
        var live = await SellAsync(pharmacist, medicine, 1m, null);
        block.Numbers.ShouldNotContain(n => n.Number == live.InvoiceNumber);

        // A number of another kind, one HC360 issued normally, and a time in the future are refused.
        var bills = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.OpBill, "OP billing counter 1", 1));
        var paperAt = block.Block.IssuedAt.AddSeconds(30);
        await RefusedAsync(pharmacist, medicine, new PaperNumber(bills.Numbers[0].Number, paperAt), HttpStatusCode.BadRequest, "is not a downtime pharmacy sale number");
        await RefusedAsync(pharmacist, medicine, new PaperNumber(live.InvoiceNumber, paperAt), HttpStatusCode.BadRequest, "is not a downtime pharmacy sale number");
        await RefusedAsync(pharmacist, medicine, new PaperNumber(block.Numbers[0].Number, DateTimeOffset.UtcNow.AddHours(3)), HttpStatusCode.BadRequest, "cannot be in the future");
        (await AvailableAsync(pharmacist, medicine)).ShouldBe(onShelf - 1m, "a refused entry takes nothing off the shelf");

        // The paper sale is entered under its number, at the time on the paper.
        var sold = await SellAsync(pharmacist, medicine, 2m, new PaperNumber(block.Numbers[0].Number.ToLowerInvariant(), paperAt));
        sold.InvoiceNumber.ShouldBe(block.Numbers[0].Number);
        var detail = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{sold.Id}");
        detail.SoldAt.ShouldBe(paperAt, TimeSpan.FromSeconds(1));
        detail.EnteredFromPaperAt.ShouldNotBeNull();
        detail.Status.ShouldBe(SaleStatus.Paid);
        detail.Items.Single().Quantity.ShouldBe(2m);
        (await AvailableAsync(pharmacist, medicine)).ShouldBe(onShelf - 3m);

        // The number is used once, and the block shows it entered, with the time on the paper.
        await RefusedAsync(pharmacist, medicine, new PaperNumber(block.Numbers[0].Number, paperAt.AddSeconds(5)), HttpStatusCode.Conflict, "already been entered");
        var entered = (await mrd.GetAsync<DowntimeNumberBlockDetail>($"{Blocks}/{block.Block.Id}")).Numbers[0];
        (entered.Status, entered.EntityType, entered.EntityId).ShouldBe((DowntimeUhidStatus.Entered, nameof(PharmacySale), sold.Id));
        entered.PaperAt!.Value.ShouldBe(paperAt, TimeSpan.FromSeconds(1));

        // The entry is audited as made from paper, against the sale.
        await using var scope = server.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.Action == "patients.entered_from_paper" && a.EntityId == sold.Id.ToString(), Ct);
        audit.EntityType.ShouldBe(nameof(PharmacySale));
    }

    [Fact]
    public async Task A_paper_sale_is_written_in_the_controlled_drug_register_when_it_is_entered_and_the_balances_stay_true()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var store = await OwnStoreAsync();

        // 20 ampoules on the shelf. During the downtime two are sold on paper, one number from the block.
        var morphine = await OwnMedicine.OnTheShelfAsync(
            server, pharmacist, tablets: 20m, storeId: store, schedule: DrugSchedule.X, isControlled: true, genericName: "Morphine sulphate");
        var block = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.PharmacySale, "Night pharmacy", 1));
        // The paper's time is just after the block was issued, before the goods receipt below: the whole test can run within
        // a second, so a paper time a second on would come after the sale is entered.
        var paperAt = block.Block.IssuedAt.AddMilliseconds(1);

        // A goods receipt of 50 is made after the paper's time and before the paper is entered: the register's balance is 70.
        await OwnMedicine.ReceiveAsync(server, storeKeeper, store, morphine.ItemId, 50m);

        // The paper sale is entered after it.
        await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", new CounterSaleRequest(
            store, null, "Walk-in customer", null, new ExternalPrescription("Dr. Paper", "TN 12345", null, "RX-PAPER-1"),
            [new CounterSaleLine(morphine.ItemId, 2m, morphine.Batch.BatchId)], 0m,
            [new SalePayment(PaymentMode.Cash, Math.Round(morphine.Batch.Mrp * 2m, MidpointRounding.AwayFromZero), null)], true,
            Paper: new PaperNumber(block.Numbers[0].Number, paperAt)));

        // The register is in the order the movements were made, so each balance follows the one above it: 20, 70, 68.
        var lines = (await pharmacist.GetAsync<PagedResult<ControlledRegisterRow>>(
            $"/api/v1/pharmacy/registers/controlled?storeId={store}&itemId={morphine.ItemId}&pageSize=50")).Items;
        lines.Select(l => l.BalanceAfter).ShouldBe([20m, 70m, 68m]);
        var sale = lines[2];
        sale.OccurredAt.ShouldBeGreaterThan(lines[1].OccurredAt, "the line is dated when it was written, not at the time on the paper");
        sale.OccurredAt.ShouldBeGreaterThan(paperAt);
        sale.Remarks.ShouldNotBeNull().ShouldStartWith("Entered from the paper sale of ");
        sale.Remarks.ShouldContain("IST, invoice " + block.Numbers[0].Number);

        // The day's account (Form 3H) balances: 70 received, 2 dispensed, closing 68 = the register's last balance.
        var today = HospitalClock.ToLocalDate(DateTimeOffset.UtcNow);
        var drug = (await pharmacist.GetAsync<NdpsDailyAccount>($"/api/v1/pharmacy/ndps/daily?storeId={store}&date={today:yyyy-MM-dd}")).Drugs.ShouldHaveSingleItem();
        (drug.Opening, drug.Received, drug.Dispensed, drug.Closing, drug.Balanced).ShouldBe((0m, 70m, 2m, 68m, true));
        drug.Movements.Single(m => m.Type == StockMovementType.Sale).Remarks.ShouldNotBeNull().ShouldContain("paper sale");

        // A count after the entry is compared with 68.
        var count = await nurse.PostAsync<NarcoticCountDto>("/api/v1/pharmacy/ndps/counts", new RecordNarcoticCountRequest(store, [new CountedDrug(morphine.ItemId, 68m)], null));
        (count.HasDifference, count.Lines.Single().RegisterBalance).ShouldBe((false, 68m));
    }

    /// <summary>A pharmacy store of the test's own, so no other test's stock moves its register.</summary>
    private async Task<Guid> OwnStoreAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facility = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("No facility.");
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var code = "PAPR" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var store = new Store { FacilityId = facility, Code = code, Name = $"Night pharmacy {code}", Type = StoreType.Pharmacy };
        db.Add(store);
        await db.SaveChangesAsync(Ct);
        return store.Id;
    }

    [Fact]
    public async Task A_prescription_dispensed_on_paper_does_not_come_back_in_the_dispensing_queue()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var patient = await RegisterAsync(frontOffice);
        var block = await mrd.PostAsync<DowntimeNumberBlockDetail>(Blocks, new IssueDowntimeNumbersRequest(DowntimeNumberKind.PharmacySale, "OP pharmacy counter 2", 2));
        var counted = await QueuedAsync(pharmacist, patient.Id, medicine);
        var sold = await QueuedAsync(pharmacist, patient.Id, medicine);
        var elsewhere = await QueuedAsync(pharmacist, patient.Id, medicine);

        // All three prescriptions are waiting in the queue, as they are when the outage ends.
        foreach (var rx in new[] { counted, sold, elsewhere })
        {
            (await InQueueAsync(pharmacist, rx.PrescriptionNumber)).ShouldBeTrue();
        }

        // Back-entering the paper sale against the prescription's number closes the prescription.
        var paperAt = block.Block.IssuedAt.AddSeconds(30);
        var entered = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", new CounterSaleRequest(
            medicine.StoreId, patient.Id, null, null, new ExternalPrescription("Dr. Paper", null, null, sold.PrescriptionNumber),
            [new CounterSaleLine(medicine.ItemId, 1m, medicine.Batch.BatchId)], 0m,
            [new SalePayment(PaymentMode.Cash, Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero), null)], false,
            Paper: new PaperNumber(block.Numbers[0].Number, paperAt)));
        (await InQueueAsync(pharmacist, sold.PrescriptionNumber)).ShouldBeFalse("the paper sale dispensed it");
        var closed = await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{sold.Id}");
        (closed.Status, closed.PaperSaleNumber).ShouldBe((QueueEntryStatus.Dispensed, entered.InvoiceNumber));
        closed.DispensedOnPaperAt!.Value.ShouldBe(paperAt, TimeSpan.FromSeconds(1));

        // An explicit "dispensed on paper" closes another, with the sale number or a note of where the paper is, once.
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        await RefusedAsync(pharmacist, $"/api/v1/pharmacy/queue/{counted.Id}/dispensed-on-paper", new DispensedOnPaperRequest(at, "NOTASALE1", null),
            HttpStatusCode.BadRequest, "is not a pharmacy sale entered from paper");
        await RefusedAsync(pharmacist, $"/api/v1/pharmacy/queue/{counted.Id}/dispensed-on-paper", new DispensedOnPaperRequest(DateTimeOffset.UtcNow.AddHours(2), null, "Sale book 3"),
            HttpStatusCode.BadRequest, "cannot be in the future");
        (await InQueueAsync(pharmacist, counted.PrescriptionNumber)).ShouldBeTrue("a refused action closes nothing");
        using (var closedOnPaper = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/queue/{counted.Id}/dispensed-on-paper",
                   new DispensedOnPaperRequest(at, entered.InvoiceNumber, null)))
        {
            closedOnPaper.StatusCode.ShouldBe(HttpStatusCode.NoContent, await closedOnPaper.Content.ReadAsStringAsync(Ct));
        }

        (await InQueueAsync(pharmacist, counted.PrescriptionNumber)).ShouldBeFalse();
        await RefusedAsync(pharmacist, $"/api/v1/pharmacy/queue/{counted.Id}/dispensed-on-paper", new DispensedOnPaperRequest(at, null, "again"),
            HttpStatusCode.UnprocessableEntity, "Dispensed");
        using (var noted = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/queue/{elsewhere.Id}/dispensed-on-paper",
                   new DispensedOnPaperRequest(at, null, "Sale book 3, page 12")))
        {
            noted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await noted.Content.ReadAsStringAsync(Ct));
        }

        (await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{elsewhere.Id}")).CloseReason.ShouldNotBeNull().ShouldContain("Sale book 3, page 12");

        // Dispensing a closed prescription is refused: it cannot be dispensed twice.
        await RefusedAsync(pharmacist, $"/api/v1/pharmacy/queue/{sold.Id}/dispense", new DispenseRequest(
            medicine.StoreId, [new DispenseLineRequest(closed.Lines[0].Id, medicine.ItemId, 1m, medicine.Batch.BatchId, null, false)], null, 0m,
            [new SalePayment(PaymentMode.Cash, Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero), null)], false),
            HttpStatusCode.UnprocessableEntity, "Dispensed");
    }

    private static Task<PharmacySaleCreated> SellAsync(ApiClient pharmacist, OwnMedicine medicine, decimal strips, PaperNumber? paper) =>
        pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", CounterSale(medicine, strips, paper));

    private static CounterSaleRequest CounterSale(OwnMedicine medicine, decimal strips, PaperNumber? paper) => new(
        medicine.StoreId, null, "Walk-in customer", null, null, [new CounterSaleLine(medicine.ItemId, strips, medicine.Batch.BatchId)], 0m,
        [new SalePayment(PaymentMode.Cash, Math.Round(medicine.Batch.Mrp * strips, MidpointRounding.AwayFromZero), null)], false, Paper: paper);

    private static Task RefusedAsync(ApiClient pharmacist, OwnMedicine medicine, PaperNumber paper, HttpStatusCode status, string reason) =>
        RefusedAsync(pharmacist, "/api/v1/pharmacy/sales/counter", CounterSale(medicine, 1m, paper), status, reason);

    private static async Task RefusedAsync(ApiClient client, string path, object body, HttpStatusCode status, string reason)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        text.ShouldContain(reason);
    }

    private static async Task<decimal> AvailableAsync(ApiClient pharmacist, OwnMedicine medicine) =>
        (await pharmacist.GetAsync<List<BatchStock>>($"/api/v1/pharmacy/stock/batches?storeId={medicine.StoreId}&itemId={medicine.ItemId}"))
            .Single(b => b.BatchId == medicine.Batch.BatchId).Available;

    private static async Task<bool> InQueueAsync(ApiClient pharmacist, string prescriptionNumber) =>
        (await pharmacist.GetAsync<PagedResult<QueueListItem>>($"/api/v1/pharmacy/queue?search={prescriptionNumber}")).Items.Any();

    /// <summary>An e-prescription for the patient waiting in the OP queue, as the prescriber's signing leaves it.</summary>
    private async Task<PharmacyQueueEntry> QueuedAsync(ApiClient pharmacist, Guid patientId, OwnMedicine medicine)
    {
        var facilityId = (await pharmacist.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("The pharmacist has no facility.");
        var number = "NHRX" + string.Concat(Enumerable.Range(0, 10).Select(_ => (char)('0' + Random.Shared.Next(10))));
        var entry = PharmacyQueueEntry.FromPrescription(new PrescriptionSigned(
            Guid.CreateVersion7(), number, patientId, null, null, facilityId, Guid.CreateVersion7(), false,
            [new PrescriptionLine(Guid.CreateVersion7(), medicine.ItemId, "Paracetamol 500 mg", "Paracetamol", "500 mg", "Tablet", "Oral", "1 tablet", "TDS", 3, 1m, null, false)],
            DateTimeOffset.UtcNow.AddHours(-10)), DateTimeOffset.UtcNow);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(entry);
        await db.SaveChangesAsync(Ct);
        return entry;
    }

    private static async Task<PatientCreated> RegisterAsync(ApiClient frontOffice) =>
        await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: TestPatients.FirstName("Paper"), MiddleName: null, LastName: "Sale", Gender: Gender.Female,
            DateOfBirth: null, AgeYears: 20 + Random.Shared.Next(60), AgeMonths: null, Phone: null, AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
            PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false,
            Notes: null, ConfirmedNotDuplicate: true));
}
