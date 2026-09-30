using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Settings;

/// <summary>What the public privacy notice shows: whom to contact, and how soon requests are answered.</summary>
public sealed record PublicPrivacyInfo(string Hospital, PrivacyContact Contact, int ResponseDays);

/// <summary>The settings as saved, and the contact as published (with the hospital's own details filling any gap).</summary>
public sealed record PrivacySettingsView(PrivacySettingsDto Settings, PrivacyContact Published);

public sealed record SavePrivacySettingsRequest(string? Title, string? Name, string? Email, string? Phone, string? Address, int ResponseDays);

public sealed class SavePrivacySettingsRequestValidator : AbstractValidator<SavePrivacySettingsRequest>
{
    public SavePrivacySettingsRequestValidator()
    {
        RuleFor(x => x.Title).SafeText(100);
        RuleFor(x => x.Name).SafeText(200);
        RuleFor(x => x.Email).MaximumLength(200).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Address).SafeText(500);
        RuleFor(x => x.ResponseDays).InclusiveBetween(1, DataRequest.MaxResponseDays)
            .WithMessage($"Choose 1 to {DataRequest.MaxResponseDays} days: the DPDP Rules 2025 allow at most {DataRequest.MaxResponseDays}.");
    }
}

/// <summary>
/// /api/v1/privacy/settings (the grievance officer's contact and the response period), and the public
/// /api/v1/public/privacy the portal's privacy notice reads without sign-in (DPDP Rules 2025, rules 9 and 14).
/// </summary>
internal static class PrivacySettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/privacy/settings").WithTags("Privacy").RequirePermission(PrivacyPermissions.SettingsManage);
        group.MapGet("/", async (PrivacySettings privacy, CancellationToken ct) =>
            TypedResults.Ok(new PrivacySettingsView(await privacy.GetAsync(ct), await privacy.PublishedContactAsync(ct))));
        group.MapPut("/", async (SavePrivacySettingsRequest r, PrivacySettings privacy, CancellationToken ct) =>
            {
                await privacy.SaveAsync(new PrivacySettingsDto(new PrivacyContact(r.Title ?? PrivacySettings.DefaultTitle, r.Name, r.Email, r.Phone, r.Address),
                    r.ResponseDays), ct);
                return TypedResults.NoContent();
            })
            .WithValidation<SavePrivacySettingsRequest>();

        // Public: the privacy notice names the grievance officer for everyone, signed in or not.
        app.MapGet("/api/v1/public/privacy", async (PrivacySettings privacy, HospitalDbContext db, CancellationToken ct) =>
            {
                var hospital = await db.Set<Facility>().AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.CreatedAt).Select(f => f.Name).FirstOrDefaultAsync(ct);
                return TypedResults.Ok(new PublicPrivacyInfo(hospital ?? "Hospital", await privacy.PublishedContactAsync(ct), await privacy.ResponseDaysAsync(ct)));
            })
            .WithTags("Privacy")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public);
    }
}
