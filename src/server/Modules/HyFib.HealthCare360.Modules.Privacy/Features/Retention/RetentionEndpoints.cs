using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Retention;

/// <param name="Version">The version of the period as loaded (REL-04); none while it is the default.</param>
public sealed record UpdateRetentionRequest(int Value, uint? Version = null);

public sealed class UpdateRetentionRequestValidator : AbstractValidator<UpdateRetentionRequest>
{
    public UpdateRetentionRequestValidator() => RuleFor(x => x.Value).GreaterThan(0);
}

/// <summary>/api/v1/privacy/retention: the retention schedule (SRS PRV-02), its clean-up and the review list.</summary>
internal static class RetentionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/privacy/retention").WithTags("Privacy").RequirePermission(PrivacyPermissions.RetentionManage);

        group.MapGet("/", async (RetentionService retention, CancellationToken ct) => TypedResults.Ok(await retention.RulesAsync(ct)));
        group.MapPut("/{key}", async (string key, UpdateRetentionRequest r, RetentionService retention, CancellationToken ct) =>
                (await retention.UpdateAsync(key, r.Value, r.Version, ct)).ToNoContent())
            .WithValidation<UpdateRetentionRequest>().FirstSaveCreates();
        group.MapGet("/runs", async (int? take, RetentionService retention, CancellationToken ct) =>
            TypedResults.Ok(await retention.RunsAsync(take ?? 20, ct)));
        group.MapGet("/preview", async (RetentionService retention, CancellationToken ct) => TypedResults.Ok(await retention.PreviewAsync(ct)));
        group.MapPost("/run", async (RetentionService retention, CancellationToken ct) =>
        {
            await retention.RequestRunAsync(ct);
            return TypedResults.Accepted("/api/v1/privacy/retention/runs");
        });
        group.MapGet("/review", async (RetentionService retention, CancellationToken ct) => TypedResults.Ok(await retention.ReviewAsync(ct)));
    }
}
