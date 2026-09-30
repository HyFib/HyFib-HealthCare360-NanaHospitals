using FluentValidation;
using HyFib.HealthCare360.Platform.Http;
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
}
