using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Tally;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The Tally day book's safety rails against the real database (INT-TALLY): concurrent saves of a day conflict instead of
/// overwriting each other, "mark imported" waits for a post in progress, a day left Queued is queued again, and posting
/// a day from before per-voucher tracking posts the days of the bills cancelled on it again; and a pharmacy's goods
/// receipt reaches the day book as a purchase from its supplier that the simulator keeps and reads back. Each test works
/// on dates in 2024 of its own, so no other test's activity reaches them.
/// </summary>
public sealed class TallyPostingTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_saves_of_a_day_from_the_same_reading_conflict_instead_of_overwriting()
    {
        var facilityId = await FacilityAsync();
        var day = await AddDayAsync(facilityId, new DateOnly(2024, 2, 1), TallyExportStatus.Failed);

        await using var first = server.Services.CreateAsyncScope();
        await using var second = server.Services.CreateAsyncScope();
        var a = await Db(first).Set<TallyExport>().SingleAsync(e => e.Id == day, Ct);
        var b = await Db(second).Set<TallyExport>().SingleAsync(e => e.Id == day, Ct);
        a.Message = "Saved by a post";
        await Db(first).SaveChangesAsync(Ct);
        b.Message = "Saved by a check";

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => Db(second).SaveChangesAsync(Ct));
        BillingDb.IsConflict(new DbUpdateConcurrencyException()).ShouldBeTrue();
    }

    [Fact]
    public async Task A_day_being_posted_cannot_be_marked_imported()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = await FacilityAsync(admin);
        await AddDayAsync(facilityId, new DateOnly(2024, 2, 2), TallyExportStatus.Queued, fileFingerprint: new string('0', 64));

        using var refused = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/billing/tally/days/2024-02-02/imported");

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.tally_posting");
    }

    [Fact]
    public async Task A_day_left_queued_is_queued_again_by_the_hourly_job()
    {
        var facilityId = await FacilityAsync();
        var queuedAt = DateTimeOffset.UtcNow - TallyDayBookScheduler.StuckAfter - TimeSpan.FromMinutes(5);
        var day = await AddDayAsync(facilityId, new DateOnly(2024, 2, 3), TallyExportStatus.Queued, queuedAt: queuedAt);
        var scheduler = server.Services.GetServices<IHostedService>().OfType<TallyDayBookScheduler>().Single();

        (await scheduler.RequeueStuckAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);

        await using var scope = server.Services.CreateAsyncScope();
        var export = await Db(scope).Set<TallyExport>().AsNoTracking().SingleAsync(e => e.Id == day, Ct);
        export.QueuedAt!.Value.ShouldBeGreaterThan(queuedAt + TallyDayBookScheduler.StuckAfter);
        export.RunId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Posting_a_day_from_before_tracking_posts_the_days_of_the_bills_cancelled_on_it_again()
    {
        var facilityId = await FacilityAsync();
        var billed = new DateOnly(2024, 2, 10);
        var cancelled = new DateOnly(2024, 2, 12);
        var number = await AddCancelledBillAsync(facilityId, billed, cancelled);
        var started = DateTimeOffset.UtcNow;
        await AddDayAsync(facilityId, billed, TallyExportStatus.Posted, legacy: true, queuedAt: started.AddDays(-30));
        var cancellationDay = await AddDayAsync(facilityId, cancelled, TallyExportStatus.Posted, legacy: true, queuedAt: started.AddDays(-30));
        var runId = Guid.CreateVersion7();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var export = await Db(scope).Set<TallyExport>().SingleAsync(e => e.Id == cancellationDay, Ct);
            export.StartRun(runId, DateTimeOffset.UtcNow);
            await Db(scope).SaveChangesAsync(Ct);
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<TallyService>().PostAsync(new TallyPostMessage(facilityId, cancelled, runId), Ct);
            }
            catch (InvalidOperationException)
            {
                // Whether Tally is set up here does not matter: the bill's day is queued before anything is sent.
            }
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = Db(scope);
            var billDay = await db.Set<TallyExport>().AsNoTracking().SingleAsync(e => e.FacilityId == facilityId && e.Date == billed, Ct);
            billDay.QueuedAt!.Value.ShouldBeGreaterThanOrEqualTo(started);
            var messages = await db.Set<OutboxMessage>().AsNoTracking()
                .Where(m => m.Type == TallyDayBookHandler.MessageType && m.FacilityId == facilityId && m.CreatedAt >= started)
                .ToListAsync(Ct);
            messages.ShouldContain(m => m.Read<TallyPostMessage>().Date == billed);
            var cancellation = await db.Set<TallyExport>().AsNoTracking().SingleAsync(e => e.Id == cancellationDay, Ct);
            cancellation.Message!.ShouldContain("10-Feb-2024");
            var records = await db.Set<TallyVoucherRecord>().AsNoTracking().Where(r => r.ExportId == cancellationDay).Select(r => r.Key).ToListAsync(Ct);
            records.ShouldContain(TallyVoucherBuilder.CancellationNumber(number));
        }
    }

    /// <summary>
    /// N11 and T14: a goods receipt into the pharmacy, entered and approved through the API, is in its day's day book as a
    /// purchase from its supplier (read through Supply chain's source), crediting the supplier's ledger against a new bill
    /// of the supplier's invoice, with the supplier's GST details. The simulator takes it as Tally would, party details and
    /// allocations included, and reads it back as sent. No setting changes, so other tests' posts are not disturbed.
    /// </summary>
    [Fact]
    public async Task A_pharmacy_goods_receipt_is_a_purchase_from_its_supplier_that_the_simulator_keeps_and_reads_back()
    {
        var store = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = await FacilityAsync(store);
        var received = new DateOnly(2024, 2, 20);
        var pharmacy = (await store.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "OPPH").Id;
        var vendor = (await store.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var item = (await store.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: $"Cetirizine 10 mg Tablet {Letters(6)}", GenericName: "Cetirizine", BrandName: null, Type: ItemType.Drug,
            CategoryId: null, Strength: "10 mg", DosageForm: null, PackSize: null, BaseUnit: "TAB", PurchaseUnit: "TAB", PurchaseUnitFactor: 1, HsnCode: "300490",
            GstRate: 12m, Schedule: DrugSchedule.Otc, IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential, ReorderLevel: 0, ReorderQuantity: 0,
            MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null))).GetProperty("id").GetGuid();
        var invoice = $"SB/{Letters(8)}".ToUpperInvariant();
        var grn = await GoodsReceipts.IntoStockAsync(server, store, new CreateGoodsReceiptRequest(
            vendor.Id, pharmacy, null, invoice, received.AddDays(-2), received, null,
            [new GoodsReceiptLineInput(item, null, $"B{Letters(8)}", null, received.AddYears(2), 25m, 0m, 18.30m, 30m, 0m, null, AcceptShortExpiry: false)],
            SendForApproval: true));

        await using var scope = server.Services.CreateAsyncScope();
        var vouchers = await scope.ServiceProvider.GetRequiredService<TallyService>().BuildVouchersAsync(facilityId, received, Ct);

        var purchase = vouchers.Single(v => v.Number == grn.Number);
        purchase.ShouldSatisfyAllConditions(
            v => v.VoucherType.ShouldBe("Purchase"),
            v => v.Reference.ShouldBe(grn.Number),
            v => v.SupplierInvoiceNumber.ShouldBe(invoice),
            v => v.SupplierInvoiceDate.ShouldBe(received.AddDays(-2)),
            v => v.Party.ShouldNotBeNull().Gstin.ShouldBe("33AABCS1234F1Z5"),
            v => v.Party!.StateName.ShouldBe("Tamil Nadu"),
            v => v.Narration.ShouldContain(invoice));
        purchase.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(purchase.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount));
        var supplier = purchase.Entries.Single(e => !e.IsDebit && e.BillAllocations.Count > 0);
        supplier.ShouldSatisfyAllConditions(
            e => e.Amount.ShouldBe(grn.NetAmount),
            e => e.BillAllocations.ShouldBe([new AccountingBillAllocation(invoice, AccountingBillType.NewRef, grn.NetAmount)]));
        purchase.Entries.ShouldContain(e => e.IsDebit && e.LedgerName == "Purchase Accounts @ 12%");

        // CGST and SGST at 6% each, or IGST at 12% while another test has the hospital registered in another state.
        var inputGst = purchase.Entries.Where(e => e.LedgerName.StartsWith("Input ", StringComparison.Ordinal)).ToList();
        inputGst.ShouldNotBeEmpty();
        inputGst.ShouldAllBe(e => e.IsDebit && (e.LedgerName.EndsWith(" @ 6%", StringComparison.Ordinal) || e.LedgerName.EndsWith(" @ 12%", StringComparison.Ordinal)));

        // The simulator keeps the voucher as Tally would, and reads it back as sent.
        TallyXml.Validate(purchase).ShouldBeNull();
        var options = new TallyImportOptions($"IT {Guid.NewGuid():N}", "Primary Cost Category", null);
        var company = scope.ServiceProvider.GetRequiredService<SimulatedTallyCompany>();
        (await company.ImportAsync(options.CompanyName!, [(purchase, TallyXml.Voucher(purchase, options))], facilityId, Ct)).ShouldBe([AccountingVoucherOutcome.Created]);
        var held = TallyVoucherReader.Read(await company.ExportDayAsync(options.CompanyName!, received, Ct));
        held.Error.ShouldBeNull();
        TallyReadBack.Compare(received, [purchase], held.Vouchers, options).Vouchers.ShouldHaveSingleItem().Match.ShouldBe(AccountingVoucherMatch.Matched);
    }

    /// <summary>
    /// T17 and N12: a counter sale is a voucher of its own under the pharmacy's voucher type, numbered and referenced by its
    /// invoice and naming the walk-in customer (read through the pharmacy's invoice source), and the day has no summary
    /// voucher; the simulator keeps it and reads it back as sent.
    /// </summary>
    [Fact]
    public async Task A_counter_sale_is_a_pharmacy_voucher_of_its_own_that_names_the_walk_in_customer()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var facilityId = await FacilityAsync();
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var customer = $"Kumar {Letters(6)}";
        var sold = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", new CounterSaleRequest(
            medicine.StoreId, null, customer, null, null, [new CounterSaleLine(medicine.ItemId, 2m, medicine.Batch.BatchId)], 0m,
            [new SalePayment(PharmacyPaymentMode.Upi, Math.Round(medicine.Batch.Mrp * 2, MidpointRounding.AwayFromZero), $"UPI{Random.Shared.NextInt64(100_000_000_000, 999_999_999_999)}")],
            false));
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(330)).DateTime);

        await using var scope = server.Services.CreateAsyncScope();
        var vouchers = await scope.ServiceProvider.GetRequiredService<TallyService>().BuildVouchersAsync(facilityId, today, Ct);

        var invoice = vouchers.Single(v => v.Number == sold.InvoiceNumber);
        invoice.ShouldSatisfyAllConditions(
            v => v.VoucherType.ShouldBe("Pharmacy Sales"),
            v => v.Reference.ShouldBe(sold.InvoiceNumber),
            v => v.Narration.ShouldBe($"{customer}: Pharmacy invoice {sold.InvoiceNumber}"),
            v => v.Amount.ShouldBe(sold.NetAmount));
        invoice.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(invoice.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount));
        invoice.Entries.ShouldContain(e => !e.IsDebit && e.LedgerName == "Pharmacy Sales @ 5%");
        vouchers.ShouldNotContain(v => v.Number.StartsWith("PH-", StringComparison.Ordinal));

        TallyXml.Validate(invoice).ShouldBeNull();
        var options = new TallyImportOptions($"IT {Guid.NewGuid():N}", "Primary Cost Category", null);
        var company = scope.ServiceProvider.GetRequiredService<SimulatedTallyCompany>();
        (await company.ImportAsync(options.CompanyName!, [(invoice, TallyXml.Voucher(invoice, options))], facilityId, Ct)).ShouldBe([AccountingVoucherOutcome.Created]);
        var held = TallyVoucherReader.Read(await company.ExportDayAsync(options.CompanyName!, today, Ct));
        TallyReadBack.Compare(today, [invoice], held.Vouchers, options).Vouchers.ShouldHaveSingleItem().Match.ShouldBe(AccountingVoucherMatch.Matched);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static HospitalDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

    private async Task<Guid> FacilityAsync(ApiClient? admin = null)
    {
        admin ??= await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        return (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("The administrator has no facility.");
    }

    /// <summary>The day's posting record as the test needs it, replacing one left by an earlier run of the test.</summary>
    private async Task<Guid> AddDayAsync(
        Guid facilityId, DateOnly date, TallyExportStatus status, bool legacy = false, DateTimeOffset? queuedAt = null, string? fileFingerprint = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = Db(scope);
        await db.Set<TallyExport>().Where(e => e.FacilityId == facilityId && e.Date == date).ExecuteDeleteAsync(Ct);
        var export = new TallyExport
        {
            FacilityId = facilityId,
            Date = date,
            Status = status,
            CreatedAt = queuedAt ?? DateTimeOffset.UtcNow,
            QueuedAt = queuedAt ?? DateTimeOffset.UtcNow,
            RunId = Guid.CreateVersion7(),
            LegacyReferences = legacy,
            FileFingerprint = fileFingerprint,
            FileDownloadedAt = fileFingerprint is null ? null : DateTimeOffset.UtcNow,
        };
        db.Add(export);
        await db.SaveChangesAsync(Ct);
        return export.Id;
    }

    /// <summary>An OP bill finalised at noon on one day and cancelled at noon on a later one; returns its number.</summary>
    private async Task<string> AddCancelledBillAsync(Guid facilityId, DateOnly billed, DateOnly cancelled)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = Db(scope);
        var patientId = await db.Set<Patient>().IgnoreQueryFilters().Select(p => p.Id).FirstAsync(Ct);
        var number = "ITOB" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.Add(new Bill
        {
            FacilityId = facilityId,
            BillNumber = number,
            Type = BillType.OutPatient,
            Status = BillStatus.Cancelled,
            PatientId = patientId,
            GrossAmount = 500m,
            NetAmount = 500m,
            PatientAmount = 500m,
            FinalizedAt = BillingTime.StartOfDay(billed).AddHours(12),
            CancelledAt = BillingTime.StartOfDay(cancelled).AddHours(12),
        });
        await db.SaveChangesAsync(Ct);
        return number;
    }
}
