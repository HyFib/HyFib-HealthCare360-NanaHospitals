using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// An endpoint that asks for its request to be validated (WithValidation) has a validator for it. Without one the
/// endpoint would refuse every request, and before that change it let requests through unchecked.
/// </summary>
public sealed class RequestValidationTests
{
    [Fact]
    public void Every_validated_request_has_a_validator()
    {
        var app = EndpointAuthorizationTests.App.Value;
        using var scope = app.Services.CreateScope();

        var missing = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<ValidatedRequest>())
            .Select(validated => validated.RequestType)
            .Distinct()
            .Where(type => scope.ServiceProvider.GetService(typeof(IValidator<>).MakeGenericType(type)) is null)
            .Select(type => type.FullName!)
            .Order()
            .ToList();

        missing.ShouldBeEmpty("These requests are marked WithValidation<T>() but have no validator:\n" + string.Join('\n', missing));
    }

    /// <summary>
    /// SRS SEC-06 (ASVS V5.1.3): all input is validated on the server. Every write (POST, PUT, PATCH, DELETE) that reads a
    /// body checks it before the handler acts on it:
    /// <list type="bullet">
    /// <item>A request record is validated with WithValidation, so a bad field gets a 400 that names it.</item>
    /// <item>A file or raw JSON, which its handler reads in its own way (a file's type from its first bytes, a device's
    /// readings by their format), has a size limit of its own, so a body too large is refused before it is read.</item>
    /// <item>A record of yes/no answers only has nothing a bad value could be once it is read.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Every_write_body_is_validated()
    {
        var unguarded = ((IEndpointRouteBuilder)EndpointAuthorizationTests.App.Value).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => (
                Methods: e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [],
                Route: e.RoutePattern.RawText,
                Body: e.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType,
                Validated: e.Metadata.GetOrderedMetadata<ValidatedRequest>().Select(v => v.RequestType).ToHashSet(),
                SizeLimit: e.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize))
            .Where(e => e.Methods.Any(m => m is "POST" or "PUT" or "PATCH" or "DELETE") && e.Body is not null)
            .Where(e => IsRaw(e.Body!) ? e.SizeLimit is null : !e.Validated.Contains(e.Body!) && !OnlyYesOrNo(e.Body!))
            .Select(e => $"{string.Join(',', e.Methods)} {e.Route} ({e.Body!.Name})")
            .Order()
            .ToList();

        unguarded.ShouldBeEmpty($"{unguarded.Count} writes read a body they do not check. Add a validator and .WithValidation<T>(), or, for a "
            + "file or raw JSON, a RequestSizeLimit:\n" + string.Join('\n', unguarded));
    }

    private static bool IsRaw(Type body) =>
        body == typeof(IFormFile) || body == typeof(IFormFileCollection) || body == typeof(IFormCollection) || body == typeof(JsonElement)
        || body == typeof(JsonNode) || typeof(Stream).IsAssignableFrom(body) || body == typeof(PipeReader);

    private static bool OnlyYesOrNo(Type body) =>
        body.GetProperties() is { Length: > 0 } properties && properties.All(p => p.PropertyType == typeof(bool));
}
