using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// What the AI endpoints answer (dictation transcripts, SOAP drafts, interpreted commands) is clinical text HC360 does
/// not store, so none of it may go into the idempotency store, where a retried request's answer is kept for a day.
/// </summary>
public sealed class StoredResponseTests
{
    [Fact]
    public void The_ai_endpoints_answers_are_never_stored()
    {
        var writes = ((IEndpointRouteBuilder)EndpointAuthorizationTests.App.Value).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/ai/", StringComparison.Ordinal) == true
                        && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .ToList();

        writes.Count.ShouldBeGreaterThanOrEqualTo(3, "the assistant command, the SOAP draft and the transcription");
        writes.Where(e => e.Metadata.GetMetadata<NoStoredResponseMetadata>() is null).Select(e => e.RoutePattern.RawText)
            .ShouldBeEmpty("Mark these with .WithoutStoredResponse()");
    }
}
