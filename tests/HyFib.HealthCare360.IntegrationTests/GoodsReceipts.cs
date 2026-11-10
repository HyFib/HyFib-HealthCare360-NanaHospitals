using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Goods into stock as the hospital takes them in: the store enters the receipt and sends it for approval, and someone
/// else, who checks receipts against the supplier's invoice, approves it (maker-checker), which posts it into stock.
/// </summary>
internal static class GoodsReceipts
{
    public const string Path = "/api/v1/purchase/grns";

    /// <summary>
    /// <paramref name="store"/> enters the receipt and sends it for approval; the administrator who set the hospital up
    /// (<see cref="HospitalServer.Users.Admin"/>, a second person) approves it. The receipt is then in stock.
    /// </summary>
    public static async Task<GoodsReceiptCreated> IntoStockAsync(HospitalServer server, ApiClient store, CreateGoodsReceiptRequest request)
    {
        var created = await store.PostAsync<GoodsReceiptCreated>(Path, request with { SendForApproval = true });
        created.Status.ShouldBe(GoodsReceiptStatus.Draft);
        var approved = await ApproveAsync(await server.SignInAsync(HospitalServer.Users.Admin), created.ApprovalId.ShouldNotBeNull());
        approved.Status.ShouldBe(ApprovalStatus.Approved);
        return created with { Status = GoodsReceiptStatus.Posted };
    }

    /// <summary>The checker approves the request as it stands now.</summary>
    public static async Task<ApprovalRequestDto> ApproveAsync(ApiClient checker, Guid approvalId)
    {
        var request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        return await checker.PostAsync<ApprovalRequestDto>(
            $"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Checked against the supplier's invoice.", request.Version));
    }
}
