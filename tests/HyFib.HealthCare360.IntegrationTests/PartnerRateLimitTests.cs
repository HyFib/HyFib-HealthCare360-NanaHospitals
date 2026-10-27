using System.Net;
using HyFib.HealthCare360.Platform.Hosting;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Calls from the hospital's partners (SMS and WhatsApp delivery receipts, the payment gateway, the ABDM gateway) come in
/// bursts from a few addresses: a campaign of 500 messages brings 500 receipts from the provider's address within a
/// minute. They have their own budget per address (<see cref="RateLimitPolicies.Partner"/>), larger than the public
/// one that QR codes and links share, and outside the per-address limit on every request, so a burst of receipts is
/// taken while the public budget stays as it was. Each test calls from its own address.
/// </summary>
public sealed class PartnerRateLimitTests(HospitalServer server)
{
    private const string SmsReceipts = "/api/v1/integrations/webhooks/sms/generic";
    private const string PrescriptionCheck = "/api/v1/public/prescriptions/verify/NOSUCHCODE";

    [Fact]
    public async Task A_campaigns_burst_of_delivery_receipts_is_taken()
    {
        var provider = server.Anonymous(ownAddress: true);

        // 500 receipts at once: more than the public budget (60) and the per-address burst on every request (400).
        var refused = 0;
        for (var i = 0; i < 500; i++)
        {
            using var receipt = await provider.SendRawAsync(HttpMethod.Post, SmsReceipts, new { messageId = $"m{i}", status = "delivered" });
            refused += receipt.StatusCode == HttpStatusCode.TooManyRequests ? 1 : 0;
        }

        refused.ShouldBe(0);
    }

    [Fact]
    public async Task Partners_have_a_budget_of_their_own()
    {
        var defaults = new RateLimitOptions();
        var provider = server.Anonymous(ownAddress: true);

        var refused = 0;
        for (var i = 0; i <= defaults.Partner.PermitLimit; i++)
        {
            using var receipt = await provider.SendRawAsync(HttpMethod.Post, SmsReceipts, new { messageId = $"m{i}", status = "delivered" });
            refused += receipt.StatusCode == HttpStatusCode.TooManyRequests ? 1 : 0;
        }

        refused.ShouldBe(1, "one past the partner budget is refused");
    }

    [Fact]
    public async Task The_public_budget_is_unchanged()
    {
        var defaults = new RateLimitOptions();
        var visitor = server.Anonymous(ownAddress: true);

        var refused = 0;
        for (var i = 0; i <= defaults.Public.PermitLimit; i++)
        {
            using var check = await visitor.SendRawAsync(HttpMethod.Get, PrescriptionCheck);
            refused += check.StatusCode == HttpStatusCode.TooManyRequests ? 1 : 0;
        }

        refused.ShouldBe(1, "one past the public budget is refused");
    }
}
