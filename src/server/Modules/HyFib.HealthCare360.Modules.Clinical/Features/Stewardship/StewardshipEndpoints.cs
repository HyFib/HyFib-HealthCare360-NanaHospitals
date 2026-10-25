using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Stewardship;

/// <summary>
/// /api/v1/ams — antimicrobial stewardship: the restricted antibiotics waiting for approval or review, the prescriber's
/// justification, the team's decision and review, and the programme's report.
/// </summary>
internal static class StewardshipEndpoints
{
    private static readonly string[] Readers = [ClinicalPermissions.AmsRead, ClinicalPermissions.AmsApprove];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ams").WithTags("Antimicrobial Stewardship");

        group.MapGet("/approvals", async (ApprovalQueue? queue, bool? mine, StewardshipService ams, CancellationToken ct) =>
            TypedResults.Ok(await ams.ListAsync(queue ?? ApprovalQueue.Pending, mine == true, ct))).RequireAnyPermission(Readers);
        group.MapGet("/approvals/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapPost("/approvals/{id:guid}/justification", Justify)
            .RequirePermission(ClinicalPermissions.PrescriptionsWrite).WithValidation<JustifyRequest>();
        group.MapPost("/approvals/{id:guid}/decision", Decide).RequirePermission(ClinicalPermissions.AmsApprove).WithValidation<DecideRequest>();
        group.MapPost("/approvals/{id:guid}/review", Review)
            .RequireAnyPermission(ClinicalPermissions.AmsApprove, ClinicalPermissions.PrescriptionsWrite).WithValidation<ReviewRequest>();
        group.MapGet("/report", Report).RequireAnyPermission(Readers);
    }

    private static async Task<IResult> Get(Guid id, StewardshipService ams, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, ams, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await ams.DtoAsync(opened.Value, ct));
    }

    private static async Task<IResult> Justify(Guid id, JustifyRequest request, StewardshipService ams, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, ams, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await ams.JustifyAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> Decide(Guid id, DecideRequest request, StewardshipService ams, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, ams, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await ams.DecideAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> Review(
        Guid id, ReviewRequest request, StewardshipService ams, PatientAccessGuard guard, IPermissionService permissions, ICurrentUser user,
        CancellationToken ct)
    {
        var opened = await OpenAsync(id, ams, guard, ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var steward = user.UserId is { } me && await permissions.HasPermissionAsync(me, ClinicalPermissions.AmsApprove, ct);
        return (await ams.ReviewAsync(id, request, steward, ct)).ToOk();
    }

    private static async Task<IResult> Report(DateOnly? from, DateOnly? to, StewardshipService ams, TimeProvider time, CancellationToken ct)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-30);
        if (end < start || end.DayNumber - start.DayNumber > 366)
        {
            return Error.Field("to", "Choose up to a year, ending on or after the start.").ToProblem();
        }

        return TypedResults.Ok(await ams.ReportAsync(start, end, ct));
    }

    private static async Task<Result<Domain.Stewardship.AntimicrobialApproval>> OpenAsync(
        Guid id, StewardshipService ams, PatientAccessGuard guard, CancellationToken ct)
    {
        if (await ams.FindAsync(id, ct) is not { } approval)
        {
            return Error.NotFound("Antibiotic approval", id);
        }

        var allowed = await guard.EnsureCanOpenAsync(approval.PatientId, ct);
        return allowed.IsFailure ? allowed.Error! : approval;
    }
}
