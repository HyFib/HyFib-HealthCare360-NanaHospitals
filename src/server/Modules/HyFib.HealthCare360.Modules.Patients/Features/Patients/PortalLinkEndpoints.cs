using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

/// <summary>
/// Links to the patient portal sent to the patient's registered number (PP-01). Staff send one from the patient's record,
/// by SMS or WhatsApp, and see the latest ones. The patient opens it on the portal's page, which shows the hospital and
/// the number masked, and asks for a sign-in code: the code goes to the same number, and the portal's own
/// <c>/api/v1/auth/otp/verify</c> signs them in with it. The link itself signs nobody in.
/// <para>
/// The link's endpoints are anonymous, under <c>/api/v1/public</c> (both the portal's address and the staff app's serve
/// it, and its answers are in the patient's language), as the feedback links are: the "public" rate limit, the route
/// template instead of the path in logs (the token is the link), one generic 404 for any token that does not work now
/// (unknown, expired, replaced, or the record changed since), and nothing about the patient but the hospital and the
/// number masked. GET changes nothing, because SMS apps and link scanners open links by themselves; asking for a code
/// is a POST with a proof of work and the sign-in's limit of codes per address, as the sign-in page's request is. Both
/// stop with the patient portal.
/// </para>
/// </summary>
internal static class PortalLinkEndpoints
{
    private const string NoLongerWorks = "This link no longer works.";

    public static void Map(IEndpointRouteBuilder app)
    {
        var patients = app.MapGroup("/api/v1/patients").WithTags("Patients");
        patients.MapGet("/{patientId:guid}/portal-links", async (Guid patientId, PortalLinkService links, CancellationToken ct) =>
                (await links.ListAsync(patientId, ct)).ToOk())
            .RequirePermission(PatientsPermissions.Read);
        patients.MapPost("/{patientId:guid}/portal-link", async (Guid patientId, SendPortalLinkRequest r, PortalLinkService links, CancellationToken ct) =>
                (await links.SendAsync(patientId, r, ct)).ToOk())
            .RequirePermission(PatientsPermissions.Register).WithValidation<SendPortalLinkRequest>();

        var open = app.MapGroup("/api/v1/public/portal-links").WithTags("Public").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Public)
            .RequireFeature(FeatureKeys.PatientPortal);
        open.MapGet("/{token}", Open).WithSensitivePath();
        open.MapPost("/{token}/otp", RequestCode)
            .RequireAddressRateLimit(ClusterRateLimits.OneTimeCode)
            .RequireProofOfWork(AntiAutomationActions.Otp, subjectOnFailure: PhoneOfLinkAsync)
            .WithSensitivePath();
    }

    private static async Task<IResult> Open(string token, PortalLinkService links, CancellationToken ct) =>
        await links.OpenAsync(token, ct) is { } link ? TypedResults.Ok(link) : NotFound();

    /// <summary>
    /// Sends a sign-in code to the number the link went to. The page asks for the proof with no number (it does not know
    /// it): the work asked is the address's, and a failed request counts against the link's number as well.
    /// </summary>
    private static async Task<IResult> RequestCode(string token, PortalLinkService links, CancellationToken ct) =>
        await links.RequestCodeAsync(token, ct) is { } sent ? sent.ToOk() : NotFound();

    private static Task<string?> PhoneOfLinkAsync(EndpointFilterInvocationContext context, CancellationToken ct) =>
        context.Arguments.OfType<string>().FirstOrDefault() is { } token
            ? context.HttpContext.RequestServices.GetRequiredService<PortalLinkService>().PhoneOfAsync(token, ct)
            : Task.FromResult<string?>(null);

    private static IResult NotFound() => TypedResults.Problem(detail: NoLongerWorks, statusCode: StatusCodes.Status404NotFound);
}
