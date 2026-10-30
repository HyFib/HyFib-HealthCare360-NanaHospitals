using System.Net;
using System.Net.Http.Json;
using System.Text;
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
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// GST e-invoicing over the real API (GST rule 48(4)). The hospital in Chennai has e-invoicing from yesterday. A bill to
/// a company registered in Kerala, and a pharmacy B2B sale, wait in the register for an IRN; their e-invoice JSON goes to
/// the portal, and the IRN comes back with the signed QR code, which must name the invoice. The bill then prints its IRN
/// and QR code and cannot be cancelled here until the IRN is cancelled on the portal, within 24 hours.
/// </summary>
public sealed class EInvoiceRegisterTests(HospitalServer server)
{
    private const string Register = "/api/v1/billing/einvoices";
    private const string Hospital = "33AABCN1234F1Z3";
    private const string KeralaCompany = "32AAACH7409R1ZA";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bill_to_a_registered_company_gets_its_irn_recorded_printed_and_cancelled_within_the_day()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Executive lounge access", null, ServiceCategory.Miscellaneous, null, 1000m, 18m, "999799", true, false, null, true, null));
        var company = await admin.PostAsync<PayerDetail>("/api/v1/insurance/payers", new SavePayerRequest(
            $"CO{Letters(8).ToUpperInvariant()}", $"Malabar Traders {Letters(6).ToUpperInvariant()} Ltd", PayerCategory.Corporate, "Anil Menon", "9876543210",
            null, "4/12 MG Road, Ernakulam, Kochi, Kerala 682016", KeralaCompany, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null,
            IsActive: true, Notes: null, Version: null));

        await using (await EInvoicingAsync(from: null))
        {
            var early = await BillAsync(company, service);
            var billing = await server.SignInAsync(HospitalServer.Users.Billing);
            using var off = await billing.SendRawAsync(HttpMethod.Get, $"{Register}/bill/{early.Id}");
            (await ProblemCodeAsync(off)).ShouldBe("einvoice.not_applicable");
            (await billing.GetAsync<EInvoiceRegister>(Register)).Applicable.ShouldBeFalse();
        }

        await using var _ = await EInvoicingAsync(from: Today().AddDays(-1));
        var bill = await BillAsync(company, service);
        var accounts = await server.SignInAsync(HospitalServer.Users.Billing);

        // The bill waits in the register, due to the portal within 30 days.
        var register = await accounts.GetAsync<EInvoiceRegister>(Register);
        register.Applicable.ShouldBeTrue();
        var row = register.Rows.Single(r => r.Id == bill.Id);
        (row.Source, row.Number, row.BuyerGstin, row.State, row.Total).ShouldBe(("bill", bill.BillNumber, KeralaCompany, EInvoiceState.Pending, bill.NetAmount));
        row.ReportBy.ShouldBe(row.Date.AddDays(30));

        // Its JSON is the portal's: Kerala is the place of supply, and the whole tax is IGST.
        var payload = await JsonAsync(accounts, $"{Register}/bill/{bill.Id}/json");
        var invoice = payload.AsArray().ShouldHaveSingleItem()!;
        invoice["DocDtls"]!["No"]!.GetValue<string>().ShouldBe(bill.BillNumber);
        (invoice["SellerDtls"]!["Gstin"]!.GetValue<string>(), invoice["SellerDtls"]!["Pin"]!.GetValue<int>()).ShouldBe((Hospital, 600018));
        (invoice["BuyerDtls"]!["Pos"]!.GetValue<string>(), invoice["BuyerDtls"]!["Loc"]!.GetValue<string>(), invoice["BuyerDtls"]!["Pin"]!.GetValue<int>())
            .ShouldBe(("32", "Kochi", 682016));
        invoice["ValDtls"]!["IgstVal"]!.GetValue<decimal>().ShouldBe(bill.IgstAmount);
        invoice["ValDtls"]!["TotInvVal"]!.GetValue<decimal>().ShouldBe(bill.NetAmount);
        var lines = invoice["ItemList"]!.AsArray().Count;
        lines.ShouldBe(bill.Lines.Count);

