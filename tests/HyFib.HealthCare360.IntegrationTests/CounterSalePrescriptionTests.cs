using System.Net;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using PaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A counter sale against an outside prescription keeps the prescription as presented, its date with it, so the sale is
/// matched to the paper it was made against; a prescription written today is taken, and a date after today is refused as
/// a slip in the entry.
/// </summary>
public sealed class CounterSalePrescriptionTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_counter_sale_keeps_the_outside_prescriptions_date_takes_todays_and_refuses_one_after_today()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var written = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)).AddDays(-3);

        var sale = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter",
            Sale(medicine, new ExternalPrescription("Dr. Kavitha Rao", "TNMC 54321", written, "RX-889")));
        var detail = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{sale.Id}");
        (detail.PrescriberName, detail.PrescriberRegistrationNumber, detail.PrescriptionNumber, detail.PrescriptionDate)
            .ShouldBe(("Dr. Kavitha Rao", "TNMC 54321", "RX-889", (DateOnly?)written));

        var today = written.AddDays(3);
        var writtenToday = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter",
            Sale(medicine, new ExternalPrescription("Dr. Kavitha Rao", "TNMC 54321", today, "RX-890")));
        (await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{writtenToday.Id}")).PrescriptionDate.ShouldBe(today);

        using var tomorrow = await pharmacist.SendRawAsync(HttpMethod.Post, "/api/v1/pharmacy/sales/counter",
            Sale(medicine, new ExternalPrescription("Dr. Kavitha Rao", "TNMC 54321", written.AddDays(4), "RX-891")));
        tomorrow.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await tomorrow.Content.ReadAsStringAsync(Ct));
        (await tomorrow.Content.ReadAsStringAsync(Ct)).ShouldContain("prescription.prescriptionDate");
    }

    /// <summary>One tablet sold over the counter to a walk-in customer, paid in cash, against the prescription.</summary>
    private static CounterSaleRequest Sale(OwnMedicine medicine, ExternalPrescription prescription) =>
        new(medicine.StoreId, null, "Walk-in customer", null, prescription, [new CounterSaleLine(medicine.ItemId, 1m, medicine.Batch.BatchId)], 0m,
            [new SalePayment(PaymentMode.Cash, Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero), null)], false);
}
