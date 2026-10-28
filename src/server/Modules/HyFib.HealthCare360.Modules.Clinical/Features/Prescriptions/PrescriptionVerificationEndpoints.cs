using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>
/// Anonymous QR verification of a printed prescription (ERX-04). A pharmacist outside the hospital scans the code and
/// sees only non-PHI facts — prescriber, facility, date, prescription number, number of medicines and whether it is still
/// valid — and, when the doctor signed it with their DSC, the signer, the certificate, the SHA-256 of the signed bytes and
/// whether the signature checks out now, from a certificate that chains to a CCA India root (re-verified on every
/// request). The .p7s can be downloaded for an independent
/// check; the signed bytes themselves name the patient and are not public. The token is 144 random bits, and both
/// endpoints are rate-limited to stop enumeration.
/// </summary>
internal static class PrescriptionVerificationEndpoints
{
    public const string PublicRateLimit = RateLimitPolicies.Public;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/public/prescriptions/verify/{token}", Verify)
            .WithTags("Public")
            .AllowAnonymous()
            .RequireRateLimiting(PublicRateLimit)
            .WithSensitivePath();
        app.MapGet("/api/v1/public/prescriptions/verify/{token}/signature.p7s", Signature)
            .WithTags("Public")
            .AllowAnonymous()
            .RequireRateLimiting(PublicRateLimit)
            .WithSensitivePath();
    }

    private static async Task<IResult> Verify(string token, HospitalDbContext db, DscTrustStore trust, CancellationToken ct)
    {
        var rx = await FindAsync(token, db, ct);
        if (rx is null)
        {
            return Error.NotFound("Prescription").ToProblem();
        }

        var prescriber = await PrintQueries.PrescriberAsync(db, rx, ct);
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
            rx.Items.Count,
            rx.PrescriptionNumber,
            await PublicSignatureAsync(rx, db, trust, ct),
            rx.IsDigitallySigned ? PrescriptionVerificationLinks.SignaturePath(token) : null));
    }

    /// <summary>The DSC signature as a .p7s, to check against the signed bytes the patient or the hospital gives.</summary>
    private static async Task<IResult> Signature(string token, HospitalDbContext db, CancellationToken ct)
    {
        var rx = await FindAsync(token, db, ct);
        return rx?.DigitalSignature is { } signature
            ? TypedResults.File(signature, PrescriptionEndpoints.SignatureContentType, $"prescription-{rx.PrescriptionNumber}.p7s")
            : Error.NotFound("Prescription").ToProblem();
    }

    private static async Task<Prescription?> FindAsync(string token, HospitalDbContext db, CancellationToken ct)
    {
        if (token.Length is < 16 or > 64 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }

        return await db.Set<Prescription>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.VerificationToken == token, ct);
    }

    private static async Task<PublicDigitalSignature?> PublicSignatureAsync(Prescription rx, HospitalDbContext db, DscTrustStore trust, CancellationToken ct)
    {
        if (!rx.IsDigitallySigned)
        {
            return null;
        }

        var check = await PrescriptionDigitalSignatureService.CheckAsync(db, trust.Current, rx, PrescriptionSignaturePolicy.Off, ct);
        return new PublicDigitalSignature(check.State, rx.SignerName!, rx.SignerSerialNumber!, rx.SignerIssuer!, rx.DigitallySignedAt!.Value,
            rx.SignedContentHash!, check.ChainTrusted, check.Message!);
    }
}
