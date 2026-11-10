using System.Net;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A patient's policy or membership with a payer, as putting a visit, an admission or a stay on that payer needs it: the
/// billing desk records it and another person, the hospital administrator, verifies it with the payer.
/// </summary>
internal static class Coverage
{
    public static async Task<PolicyDto> VerifiedPolicyAsync(this HospitalServer server, Guid patientId, Guid payerId, decimal coPayPercent = 0m)
    {
        var recorder = await server.SignInAsync(HospitalServer.Users.Billing);
        var verifier = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var policy = await recorder.PostAsync<PolicyDto>("/api/v1/insurance/policies", new SavePolicyRequest(
            patientId, payerId, null, "MEM-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), null, null, null,
            today.AddDays(-30), today.AddYears(1), 0m, coPayPercent, IsActive: true, CardFileId: null, Notes: null, Version: null));
        using var verified = await verifier.SendRawAsync(HttpMethod.Post, $"/api/v1/insurance/policies/{policy.Id}/verify",
            new VerifyPolicyRequest("Membership confirmed with the payer"));
        verified.StatusCode.ShouldBe(HttpStatusCode.NoContent, await verified.Content.ReadAsStringAsync());
        return policy;
    }
}
