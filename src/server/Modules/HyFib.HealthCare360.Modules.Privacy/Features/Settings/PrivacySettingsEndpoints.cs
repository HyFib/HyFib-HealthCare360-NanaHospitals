using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Features.Settings;

/// <summary>What the public privacy notice shows: whom to contact, and how soon requests are answered.</summary>
public sealed record PublicPrivacyInfo(string Hospital, PrivacyContact Contact, int ResponseDays);

/// <summary>The settings as saved, and the contact as published (with the hospital's own details filling any gap).</summary>
public sealed record PrivacySettingsView(PrivacySettingsDto Settings, PrivacyContact Published);

/// <param name="Version">The version of the contact as loaded (REL-04); none until it is first saved.</param>
/// <param name="ResponseDaysVersion">The version of the response period as loaded; none until it is first saved.</param>
public sealed record SavePrivacySettingsRequest(
    string? Title, string? Name, string? Email, string? Phone, string? Address, int ResponseDays, uint? Version = null, uint? ResponseDaysVersion = null);

public sealed class CertInContactValidator : AbstractValidator<CertInContact>
{
    public CertInContactValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name the person CERT-In should contact.").SafeText(200);
        RuleFor(x => x.Designation).SafeText(100);
        RuleFor(x => x.Email).NotEmpty().WithMessage("CERT-In writes to this address: enter it.").MaximumLength(200).EmailAddress();
        RuleFor(x => x.Phone).NotEmpty().WithMessage("Enter a phone number CERT-In can call at any hour.").PhoneNumber();
        RuleFor(x => x.Address).SafeText(500);
    }
}

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
/// /api/v1/privacy/settings (the grievance officer's contact and the response period; /cert-in, the point of contact designated
/// to CERT-In), and the public
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
                    r.ResponseDays, r.Version, r.ResponseDaysVersion), ct);
                return TypedResults.NoContent();
            })
            .WithValidation<SavePrivacySettingsRequest>().FirstSaveCreates();

        // The point of contact designated to CERT-In (CERT-In Directions 2022, (iii)).
        group.MapGet("/cert-in", async (PrivacySettings privacy, CancellationToken ct) => TypedResults.Ok(await privacy.CertInContactAsync(ct)));
        group.MapPut("/cert-in", async (CertInContact contact, PrivacySettings privacy, CancellationToken ct) =>
            {
                await privacy.SaveCertInContactAsync(contact, ct);
                return TypedResults.NoContent();
            })
            .WithValidation<CertInContact>().FirstSaveCreates();

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
