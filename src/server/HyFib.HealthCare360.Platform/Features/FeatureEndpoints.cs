using FluentValidation;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.Platform.Features;

/// <summary>A feature switch as the Features screen shows it; <see cref="Enabled"/> is the hospital-wide value.</summary>
public sealed record FeatureDto(string Key, string Code, string Name, string Description, string WhenOff, bool DefaultEnabled, bool Enabled);

/// <summary>Turns a feature on or off for the whole hospital.</summary>
public sealed record SetFeatureRequest(bool? Enabled);

public sealed class SetFeatureRequestValidator : AbstractValidator<SetFeatureRequest>
{
    public SetFeatureRequestValidator() =>
        RuleFor(x => x.Enabled).NotNull().WithMessage("Say whether the feature should be on or off.");
}

/// <summary>/api/v1/admin/features — the feature switches for administrators (<c>admin.settings.manage</c>).</summary>
internal static class FeatureEndpoints
{
    public const string ChangedAction = "feature.changed";

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin/features").WithTags("Administration").RequirePermission(PlatformPermissions.SettingsManage);

        admin.MapGet("/", async (IFeatureFlags features, CancellationToken ct) =>
        {
            var list = new List<FeatureDto>();
            foreach (var feature in FeatureCatalog.All)
            {
                list.Add(Dto(feature, await features.IsEnabledAsync(feature.Key, null, ct)));
            }

            return TypedResults.Ok(list);
        });

        // The hospital-wide value, saved with its audit entry in one transaction. A branch's own value, if one was set in
        // the settings store, still overrides it for that branch.
        admin.MapPut("/{key}", async (string key, SetFeatureRequest request, IFeatureFlags features, ISettingsService settings, IAuditLogger audit, CancellationToken ct) =>
            {
                if ((FeatureCatalog.Find(key) ?? FeatureCatalog.Find(FeatureKeys.Prefix + key)) is not { } feature)
                {
                    return Error.NotFound("Feature", key).ToProblem();
                }

                var enabled = request.Enabled!.Value;
                var wasEnabled = await features.IsEnabledAsync(feature.Key, null, ct);
                audit.Add(new AuditEntry(AuditCategories.Admin, ChangedAction, $"{feature.Name}: turned {(enabled ? "on" : "off")}")
                {
                    EntityType = "Feature",
                    EntityId = feature.Key,
                    Details = new { feature = feature.Code, enabled, wasEnabled },
                    Severity = enabled ? AuditSeverity.Info : AuditSeverity.Warning,
                });
                await settings.SetAsync(feature.Key, enabled, facilityId: null, secret: false, ct);
                return TypedResults.Ok(Dto(feature, enabled));
            })
            .WithValidation<SetFeatureRequest>();
    }

    private static FeatureDto Dto(FeatureDefinition feature, bool enabled) =>
        new(feature.Key, feature.Code, feature.Name, feature.Description, feature.WhenOff, feature.DefaultEnabled, enabled);
}
