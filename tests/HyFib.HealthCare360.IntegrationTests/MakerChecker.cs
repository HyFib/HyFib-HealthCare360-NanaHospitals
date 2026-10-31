using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>Changes one person asks for and another approves (maker-checker), as a test's steps make them.</summary>
internal static class MakerChecker
{
    /// <summary>
    /// Merges <paramref name="duplicateId"/> into <paramref name="keptId"/>: <paramref name="maker"/> asks for it and
    /// <paramref name="checker"/>, another person who may merge records, approves it.
    /// </summary>
    public static async Task<ApprovalRequestDto> MergeAsync(ApiClient maker, ApiClient checker, Guid duplicateId, Guid keptId, string reason)
    {
        var asked = await maker.PostAsync<ApprovalRequestDto>($"/api/v1/patients/{duplicateId}/merge", new MergePatientRequest(keptId, reason));
        return await ApproveAsync(checker, asked);
    }

    public static async Task<ApprovalRequestDto> ApproveAsync(ApiClient checker, ApprovalRequestDto request, string? note = null)
    {
        var approved = await checker.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{request.Id}/approve", new ApproveRequest(note, request.Version));
        approved.Status.ShouldBe(ApprovalStatus.Approved, approved.Outcome);
        return approved;
    }
}
