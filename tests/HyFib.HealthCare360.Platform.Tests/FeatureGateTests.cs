using System.Security.Claims;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// An endpoint marked <c>RequireFeature</c> answers 503 <c>feature.disabled</c> while its feature is off, before its
/// handler runs, in words for whoever reads it; and while the portal is off a patient's sign-in opens nothing.
/// </summary>
public sealed class FeatureGateTests
{
    private static readonly Guid FacilityId = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000a");

    private readonly FakeFlags _flags = new();
    private bool _reachedHandler;

    [Fact]
    public async Task An_endpoint_without_a_switch_is_not_affected()
    {
        _flags.Off(FeatureKeys.Telehealth);

        var response = await SendAsync(EndpointWith());

        response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        _reachedHandler.ShouldBeTrue();
    }

    [Fact]
    public async Task An_endpoint_whose_feature_is_on_runs()
    {
        var response = await SendAsync(EndpointWith(new RequiresFeatureMetadata(FeatureKeys.Telehealth)));

        response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        _reachedHandler.ShouldBeTrue();
    }

    [Fact]
    public async Task An_endpoint_whose_feature_is_off_answers_503_feature_disabled_without_running()
    {
        _flags.Off(FeatureKeys.Telehealth);

        var response = await SendAsync(EndpointWith(new RequiresFeatureMetadata(FeatureKeys.Telehealth)), Staff());

        _reachedHandler.ShouldBeFalse();
        response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        response.ContentType.ShouldStartWith("application/problem+json");
        var problem = await ProblemOf(response);
        problem.GetProperty("code").GetString().ShouldBe(FeatureProblems.Code);
        problem.GetProperty("data").GetProperty("feature").GetString().ShouldBe("telehealth");
        problem.GetProperty("title").GetString().ShouldBe("Video consultations are turned off");
        problem.GetProperty("detail").GetString().ShouldBe(FeatureCatalog.Telehealth.OffDetailForStaff);
    }

    [Fact]
    public async Task Patients_and_anonymous_callers_are_told_what_they_can_do()
    {
        _flags.Off(FeatureKeys.PatientPortal);

        var anonymous = await SendAsync(EndpointWith(new AllowAnonymousAttribute(), new RequiresFeatureMetadata(FeatureKeys.PatientPortal)));

        (await ProblemOf(anonymous)).GetProperty("detail").GetString()
            .ShouldBe("The patient portal is not available right now. Please contact the hospital.");
    }

    [Fact]
    public async Task The_switch_is_read_for_the_callers_facility()
    {
        _flags.Off(FeatureKeys.Telehealth, FacilityId);

        var atFacility = await SendAsync(EndpointWith(new RequiresFeatureMetadata(FeatureKeys.Telehealth)), Staff(FacilityId));
        var elsewhere = await SendAsync(EndpointWith(new RequiresFeatureMetadata(FeatureKeys.Telehealth)), Staff());

        atFacility.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        elsewhere.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task A_switch_that_applies_to_some_requests_leaves_the_others_alone()
    {
        _flags.Off(FeatureKeys.PatientPortal);
        var portalOnly = EndpointWith(new AllowAnonymousAttribute(), new RequiresFeatureMetadata(FeatureKeys.PatientPortal, r => r.Headers["X-HC360-Client"] == "portal"));

        var staffApp = await SendAsync(portalOnly, configure: http => http.Request.Headers["X-HC360-Client"] = "web");
        var portal = await SendAsync(portalOnly, configure: http => http.Request.Headers["X-HC360-Client"] = "portal");

        staffApp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        portal.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Every_switch_of_an_endpoint_must_be_on()
    {
        _flags.Off(FeatureKeys.Telehealth);
        var both = EndpointWith(new RequiresFeatureMetadata(FeatureKeys.PatientPortal), new RequiresFeatureMetadata(FeatureKeys.Telehealth));

        var response = await SendAsync(both, Patient());

        (await ProblemOf(response)).GetProperty("data").GetProperty("feature").GetString().ShouldBe("telehealth");
    }

    [Fact]
    public async Task While_the_portal_is_off_a_patients_sign_in_opens_no_endpoint_that_needs_one()
    {
        _flags.Off(FeatureKeys.PatientPortal);

        var signedInEndpoint = await SendAsync(EndpointWith(), Patient());
        var staffOnSameEndpoint = await SendAsync(EndpointWith(), Staff());
        var anonymousEndpoint = await SendAsync(EndpointWith(new AllowAnonymousAttribute()), Patient());

        signedInEndpoint.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        (await ProblemOf(signedInEndpoint)).GetProperty("data").GetProperty("feature").GetString().ShouldBe("patientPortal");
        staffOnSameEndpoint.StatusCode.ShouldBe(StatusCodes.Status200OK);
        anonymousEndpoint.StatusCode.ShouldBe(StatusCodes.Status200OK, "Signing out, QR checks and SMS links take no sign-in.");
    }

    [Fact]
    public void A_switch_outside_the_catalogue_fails_when_the_endpoint_is_mapped() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new RequiresFeatureMetadata("features.unknown"));

    [Fact]
    public void A_refused_option_of_a_working_request_is_a_422_with_the_same_code()
    {
        var error = FeatureProblems.DisabledError(FeatureCatalog.Telehealth, forPatients: true);

        error.Code.ShouldBe(FeatureProblems.Code);
        error.Type.ShouldBe(SharedKernel.ErrorType.BusinessRule);
        error.Message.ShouldBe(FeatureCatalog.Telehealth.OffDetailForPatients);
    }

    private static Endpoint EndpointWith(params object[] metadata) => new(null, new EndpointMetadataCollection(metadata), "test");

    private static ClaimsPrincipal Staff(Guid? facilityId = null)
    {
        List<Claim> claims = [new(HcClaims.UserId, Guid.NewGuid().ToString()), new(HcClaims.UserType, HcClaims.StaffType)];
        if (facilityId is { } facility)
        {
            claims.Add(new Claim(HcClaims.Facility, facility.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal Patient() => new(new ClaimsIdentity(
        [new(HcClaims.UserId, Guid.NewGuid().ToString()), new(HcClaims.UserType, HcClaims.PatientType)], "test"));

    private async Task<HttpResponse> SendAsync(Endpoint endpoint, ClaimsPrincipal? user = null, Action<HttpContext>? configure = null)
    {
        _reachedHandler = false;
        using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services, User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) };
        http.Response.Body = new MemoryStream();
        http.SetEndpoint(endpoint);
        configure?.Invoke(http);

        var gate = new FeatureGateMiddleware(context =>
        {
            _reachedHandler = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        await gate.InvokeAsync(http, _flags, new HttpCurrentUser(new HttpContextAccessor { HttpContext = http }));
        return http.Response;
    }

    private static async Task<JsonElement> ProblemOf(HttpResponse response)
    {
        response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(response.Body, cancellationToken: TestContext.Current.CancellationToken);
        return document.RootElement.Clone();
    }

    /// <summary>Switches that are on unless turned off here, hospital-wide or for one facility.</summary>
    private sealed class FakeFlags : IFeatureFlags
    {
        private readonly HashSet<(string Feature, Guid? FacilityId)> _off = [];

        public void Off(string feature, Guid? facilityId = null) => _off.Add((feature, facilityId));

        public Task<bool> IsEnabledAsync(string feature, Guid? facilityId, CancellationToken cancellationToken = default) =>
            Task.FromResult(!_off.Contains((feature, null)) && !_off.Contains((feature, facilityId)));
    }
}
