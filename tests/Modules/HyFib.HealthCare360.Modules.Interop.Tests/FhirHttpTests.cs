using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class FhirOutcomeTests
{
    [Theory]
    [InlineData(nameof(SharingDecision.NoConsent), 403, FhirOutcomes.ConsentRequired)]
    [InlineData(nameof(SharingDecision.Restricted), 403, FhirOutcomes.RestrictedRecord)]
    [InlineData(nameof(SharingDecision.Merged), 410, FhirOutcomes.PatientMerged)]
    [InlineData(nameof(SharingDecision.Deleted), 410, FhirOutcomes.Deleted)]
    [InlineData(nameof(SharingDecision.NotFound), 404, FhirOutcomes.NotFound)]
    public void Refuses_records_that_may_not_be_shared_with_the_right_status(string decision, int status, string reason)
    {
        var error = FhirOutcomes.Denied(Enum.Parse<SharingDecision>(decision), "Observation", "lab-1");

        error.StatusCode.ShouldBe(status);
        error.Reason.ShouldBe(reason);
        var issue = error.Outcome.Issue.ShouldHaveSingleItem();
        issue.Severity.ShouldBe(R4.OperationOutcome.IssueSeverity.Error);
        issue.Details.Coding[0].System.ShouldBe("urn:hc360:fhir-issue");
        issue.Diagnostics.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_merged_record_points_to_the_surviving_record_only_when_given() =>
        FhirOutcomes.Denied(SharingDecision.Merged, "Patient", "x", TestData.OtherPatientId).Outcome.Issue[0].Diagnostics
            .ShouldContain($"Patient/{TestData.OtherPatientId:D}");

    [Fact]
    public void Consent_refusals_are_forbidden_issues() =>
        FhirOutcomes.Denied(SharingDecision.NoConsent, "Patient", "x").Outcome.Issue[0].Code.ShouldBe(R4.OperationOutcome.IssueType.Forbidden);

    [Fact]
    public void Unknown_types_are_404_and_list_the_served_types()
    {
        var error = FhirOutcomes.UnknownType("Practitioner");

        error.StatusCode.ShouldBe(404);
        error.Outcome.Issue[0].Diagnostics.ShouldContain("DocumentReference");
    }

    [Fact]
    public void Echoed_values_are_bounded_and_printable() =>
        FhirOutcomes.Printable(new string('x', 200) + "\n").Length.ShouldBe(65);

    [Theory]
    [InlineData(401, "login")]
    [InlineData(403, "forbidden")]
    public void Authorization_failures_have_fhir_issue_types(int status, string code)
    {
        var error = status == 401 ? FhirOutcomes.Unauthenticated() : FhirOutcomes.NoPermission();

        error.StatusCode.ShouldBe(status);
        FhirJson.Serialize(error.Outcome).ShouldContain($"\"code\":\"{code}\"");
    }
}

public sealed class CapabilityStatementTests
{
    private static readonly R4.CapabilityStatement Statement = CapabilityStatementBuilder.Build("https://hc360.example/fhir/r4", "1.0.0", TestData.Now);

    [Fact]
    public void Describes_a_read_only_fhir_4_0_1_json_server()
    {
        Statement.FhirVersion.ShouldBe(R4.FHIRVersion.N4_0_1);
        Statement.Kind.ShouldBe(R4.CapabilityStatementKind.Instance);
        Statement.Format.ShouldContain("application/fhir+json");
        Statement.Implementation.Url.ShouldBe("https://hc360.example/fhir/r4");
        Statement.Date.ShouldBe("2026-09-25");
        var rest = Statement.Rest.ShouldHaveSingleItem();
        rest.Mode.ShouldBe(R4.CapabilityStatement.RestfulCapabilityMode.Server);
        rest.Security.Description.ShouldContain("fhir.read");
    }

    [Fact]
    public void Lists_the_eight_ehr05_resources_with_read_and_search()
    {
        var resources = Statement.Rest[0].Resource;

        resources.Select(r => r.Type).ShouldBe(FhirResourceTypes.Names);
        resources.ShouldAllBe(r => r.Interaction.Select(i => i.Code).SequenceEqual(new R4.CapabilityStatement.TypeRestfulInteraction?[]
        {
            R4.CapabilityStatement.TypeRestfulInteraction.Read,
            R4.CapabilityStatement.TypeRestfulInteraction.SearchType,
        }));
        resources.ShouldAllBe(r => r.ReadHistory == false && r.Versioning == R4.CapabilityStatement.ResourceVersionPolicy.NoVersion);
    }

    [Fact]
    public void Publishes_exactly_the_parameters_the_parser_accepts()
    {
        foreach (var resource in Statement.Rest[0].Resource)
        {
            var definition = SearchDefinitions.Find(resource.Type).ShouldNotBeNull();
            resource.SearchParam.Select(p => p.Name).ShouldBe(definition.Parameters.Select(p => p.Name));
            resource.SearchParam.ShouldAllBe(p => p.Documentation.Length > 0);
        }
    }

    [Theory]
    [InlineData("Patient", "identifier")]
    [InlineData("Observation", "category")]
    [InlineData("Observation", "code")]
    [InlineData("Encounter", "date")]
    [InlineData("MedicationRequest", "status")]
    [InlineData("DiagnosticReport", "_lastUpdated")]
    [InlineData("Condition", "patient")]
    [InlineData("DocumentReference", "subject")]
    public void Supports_the_required_search_parameters(string type, string parameter) =>
        SearchDefinitions.Find(type)!.Find(parameter).ShouldNotBeNull();

    [Fact]
    public void Serialises_as_valid_fhir_json() =>
        JsonSerializer.Deserialize<R4.CapabilityStatement>(FhirJson.Serialize(Statement), FhirJson.Options(false))!.Rest[0].Resource.Count.ShouldBe(8);
}

public sealed class FhirResultTests
{
    [Fact]
    public async Task Writes_fhir_json_that_is_never_cached()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var patient = PatientMapper.Map(TestData.Patient(), TestData.Directory);

        await new FhirResult(patient, StatusCodes.Status200OK).ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(200);
        context.Response.ContentType.ShouldBe("application/fhir+json; charset=utf-8");
        context.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        var json = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        var parsed = JsonSerializer.Deserialize<R4.Patient>(json, FhirJson.Options(false))!;
        parsed.Identifier[0].Value.ShouldBe("NH-UH-2600123");
        parsed.BirthDate.ShouldBe("1984-04-12");
    }

    [Fact]
    public void Every_mapped_resource_round_trips_through_the_fhir_parser()
    {
        R4.Resource[] resources =
        [
            PatientMapper.Map(TestData.Patient() with { AbhaNumber = "91123456789012", IsDeceased = true }, TestData.Directory),
            VitalSignObservations.Map(new VitalSignsRow { Id = Guid.CreateVersion7(), PatientId = TestData.PatientId, RecordedAt = TestData.Now, SystolicBp = 120, CreatedAt = TestData.Now })[0],
            FhirOutcomes.Withheld(),
        ];

        foreach (var resource in resources)
        {
            var parsed = JsonSerializer.Deserialize<R4.Resource>(FhirJson.Serialize(resource), FhirJson.Options(false))!;
            parsed.TypeName.ShouldBe(resource.TypeName);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("application/fhir+json", true)]
    [InlineData("application/json, text/plain", true)]
    [InlineData("*/*", true)]
    [InlineData("application/fhir+xml", false)]
    [InlineData("text/html", false)]
    public void Negotiates_json_only(string? accept, bool accepted) =>
        FhirEndpoints.AcceptsJson(accept is null ? default : new Microsoft.Extensions.Primitives.StringValues(accept)).ShouldBe(accepted);

    [Theory]
    [InlineData("/fhir/r4/Patient", true)]
    [InlineData("/fhir/r4/metadata", true)]
    [InlineData("/api/v1/patients", false)]
    [InlineData("/fhirx", false)]
    public void Recognises_fhir_requests(string path, bool fhir) => FhirEndpoints.IsFhirRequest(new PathString(path)).ShouldBe(fhir);
}

public sealed class FhirAuthorizationResultHandlerTests
{
    private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    private static DefaultHttpContext Context(string path)
    {
        var services = new ServiceCollection().AddSingleton<IAuthenticationService, FakeAuthentication>().BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services, Request = { Path = path }, Response = { Body = new MemoryStream() } };
    }

    private static string Body(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    [Fact]
    public async Task A_missing_token_on_a_fhir_route_is_a_401_operation_outcome_with_the_challenge()
    {
        var inner = new RecordingHandler();
        var context = Context("/fhir/r4/Patient");

        await new FhirAuthorizationResultHandler(inner).HandleAsync(_ => Task.CompletedTask, context, Policy, PolicyAuthorizationResult.Challenge());

        inner.Called.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(401);
        context.Response.Headers.WWWAuthenticate.ToString().ShouldBe("Bearer");
        context.Response.ContentType.ShouldBe("application/fhir+json; charset=utf-8");
        Body(context).ShouldContain("\"resourceType\":\"OperationOutcome\"");
        Body(context).ShouldContain("authentication-required");
    }

    [Fact]
    public async Task A_missing_permission_on_a_fhir_route_is_a_403_operation_outcome()
    {
        var context = Context("/fhir/r4/Observation");

        await new FhirAuthorizationResultHandler(new RecordingHandler())
            .HandleAsync(_ => Task.CompletedTask, context, Policy, PolicyAuthorizationResult.Forbid());

        context.Response.StatusCode.ShouldBe(403);
        Body(context).ShouldContain("permission-required");
    }

    [Fact]
    public async Task Other_routes_keep_the_platform_behaviour()
    {
        var inner = new RecordingHandler();

        await new FhirAuthorizationResultHandler(inner)
            .HandleAsync(_ => Task.CompletedTask, Context("/api/v1/patients"), Policy, PolicyAuthorizationResult.Challenge());

        inner.Called.ShouldBeTrue();
    }

    [Fact]
    public async Task Authorised_fhir_requests_continue()
    {
        var inner = new RecordingHandler();

        await new FhirAuthorizationResultHandler(inner)
            .HandleAsync(_ => Task.CompletedTask, Context("/fhir/r4/Patient"), Policy, PolicyAuthorizationResult.Success());

        inner.Called.ShouldBeTrue();
    }

    private sealed class RecordingHandler : IAuthorizationMiddlewareResultHandler
    {
        public bool Called { get; private set; }

        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Behaves like the JWT bearer scheme: a challenge is a 401 with WWW-Authenticate, a forbid a 403.</summary>
    private sealed class FakeAuthentication : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Task.CompletedTask;
        }

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
