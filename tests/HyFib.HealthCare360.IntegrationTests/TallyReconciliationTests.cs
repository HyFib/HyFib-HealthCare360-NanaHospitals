using System.Net;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Tally;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The Tally day book end to end (INT-TALLY), through the real API and the outbox: a facility without Tally gets "not
/// set up" instead of a failure, imports the file and marks the day imported; with the simulator as its Tally the day
/// posts, every voucher is confirmed, and the check reads it back and finds it matched. The day is a quiet date in the
/// past with one receipt of the test's own, so no other test's activity changes the day book.
/// </summary>
public sealed class TallyReconciliationTests(HospitalServer server)
{
    private const string Base = "/api/v1/billing/tally";
    private const string TallySetting = "integrations.tally";
    private static readonly DateOnly Day = new(2025, 1, 15);

    private static string Date => Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_day_without_tally_is_imported_from_the_file_and_with_tally_it_posts_and_reads_back_matched()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("The administrator has no facility.");
        var receiptNumber = await AddReceiptAsync(facilityId);
        await admin.SaveSettingAsync(TallySetting, new SaveSettingRequest(JsonNode.Parse("""{"enabled":false}"""), null, false, null));
        try
        {
            var before = await admin.GetAsync<TallyDayVouchersDto>($"{Base}/days/{Date}/vouchers");
            before.Status.ShouldBeNull();
            var voucher = before.Vouchers.ShouldHaveSingleItem();
            voucher.ShouldSatisfyAllConditions(
                v => v.Number.ShouldBe(receiptNumber),
                v => v.RemoteId.ShouldBe("HC360-" + receiptNumber),
                v => v.Amount.ShouldBe(500m),
                v => v.Status.ShouldBe(TallyVoucherStatus.NotSent));

            // Without Tally the post is not a failure: the day waits for the file to be imported.
            var notSetUp = await PostAsync(admin);
            notSetUp.Status.ShouldBe(TallyExportStatus.NotConfigured);
            notSetUp.Message!.ShouldContain("Download the day book file");

            using (var file = await admin.SendRawAsync(HttpMethod.Get, $"{Base}/export-file?date={Date}"))
            {
                file.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await file.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain($"REMOTEID=\"HC360-{receiptNumber}\"");
            }

            var imported = await admin.PostAsync<TallyExportDto>($"{Base}/days/{Date}/imported");
            imported.ShouldSatisfyAllConditions(e => e.Status.ShouldBe(TallyExportStatus.Imported), e => e.ConfirmedCount.ShouldBe(1));
            var afterImport = await admin.GetAsync<TallyDayCheckDto>($"{Base}/days/{Date}/check");
            afterImport.Records.ShouldBe(TallyCheckStatus.Matched);
            afterImport.Lines.Single(l => l.Group == "Money received" && l.Item == "Cash").ShouldSatisfyAllConditions(l => l.Records.ShouldBe(500m), l => l.DayBook.ShouldBe(500m));

            // With the simulator as Tally the day posts, Tally confirms the voucher, and reading it back finds it as sent.
            await admin.SaveSettingAsync(TallySetting, new SaveSettingRequest(
                JsonNode.Parse($$"""{"enabled":true,"provider":"simulator","companyName":"IT {{Guid.NewGuid():N}}"}"""), null, false, null));
            var posted = await PostAsync(admin);
            posted.ShouldSatisfyAllConditions(
                e => e.Status.ShouldBe(TallyExportStatus.Posted),
                e => e.VoucherCount.ShouldBe(1),
                e => e.ConfirmedCount.ShouldBe(1),
                e => e.CreatedCount.ShouldBe(1));

            var check = await admin.PostAsync<TallyDayCheckDto>($"{Base}/days/{Date}/check?againstTally=true");
            check.ShouldSatisfyAllConditions(
                c => c.Records.ShouldBe(TallyCheckStatus.Matched),
                c => c.Tally.ShouldBe(TallyCheckStatus.Matched),
                c => c.TallyDifferences.ShouldBe(0),
                c => c.Sending.InTally.ShouldBe(1));
            var vouchers = await admin.GetAsync<TallyDayVouchersDto>($"{Base}/days/{Date}/vouchers");
            vouchers.Vouchers.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
                v => v.Answer.ShouldBe(TallyVoucherAnswer.Created),
                v => v.Match.ShouldBe(TallyMatch.Matched),
                v => v.Status.ShouldBe(TallyVoucherStatus.InTally));
        }
        finally
        {
            await admin.SaveSettingAsync(TallySetting, new SaveSettingRequest(JsonNode.Parse("""{"enabled":false}"""), null, false, null));
        }
    }

    [Fact]
    public async Task Ledger_names_are_set_from_the_tally_screen_and_checked()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var current = await admin.GetAsync<TallyLedgerMap>($"{Base}/ledgers");
        current.Pharmacy.Sales.ShouldBe("Pharmacy Sales");

        using var refused = await admin.SendRawAsync(HttpMethod.Put, $"{Base}/ledgers", current with { RoundOff = "Round\nOff" });
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await admin.PutAsync($"{Base}/ledgers", current with { RoundOff = "Rounding Off" });
        var changed = await admin.GetAsync<TallyLedgerMap>($"{Base}/ledgers");
        changed.RoundOff.ShouldBe("Rounding Off");
        await admin.PutAsync($"{Base}/ledgers", current with { Version = changed.Version });
    }

    /// <summary>Queues the day and waits for the outbox to post it.</summary>
    private static async Task<TallyExportDto> PostAsync(ApiClient admin)
    {
        await admin.PostAsync($"{Base}/exports", new PostDayBookRequest(Day));
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var export = (await admin.GetAsync<List<TallyExportDto>>($"{Base}/exports?from={Date}&to={Date}")).Single();
            if (export.Status != TallyExportStatus.Queued)
            {
                return export;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The day book was not posted within a minute.");
    }

    /// <summary>One cash receipt at noon on the day, the only transaction the facility has on it.</summary>
    private async Task<string> AddReceiptAsync(Guid facilityId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var patientId = await db.Set<Patient>().IgnoreQueryFilters().Select(p => p.Id).FirstAsync(ct);
        var number = "ITRC" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var receipt = new Receipt
        {
            FacilityId = facilityId,
            ReceiptNumber = number,
            PatientId = patientId,
            Purpose = ReceiptPurpose.BillPayment,
            Source = ReceiptSource.Counter,
            Amount = 500m,
            ReceivedAt = BillingTime.StartOfDay(Day).AddHours(12),
        };
        receipt.Payments.Add(new ReceiptPayment { ReceiptId = receipt.Id, Mode = PaymentMode.Cash, Amount = 500m });
        db.Add(receipt);
        await db.SaveChangesAsync(ct);
        return number;
    }
}
