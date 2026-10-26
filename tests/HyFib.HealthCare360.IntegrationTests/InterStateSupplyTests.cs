using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// GST on supplies to another state, over the real API. The hospital is registered in Tamil Nadu (33). A company
/// registered in Kerala (32) is billed IGST, with Kerala as the place of supply (IGST Act s.12(2)(a)); a company without a
/// GSTIN is billed CGST and SGST. The pharmacy's B2B invoice needs the hospital's GSTIN, and medicines dispatched to
/// Kerala carry IGST (s.10).
/// </summary>
public sealed class InterStateSupplyTests(HospitalServer server)
{
    private const string Hospital = "33AABCN1234F1Z3";
    private const string KeralaClinic = "32AAACH7409R1ZA";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_company_registered_in_another_state_is_billed_igst_and_one_without_a_gstin_cgst_and_sgst()
    {
        await using var _ = await HospitalGstinAsync(Hospital);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Executive lounge access", null, ServiceCategory.Miscellaneous, null, 1000m, 18m, "999799", true, false, null, true, null));

        var kerala = await BillAsync(await CompanyAsync(admin, KeralaClinic), service);
        kerala.PlaceOfSupply.ShouldBe("32");
        kerala.IgstAmount.ShouldBeGreaterThan(0m);
        (kerala.CgstAmount, kerala.SgstAmount).ShouldBe((0m, 0m));
        kerala.Lines.Sum(l => l.IgstAmount).ShouldBe(kerala.IgstAmount);

        var unregistered = await BillAsync(await CompanyAsync(admin, gstin: null), service);
        unregistered.PlaceOfSupply.ShouldBe("33");
        unregistered.IgstAmount.ShouldBe(0m);
        (unregistered.CgstAmount + unregistered.SgstAmount).ShouldBe(kerala.IgstAmount, "The same tax, split between the Centre and the State.");
    }

    [Fact]
    public async Task A_b2b_pharmacy_invoice_needs_the_hospitals_gstin_and_medicines_sent_to_another_state_carry_igst()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var store = (await pharmacist.GetAsync<List<StoreDto>>("/api/v1/inventory/stores?type=Pharmacy")).Single(s => s.Code == "OPPH");
        var medicine = (await pharmacist.GetAsync<List<PharmacyStockRow>>($"/api/v1/pharmacy/stock?storeId={store.Id}&search=paracetamol&take=5"))
            .First(r => r.Available > 0);
        var batch = (await pharmacist.GetAsync<List<BatchStock>>($"/api/v1/pharmacy/stock/batches?storeId={store.Id}&itemId={medicine.ItemId}"))
            .First(b => b.Available > 0 && !b.IsExpired && !b.IsBlocked);
        var price = Math.Round(batch.Mrp, MidpointRounding.AwayFromZero);
        CounterSaleRequest Sale(string? deliveryState) => new(
            store.Id, null, "Sree Clinic", null, null, [new CounterSaleLine(medicine.ItemId, 1m, batch.BatchId)], 0m,
            [new SalePayment(PharmacyPaymentMode.Cash, price, null)], false, new B2bBuyer(KeralaClinic, "Sree Clinic", deliveryState));

        await using (await HospitalGstinAsync(null))
        {
            using var refused = await pharmacist.SendRawAsync(HttpMethod.Post, "/api/v1/pharmacy/sales/counter", Sale("32"));
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await ProblemCodeAsync(refused)).ShouldBe("gst.facility_gstin");
        }

        await using var _ = await HospitalGstinAsync(Hospital);
        var created = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", Sale("32"));
        created.NetAmount.ShouldBe(price, "A B2B buyer pays the MRP too: IGST replaces CGST and SGST inside it.");
        var sale = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{created.Id}");
        (sale.BuyerGstin, sale.BuyerLegalName, sale.PlaceOfSupply).ShouldBe((KeralaClinic, "Sree Clinic", "32"));
        (sale.CgstAmount, sale.SgstAmount).ShouldBe((0m, 0m));
        sale.IgstAmount.ShouldBeGreaterThan(0m, "Paracetamol carries GST.");

        // Handed over at the counter, the goods are supplied in Tamil Nadu whatever the buyer's state (s.10(1)(a)).
        var counter = await pharmacist.GetAsync<PharmacySaleDetail>(
            $"/api/v1/pharmacy/sales/{(await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", Sale(null))).Id}");
        (counter.PlaceOfSupply, counter.IgstAmount).ShouldBe(("33", 0m));
        (counter.CgstAmount + counter.SgstAmount).ShouldBe(sale.IgstAmount);
    }

    /// <summary>A corporate visit with one charge of <paramref name="service"/>, billed and finalized by the billing desk.</summary>
    private async Task<BillDetail> BillAsync(PayerDetail company, ServiceItemDto service)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Arun"), MiddleName: null, LastName: "Supply" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient.Id, VisitType.HealthCheckup, null, null, null, PayerType.Corporate, company.Id, ReferralSource.Corporate, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(
            patient.Id, visit.Id, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        return await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
    }

    private static Task<PayerDetail> CompanyAsync(ApiClient admin, string? gstin) =>
        admin.PostAsync<PayerDetail>("/api/v1/insurance/payers", new SavePayerRequest(
            $"CO{Letters(8).ToUpperInvariant()}", $"Malabar Traders {Letters(6).ToUpperInvariant()} Ltd", PayerCategory.Corporate, "Anil Menon", "9876543210",
            null, null, gstin, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null, IsActive: true, Notes: null, Version: null));

    /// <summary>
    /// Every facility registered under <paramref name="gstin"/> for the rest of the test; then put back, because the
    /// server is shared by every test of the run.
    /// </summary>
    private async Task<IAsyncDisposable> HospitalGstinAsync(string? gstin)
    {
        Dictionary<Guid, string?> before;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var facilities = await db.Set<Facility>().ToListAsync(Ct);
            before = facilities.ToDictionary(f => f.Id, f => f.Gstin);
            facilities.ForEach(f => f.Gstin = gstin);
            await db.SaveChangesAsync(Ct);
        }

        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            foreach (var facility in await db.Set<Facility>().ToListAsync(CancellationToken.None))
            {
                facility.Gstin = before.GetValueOrDefault(facility.Id);
            }

            await db.SaveChangesAsync(CancellationToken.None);
        });
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
