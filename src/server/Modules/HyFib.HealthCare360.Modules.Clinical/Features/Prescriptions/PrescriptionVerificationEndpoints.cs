using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>
/// Anonymous QR verification of a printed prescription (ERX-04). A pharmacist outside the hospital scans the code and
/// sees only non-PHI facts — prescriber, facility, date, number of medicines and whether it is still valid. The token is
/// 144 random bits, and the endpoint is rate-limited to stop enumeration.
/// </summary>
internal static class PrescriptionVerificationEndpoints
{
    public const string PublicRateLimit = RateLimitPolicies.Public;

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/public/prescriptions/verify/{token}", Verify)
            .WithTags("Public")
            .AllowAnonymous()
            .RequireRateLimiting(PublicRateLimit);

    private static async Task<IResult> Verify(string token, HospitalDbContext db, CancellationToken ct)
    {
        var notFound = Error.NotFound("Prescription").ToProblem();
        if (token.Length is < 16 or > 64 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return notFound;
        }

        var rx = await db.Set<Prescription>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(p => p.VerificationToken == token)
            .Select(p => new { p.Status, p.SignedAt, p.PractitionerId, p.FacilityId, Items = p.Items.Count })
            .FirstOrDefaultAsync(ct);
        if (rx is null)
        {
            return notFound;
        }

        var prescriber = await PrintQueries.PrescriberAsync(db, rx.PractitionerId, ct);
        var facility = await db.Set<Facility>().AsNoTracking().IgnoreQueryFilters().Where(f => f.Id == rx.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        var registration = prescriber?.RegistrationNumber is { } number
            ? string.IsNullOrWhiteSpace(prescriber.RegistrationCouncil) ? number : $"{number} ({prescriber.RegistrationCouncil})"
            : null;
        return TypedResults.Ok(new PrescriptionVerification(
            true,
            rx.Status == PrescriptionStatus.Signed ? "Valid" : "Cancelled",
            prescriber?.DisplayName,
            prescriber?.Qualification,
            registration,
            facility,
            rx.SignedAt is { } signed ? ClinicalClock.LocalDate(signed) : null,
            rx.Items));
    }
}
