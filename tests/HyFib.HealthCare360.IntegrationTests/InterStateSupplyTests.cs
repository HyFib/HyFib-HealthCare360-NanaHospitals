using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// GST on supplies to buyers in another state, over the real API. The hospital is registered in Tamil Nadu (33). A bill is
/// supplied where the hospital is, whoever pays it: health care where it is performed (IGST Act s.12(4)), a room where
/// the hospital stands (s.12(3)). So a company registered in Kerala (32) is billed CGST and SGST with Tamil Nadu as the
/// place of supply, like a company without a GSTIN, on the bill, its e-invoice and GSTR-1; its GSTIN names it as the
/// buyer. The pharmacy's invoices are not bills: its B2B invoice needs the hospital's GSTIN, and medicines dispatched to
/// Kerala carry IGST (s.10).
/// </summary>
public sealed class InterStateSupplyTests(HospitalServer server)
{
    private const string Hospital = "33AABCN1234F1Z3";
    private const string KeralaClinic = "32AAACH7409R1ZA";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_company_registered_in_another_state_is_billed_cgst_and_sgst_like_one_without_a_gstin()
    {
        await using var _ = await HospitalGstinAsync(Hospital);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Executive lounge access", null, ServiceCategory.Miscellaneous, null, 1000m, 18m, "999799", true, false, null, true, null));

        // Not IGST, as it was: the service is supplied in Tamil Nadu, where it is given, whatever the company's own state.
        var kerala = await BillAsync(await CompanyAsync(admin, KeralaClinic), service);
        (kerala.PlaceOfSupply, kerala.IsInterState, kerala.IgstAmount).ShouldBe(("33", false, 0m));
        (kerala.CgstAmount, kerala.SgstAmount).ShouldBe((90m, 90m), "18% of ₹1,000, split between the Centre and the State");
        kerala.Lines.ShouldAllBe(l => l.IgstAmount == 0m);

        var unregistered = await BillAsync(await CompanyAsync(admin, gstin: null), service);
        (unregistered.PlaceOfSupply, unregistered.IsInterState, unregistered.IgstAmount).ShouldBe(("33", false, 0m));
        (unregistered.CgstAmount, unregistered.SgstAmount).ShouldBe((kerala.CgstAmount, kerala.SgstAmount), "the same tax, whoever the company");
    }

    [Fact]
    public async Task Room_rent_billed_to_a_company_registered_in_kerala_is_cgst_and_sgst_in_tamil_nadu_on_the_bill_its_e_invoice_and_gstr1()
    {
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);

        // A room above ₹5,000 a day outside intensive care is taxed at 5% (a room charged at the desk keeps its service's rate).
        var room = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Deluxe room, a day", null, ServiceCategory.BedCharge, null, 6000m, 5m, "996311", true, false, null, true, null));
        var company = await server.CreatePayerAsync(admin, new SavePayerRequest(
            $"CO{Letters(8).ToUpperInvariant()}", $"Malabar Traders {Letters(6).ToUpperInvariant()} Ltd", PayerCategory.Corporate, "Anil Menon", "9876543210",
            null, "4/12 MG Road, Ernakulam, Kochi, Kerala 682016", EInvoicing.KeralaCompany, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null,
            IsActive: true, Notes: null, Version: null));

        // The bill: Tamil Nadu, CGST and SGST of ₹150 each.
        var bill = await BillAsync(company, room);
        (bill.PlaceOfSupply, bill.IsInterState).ShouldBe(("33", false));
        (bill.CgstAmount, bill.SgstAmount, bill.IgstAmount, bill.TaxAmount).ShouldBe((150m, 150m, 0m, 300m));
        bill.Lines.Single().ShouldSatisfyAllConditions(l => l.TaxRate.ShouldBe(5m), l => l.CgstAmount.ShouldBe(150m), l => l.SgstAmount.ShouldBe(150m));

        // Its e-invoice names the company in Kerala as the buyer and Tamil Nadu as the place of supply, with the same split.
        var invoice = (await JsonAsync(accountant, $"{EInvoicing.Register}/bill/{bill.Id}/json")).AsArray().ShouldHaveSingleItem()!;
        var buyer = invoice["BuyerDtls"]!;
        (buyer["Gstin"]!.GetValue<string>(), buyer["Pos"]!.GetValue<string>(), buyer["Stcd"]!.GetValue<string>()).ShouldBe((EInvoicing.KeralaCompany, "33", "32"));
        var item = invoice["ItemList"]!.AsArray().ShouldHaveSingleItem()!;
        (item["GstRt"]!.GetValue<decimal>(), item["CgstAmt"]!.GetValue<decimal>(), item["SgstAmt"]!.GetValue<decimal>(), item["IgstAmt"]!.GetValue<decimal>())
            .ShouldBe((5m, 150m, 150m, 0m));
        (invoice["ValDtls"]!["CgstVal"]!.GetValue<decimal>(), invoice["ValDtls"]!["IgstVal"]!.GetValue<decimal>()).ShouldBe((150m, 0m));
        (await accountant.GetAsync<EInvoiceDetail>($"{EInvoicing.Register}/bill/{bill.Id}")).Problems.ShouldBeEmpty();

        // GSTR-1: a B2B invoice to the company, within Tamil Nadu, and in the offline tool's JSON the same.
        var month = $"{EInvoicing.Today():yyyy-MM}";
        var b2b = (await accountant.GetAsync<Gstr1Return>($"/api/v1/billing/gst-returns/gstr1?month={month}")).B2b.Single(d => d.Number == bill.BillNumber);
        (b2b.BuyerGstin, b2b.PlaceOfSupply).ShouldBe((EInvoicing.KeralaCompany, "33"));
        b2b.Items.ShouldHaveSingleItem().ShouldBe(new Gstr1Item(5m, new GstReturnAmounts(6000m, 0m, 150m, 150m, 0m)));
        var filed = (await JsonAsync(accountant, $"/api/v1/billing/gst-returns/gstr1/json?month={month}"))["b2b"]!.AsArray()
            .SelectMany(c => c!["inv"]!.AsArray()).Single(i => i!["inum"]!.GetValue<string>() == bill.BillNumber)!;
        filed["pos"]!.GetValue<string>().ShouldBe("33");
        var detail = filed["itms"]![0]!["itm_det"]!.AsObject();
        (detail["camt"]!.GetValue<decimal>(), detail["samt"]!.GetValue<decimal>(), detail.ContainsKey("iamt")).ShouldBe((150m, 150m, false));
    }

    [Fact]
    public async Task A_b2b_pharmacy_invoice_needs_the_hospitals_gstin_and_medicines_sent_to_another_state_carry_igst()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        // Two tablets are sold, of a medicine of the test's own: other tests sell from the seeded shelf at the same time.
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var price = Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero);
        CounterSaleRequest Sale(string? deliveryState) => new(
            medicine.StoreId, null, "Sree Clinic", null, null, [new CounterSaleLine(medicine.ItemId, 1m, medicine.Batch.BatchId)], 0m,
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
        await server.VerifiedPolicyAsync(patient.Id, company.Id);
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient.Id, VisitType.HealthCheckup, null, null, null, PayerType.Corporate, company.Id, ReferralSource.Corporate, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(
            patient.Id, visit.Id, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        return await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
    }

    private Task<PayerDetail> CompanyAsync(ApiClient admin, string? gstin) =>
        server.CreatePayerAsync(admin, new SavePayerRequest(
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

    private static async Task<JsonNode> JsonAsync(ApiClient client, string path)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
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
