using HyFib.HealthCare360.Modules.Interop.Mapping;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>
/// Makes authorization failures under <c>/fhir</c> FHIR-shaped: a missing or expired token is a 401 and a missing
/// <c>fhir.read</c> permission a 403, each with an OperationOutcome body. The authentication challenge (and its
/// <c>WWW-Authenticate</c> header) still comes from the platform's scheme. Every other path is handed to the handler that
/// was registered before, so the rest of the API is unaffected.
/// </summary>
internal sealed class FhirAuthorizationResultHandler(IAuthorizationMiddlewareResultHandler inner) : IAuthorizationMiddlewareResultHandler
{
    public IAuthorizationMiddlewareResultHandler Inner => inner;

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded || !FhirEndpoints.IsFhirRequest(context.Request.Path) || !(authorizeResult.Challenged || authorizeResult.Forbidden))
        {
            await inner.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        FhirError error;
        if (authorizeResult.Challenged)
        {
            await ForEachScheme(policy, scheme => scheme is null ? context.ChallengeAsync() : context.ChallengeAsync(scheme));
            error = FhirOutcomes.Unauthenticated();
        }
        else
        {
            await ForEachScheme(policy, scheme => scheme is null ? context.ForbidAsync() : context.ForbidAsync(scheme));
            error = FhirOutcomes.NoPermission();
        }

        if (!context.Response.HasStarted)
        {
            await new FhirResult(error.Outcome, error.StatusCode).ExecuteAsync(context);
        }
    }

    /// <summary>
    /// Decorates the handler registered so far (the ASP.NET Core default unless another one was added), so this module
    /// changes only FHIR requests.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        var previous = services.LastOrDefault(d => d.ServiceType == typeof(IAuthorizationMiddlewareResultHandler) && !d.IsKeyedService);
        services.RemoveAll<IAuthorizationMiddlewareResultHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler>(sp => new FhirAuthorizationResultHandler(Resolve(previous, sp)));
    }

    private static IAuthorizationMiddlewareResultHandler Resolve(ServiceDescriptor? descriptor, IServiceProvider services) => descriptor switch
    {
        { ImplementationInstance: IAuthorizationMiddlewareResultHandler instance } => instance,
        { ImplementationFactory: { } factory } => (IAuthorizationMiddlewareResultHandler)factory(services),
        { ImplementationType: { } type } when type != typeof(FhirAuthorizationResultHandler) =>
            (IAuthorizationMiddlewareResultHandler)ActivatorUtilities.CreateInstance(services, type),
        _ => new AuthorizationMiddlewareResultHandler(),
    };

    private static async Task ForEachScheme(AuthorizationPolicy policy, Func<string?, Task> action)
    {
        if (policy.AuthenticationSchemes.Count == 0)
        {
            await action(null);
            return;
        }

        foreach (var scheme in policy.AuthenticationSchemes)
        {
            await action(scheme);
        }
    }
}