        // A signed QR code for another invoice is refused; the bill's own is recorded with the acknowledgement.
        var detail = await accounts.GetAsync<EInvoiceDetail>($"{Register}/bill/{bill.Id}");
        detail.Problems.ShouldBeEmpty();
        using var wrong = await accounts.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{bill.Id}/irn",
            new RecordIrnRequest("112610012345678", Qr(bill.BillNumber!, bill.NetAmount + 1m, KeralaCompany, Irn(), lines)));
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadAsStringAsync(Ct)).ShouldContain("not this invoice's");

        var irn = Irn();
        var recorded = await accounts.PostAsync<EInvoiceDetail>($"{Register}/bill/{bill.Id}/irn",
            new RecordIrnRequest("112610012345678", Qr(bill.BillNumber!, bill.NetAmount, KeralaCompany, irn, lines)));
        recorded.State.ShouldBe(EInvoiceState.Registered);
        var registration = recorded.Registration.ShouldNotBeNull();
        (registration.Irn, registration.AckNo, registration.CanCancel).ShouldBe((irn, "112610012345678", true));
        registration.QrCodeSvg.ShouldStartWith("data:image/svg+xml;base64,");
        (await accounts.GetAsync<EInvoiceRegister>(Register)).Rows.Single(r => r.Id == bill.Id).Irn.ShouldBe(irn);

        // The same IRN cannot be recorded twice, nor a second one while the first is live.
        using var again = await accounts.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{bill.Id}/irn",
            new RecordIrnRequest("112610012345679", Qr(bill.BillNumber!, bill.NetAmount, KeralaCompany, Irn(), lines)));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The bill carries its IRN and the signed QR code, on screen and in print.
        var printed = await accounts.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");
        printed.EInvoice.ShouldNotBeNull().Irn.ShouldBe(irn);
        var document = await accounts.GetAsync<PrintableDocumentDto>($"/api/v1/billing/bills/{bill.Id}/document");
        document.Html.ShouldContain(irn);
        document.Html.ShouldContain("112610012345678");
        document.Html.ShouldContain("einvoice-qr");

        // Cancelling the bill waits for the IRN to be cancelled on the portal.
        using var refused = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{bill.Id}/cancel", new CancelBillRequest("Billed to the wrong company", printed.Version));
        (await ProblemCodeAsync(refused)).ShouldBe("einvoice.active");

        var cancelled = await accounts.PostAsync<EInvoiceDetail>($"{Register}/bill/{bill.Id}/irn/cancel",
            new CancelIrnRequest(EInvoiceCancelReason.DataEntryMistake, "Billed to the wrong company", registration.Version));
        cancelled.State.ShouldBe(EInvoiceState.IrnCancelled);
        cancelled.Registration!.Status.ShouldBe(EInvoiceStatus.Cancelled);
        (await accounts.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).EInvoice.ShouldBeNull();
    }

    [Fact]
    public async Task A_pharmacy_b2b_sale_needs_the_buyers_pin_code_and_gets_its_irn_like_a_bill()
    {
        await using var _ = await EInvoicingAsync(from: Today().AddDays(-1));
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        // A medicine of the test's own: other tests sell from the seeded shelf at the same time.
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        async Task<PharmacySaleCreated> SellAsync(string? address) => await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter",
            new CounterSaleRequest(medicine.StoreId, null, "Sree Clinic", null, null, [new CounterSaleLine(medicine.ItemId, 1m, medicine.Batch.BatchId)], 0m,
                [new SalePayment(PharmacyPaymentMode.Cash, Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero), null)], false,
                new B2bBuyer(KeralaCompany, "Sree Clinic", "32", address)));

        var withoutAddress = await SellAsync(null);
        var problems = (await pharmacist.GetAsync<EInvoiceDetail>($"{Register}/pharmacy/{withoutAddress.Id}")).Problems;
        problems.ShouldContain("Enter the buyer's address, with its PIN code.");
        using var refused = await pharmacist.SendRawAsync(HttpMethod.Get, $"{Register}/pharmacy/{withoutAddress.Id}/json");
        (await ProblemCodeAsync(refused)).ShouldBe("einvoice.invalid");

        var sold = await SellAsync("Sree Clinic, 22 Beach Road, Kozhikode 673001");
        var invoice = (await JsonAsync(pharmacist, $"{Register}/pharmacy/{sold.Id}/json")).AsArray().ShouldHaveSingleItem()!;
        var item = invoice["ItemList"]![0]!;
        (item["IsServc"]!.GetValue<string>(), item["Unit"]!.GetValue<string>()).ShouldBe(("N", "NOS"));
        item["IgstAmt"]!.GetValue<decimal>().ShouldBeGreaterThan(0m);
        invoice["ValDtls"]!["TotInvVal"]!.GetValue<decimal>().ShouldBe(sold.NetAmount);

        var irn = Irn();
        await pharmacist.PostAsync<EInvoiceDetail>($"{Register}/pharmacy/{sold.Id}/irn",
            new RecordIrnRequest("112610087654321", Qr(invoice["DocDtls"]!["No"]!.GetValue<string>(), sold.NetAmount, KeralaCompany, irn, 1)));
        var sale = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{sold.Id}");
        sale.EInvoice.ShouldNotBeNull().Irn.ShouldBe(irn);
        sale.BuyerAddress.ShouldBe("Sree Clinic, 22 Beach Road, Kozhikode 673001");
        using var cancel = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/sales/{sold.Id}/cancel", new { Reason = "Wrong buyer" });
        (await ProblemCodeAsync(cancel)).ShouldBe("einvoice.active");
    }

    /// <summary>A corporate visit with one charge of <paramref name="service"/>, billed and finalized by the billing desk.</summary>
    private async Task<BillDetail> BillAsync(PayerDetail company, ServiceItemDto service)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Arun"), MiddleName: null, LastName: "Invoice" + Letters(8), Gender: Gender.Male,
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

    /// <summary>
    /// The hospital registered in Chennai, with e-invoicing <paramref name="from"/> a date (or off), for the rest of the
    /// test; then put back, because the server is shared by every test of the run.
    /// </summary>
    private async Task<IAsyncDisposable> EInvoicingAsync(DateOnly? from)
    {
        var before = new Dictionary<Guid, (string? Gstin, Address Address, string? Setting)>();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            foreach (var facility in await db.Set<Facility>().ToListAsync(Ct))
            {
                before[facility.Id] = (facility.Gstin, facility.Address, await settings.GetAsync<string?>("gst.einvoice.from", facility.Id, Ct));
                facility.Gstin = Hospital;
                facility.Address = new Address { Line1 = "12 Anna Salai", City = "Chennai", State = "Tamil Nadu", PinCode = "600018" };
                await settings.SetAsync<string?>("gst.einvoice.from", from?.ToString("yyyy-MM-dd"), facility.Id, cancellationToken: Ct);
            }

            await db.SaveChangesAsync(Ct);
        }

        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            foreach (var facility in await db.Set<Facility>().ToListAsync(CancellationToken.None))
            {
                if (before.TryGetValue(facility.Id, out var was))
                {
                    (facility.Gstin, facility.Address) = (was.Gstin, was.Address);
                    await settings.SetAsync("gst.einvoice.from", was.Setting, facility.Id, cancellationToken: CancellationToken.None);
                }
            }

            await db.SaveChangesAsync(CancellationToken.None);
        });
    }

    private static async Task<JsonNode> JsonAsync(ApiClient client, string path)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    /// <summary>A signed QR code as the portal makes it, generated a minute ago: the invoice's details in the payload's <c>data</c>.</summary>
    private static string Qr(string number, decimal total, string buyer, string irn, int items)
    {
        var generated = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).AddMinutes(-1);
        var data = new JsonObject
        {
            ["SellerGstin"] = Hospital,
            ["BuyerGstin"] = buyer,
            ["DocNo"] = number,
            ["DocTyp"] = "INV",
            ["DocDt"] = generated.ToString("dd'/'MM'/'yyyy", System.Globalization.CultureInfo.InvariantCulture),
            ["TotInvVal"] = total,
            ["ItemCnt"] = items,
            ["Irn"] = irn,
            ["IrnDt"] = generated.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        };
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("{\"alg\":\"RS256\"}")}.{Part(JsonSerializer.Serialize(new { data = data.ToJsonString(), iss = "NIC" }))}.c2lnbmF0dXJl";
    }

    private static string Irn() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    private static DateOnly Today() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);

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
