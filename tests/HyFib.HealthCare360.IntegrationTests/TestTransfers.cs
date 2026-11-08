using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Transfers;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A move to a bed of another ward, as the wards make it: the sending ward asks, with the reason and a summary, and the
/// receiving ward's nurse in charge (the hospital administrator holds <c>nursing.in_charge</c> in the seed) accepts it.
/// </summary>
internal static class TestTransfers
{
    public static async Task<TransferRequestDto> MoveAsync(HospitalServer server, ApiClient sendingWard, Guid admissionId, Guid toBedId, string reason)
    {
        var asked = await sendingWard.PostAsync<TransferRequestDto>($"/api/v1/inpatient/admissions/{admissionId}/transfer-requests",
            new RequestTransferRequest(toBedId, reason, "Stable; observations and medicines as charted.", null, null, false));
        asked.Status.ShouldBe(TransferRequestStatus.Pending);
        var receivingWard = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var accepted = await receivingWard.PostAsync<TransferRequestDto>($"/api/v1/inpatient/transfer-requests/{asked.Id}/accept",
            new AcceptTransferRequest(null, null, asked.Version));
        accepted.Status.ShouldBe(TransferRequestStatus.Completed);
        return accepted;
    }
}
