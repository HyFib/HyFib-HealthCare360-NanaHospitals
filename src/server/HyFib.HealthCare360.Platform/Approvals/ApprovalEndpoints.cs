using FluentValidation;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.Platform.Approvals;

/// <param name="Note">Anything the checker wants kept with the approval.</param>
/// <param name="Version">The version of the request the checker saw: a request withdrawn or decided meanwhile is not approved.</param>
public sealed record ApproveRequest(string? Note, uint Version);

public sealed record RejectApprovalRequest(string Reason, uint Version);

public sealed record WithdrawApprovalRequest(uint Version);

public sealed class ApproveRequestValidator : AbstractValidator<ApproveRequest>
{
    public ApproveRequestValidator()
    {
        RuleFor(x => x.Note).SafeText(500);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class RejectApprovalRequestValidator : AbstractValidator<RejectApprovalRequest>
{
    public RejectApprovalRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say why it is rejected: the person who asked for it is told.").SafeText(500);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class WithdrawApprovalRequestValidator : AbstractValidator<WithdrawApprovalRequest>
{
    public WithdrawApprovalRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

/// <summary>
/// The approvals inbox. Any signed-in member of staff may call these: what each person sees and may decide follows from
/// the permissions of the kinds of change (<see cref="IApprovalAction.ApprovePermission"/>) and from who asked, which the
/// service checks on every call.
/// </summary>
internal static class ApprovalEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/approvals").WithTags("Approvals").RequireAuthorization();

        group.MapGet("/", async ([AsParameters] PageQuery query, ApprovalView? view, ApprovalStatus? status, string? subjectType, Guid? subjectId,
                ApprovalService approvals, CancellationToken ct) =>
            (await approvals.ListAsync(view ?? ApprovalView.ToDecide, status, subjectType, subjectId, query.ToRequest(), ct)).ToOk());

        group.MapGet("/{id:guid}", async (Guid id, ApprovalService approvals, CancellationToken ct) => (await approvals.GetAsync(id, ct)).ToOk());

        group.MapPost("/{id:guid}/approve", async (Guid id, ApproveRequest request, ApprovalService approvals, CancellationToken ct) =>
                (await approvals.ApproveAsync(id, request.Note, request.Version, ct)).ToOk())
            .WithValidation<ApproveRequest>();

        group.MapPost("/{id:guid}/reject", async (Guid id, RejectApprovalRequest request, ApprovalService approvals, CancellationToken ct) =>
                (await approvals.RejectAsync(id, request.Reason, request.Version, ct)).ToOk())
            .WithValidation<RejectApprovalRequest>();

        group.MapPost("/{id:guid}/withdraw", async (Guid id, WithdrawApprovalRequest request, ApprovalService approvals, CancellationToken ct) =>
                (await approvals.WithdrawAsync(id, request.Version, ct)).ToOk())
            .WithValidation<WithdrawApprovalRequest>();
    }
}
