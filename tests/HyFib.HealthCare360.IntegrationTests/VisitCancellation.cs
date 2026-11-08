using System.Net;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>A visit cancelled from the desk as a test's step: at once, or, once the doctor has started it, approved by the hospital administrator.</summary>
internal static class VisitCancellation
{
    public static async Task CancelAsync(HospitalServer server, ApiClient desk, Guid visitId, string reason)
    {
        var path = $"/api/v1/visits/{visitId}/cancel";
        using var response = await desk.SendRawAsync(HttpMethod.Post, path, new CancelVisitRequest(reason));
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path);
            await MakerChecker.ApproveAsync(await server.SignInAsync(HospitalServer.Users.HospitalAdmin), asked);
            return;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
