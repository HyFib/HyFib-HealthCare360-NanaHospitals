using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The simulator sweep's worklist (audit P6): the records of a kind still open now, and the correction a receipt the payment
/// or terminal simulator made needs, as the tests of each kind of record use them.
/// </summary>
internal static class SimulatorRecords
{
    /// <summary>The records of kind <paramref name="code"/> still open now, newest first.</summary>
    public static Task<List<SimulatorOpenRecord>> OpenAsync(ApiClient admin, string code) =>
        admin.GetAsync<List<SimulatorOpenRecord>>($"/api/v1/admin/system/simulator-records/{code}/open");

    /// <summary>All the receipt's money recorded as not received: the billing desk asks, the accountant approves.</summary>
    public static async Task ReverseAsync(ApiClient billing, ApiClient accountant, ReceiptDto receipt)
    {
        var ct = TestContext.Current.CancellationToken;
        using var asked = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/receipts/{receipt.Id}/reversal",
            new ReverseReceiptRequest([.. receipt.Payments.Select(p => p.Id)], ReversalReason.TrainingSimulator, "Made by a training simulator: no money was received."));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(ct));
        await MakerChecker.ApproveAsync(accountant, (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, ct)).ShouldNotBeNull());
    }
}
