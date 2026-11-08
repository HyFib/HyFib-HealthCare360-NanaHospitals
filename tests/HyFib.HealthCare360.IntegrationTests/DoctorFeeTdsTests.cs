using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;
using HyFib.HealthCare360.Modules.HR.Features.Statutory;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// TDS on a visiting doctor's fees (s.194J, s.206AA): nothing while the year's fees are within ₹50,000; 20 % without a PAN
/// on all the year's fees once a payout crosses it; 10 % once the PAN is given. The quarter's Form 26Q lists the deductions
/// under the month's challan, and the doctor's Form 16A statement shows them.
/// </summary>
public sealed class DoctorFeeTdsTests(HospitalServer server)
{
    private const string Fees = "/api/v1/doctor-fees";

    [Fact]
    public async Task Fees_are_taxed_once_the_year_crosses_the_threshold_at_twenty_percent_without_a_pan_and_go_into_26q()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var manager = await server.SignInAsync(HospitalServer.Users.HrManager);
        var today = server.Today;
        var statements = new DoctorFeeStatementTests(server);
        await StatutoryReturnsTests.ProfileAsync(manager);

        // The doctor earns all of what the procedure is billed at: ₹30,000 a bill.
        var doctor = await DoctorFeeStatementTests.DoctorAsync(admin);
        var surgery = await DoctorFeeStatementTests.ServiceAsync(server, admin, "Laparoscopic hernia repair", 30_000m);
        await DoctorFeeStatementTests.RuleAsync(server, accountant, doctor.Id, surgery.Id, null, ShareMethod.Percentage, 100m, today);

        // ₹30,000 this year: within the threshold.
        await statements.BillAsync(billing, (surgery, 1m, doctor.Id));
        var first = await PayoutAsync(accountant, admin, doctor.Id, today);
        (first.GrossAmount, first.TdsAmount, first.NetAmount).ShouldBe((30_000m, 0m, 30_000m));

        // ₹60,000: over it, with no PAN on record, so 20 % on the year's ₹60,000.
        await statements.BillAsync(billing, (surgery, 1m, doctor.Id));
        var second = await PayoutAsync(accountant, admin, doctor.Id, today);
        (second.TdsPercent, second.TdsAmount, second.NetAmount).ShouldBe((20m, 12_000m, 18_000m));

        // The PAN is given: 10 % on the next ₹30,000.
        var pan = "ABCPD" + Digits(4) + "E";
        await accountant.PutAsync($"{Fees}/tax-profiles/{doctor.Id}", new SaveDoctorTaxProfileRequest(pan, null, null, null, null, null, null));
        (await accountant.GetAsync<DoctorTaxProfileDto>($"{Fees}/tax-profiles/{doctor.Id}")).PanMasked.ShouldBe("XXXXXX" + pan[6..]);
        await statements.BillAsync(billing, (surgery, 1m, doctor.Id));
        var third = await PayoutAsync(accountant, admin, doctor.Id, today);
        (third.TdsPercent, third.TdsAmount).ShouldBe((10m, 3_000m));

        // Form 26Q for the quarter: refused until this month's TDS has a 26Q challan, then both deductions under it.
        var fy = IncomeTaxRates.FinancialYearOf(today);
        var quarter = TdsCertificates.QuarterOf(today);
        using (var refused = await accountant.SendRawAsync(HttpMethod.Get, $"{Fees}/tds/26q/{fy}/{quarter}/file"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var summary = await accountant.GetAsync<JsonElement>($"{Fees}/tds/26q/{fy}/{quarter}");
        var monthTds = summary.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!).Single(p => p.Contains($"{today.Month:00}/{today.Year}", StringComparison.Ordinal));
        var amount = decimal.Parse(monthTds[(monthTds.IndexOf('₹', StringComparison.Ordinal) + 1)..monthTds.IndexOf(')', StringComparison.Ordinal)].Replace(",", "", StringComparison.Ordinal),
            System.Globalization.CultureInfo.InvariantCulture);
        amount.ShouldBeGreaterThanOrEqualTo(15_000m);
        await manager.PostAsync<JsonElement>("/api/v1/hr/statutory/challans", new SaveTdsChallanRequest(
            TdsForm.Form26Q, today.Year, today.Month, "0510308", today, Digits(5), amount, 0, 0, 0, 0, 0, null));

        using var file = await accountant.SendRawAsync(HttpMethod.Get, $"{Fees}/tds/26q/{fy}/{quarter}/file");
        var text = await file.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        file.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('^')).Where(f => f[1] == "DD").ToList();
        var layout = StatutoryLayouts.TdsDeducteeDetail;
        string Field(string[] row, string name) => row[layout.IndexOf(name)];
        var doctorRows = rows.Where(r => Field(r, "Name").Contains(doctor.Name.Split(' ')[^1].ToUpperInvariant(), StringComparison.Ordinal)).ToList();
        doctorRows.Select(r => (Field(r, "PAN"), Field(r, "Total TDS"), Field(r, "Rate"), Field(r, "Remarks"), Field(r, "Section Code"))).ShouldBe(
        [
            (TdsReturnFile.PanNotAvailable, "12000.00", "20.0000", "C", "94J-B"),
            (pan, "3000.00", "10.0000", "", "94J-B"),
        ]);
        text.Split('\n')[1].Split('^')[StatutoryLayouts.TdsBatchHeader.IndexOf("Form Number")].ShouldBe("26Q");

        // The doctor's statement for the quarter: the three payouts, ₹15,000 deducted, under the challan.
        var form16A = await accountant.GetAsync<PrintableDocumentDto>($"{Fees}/tds/16a/{doctor.Id}/{fy}/{quarter}");
        form16A.Html.ShouldContain("not the certificate");
        form16A.Html.ShouldContain("15,000.00");
        form16A.Html.ShouldContain("0510308");
    }

    /// <summary>The accountant closes today's period and the hospital administrator approves it (the date of credit).</summary>
    private static async Task<PayoutDto> PayoutAsync(ApiClient accountant, ApiClient admin, Guid doctorId, DateOnly today)
    {
        var created = await accountant.PostAsync<JsonElement>($"{Fees}/payouts", new CreatePayoutRequest(doctorId, today, today, null, null, null));
        var id = created.GetProperty("id").GetGuid();
        var payout = await admin.GetAsync<PayoutDto>($"{Fees}/payouts/{id}");
        await admin.PostAsync($"{Fees}/payouts/{id}/approve", new PayoutVersionRequest(payout.Version));
        return await admin.GetAsync<PayoutDto>($"{Fees}/payouts/{id}");
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
