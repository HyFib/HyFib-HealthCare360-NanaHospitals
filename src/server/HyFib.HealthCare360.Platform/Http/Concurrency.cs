using System.Reflection;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>
/// SRS REL-04: a record someone else changed is never silently overwritten. Every edit sends the version (xmin) of the
/// record as it was loaded, and is refused (409) when the record has moved on since. The architecture tests check that
/// every PUT and PATCH does one of the following.
/// </summary>
public static class ConcurrencyExtensions
{
    public const string VersionMissing =
        "Reload the record and make the change again. The change did not say which version of the record it was made to, so it could have overwritten someone else's.";

    /// <summary>
    /// An edit whose request it shares with the create (a <c>uint? Version</c> the create leaves empty) must send the
    /// version: without it, the edit is refused (400, field <c>version</c>) before the handler runs.
    /// </summary>
    public static RouteHandlerBuilder RequireVersion(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(VersionRequired.Instance).AddEndpointFilterFactory((factory, next) =>
        {
            var parameters = factory.MethodInfo.GetParameters();
            var index = Array.FindIndex(parameters, p => VersionRequired.Of(p.ParameterType) is not null);
            if (index < 0)
            {
                throw new InvalidOperationException($"{factory.MethodInfo} has no request with a Version to require.");
            }

            var version = VersionRequired.Of(parameters[index].ParameterType)!;
            return context => context.Arguments[index] is { } request && version.GetValue(request) is null
                ? ValueTask.FromResult<object?>(Error.Field("version", VersionMissing).ToProblem())
                : next(context);
        });

    /// <summary>
    /// An edit whose first save creates the record (a setting, a study's report, a booking's operation note) sends no
    /// version on that save, and the version it loaded on every later one. Its handler checks the version with
    /// <see cref="Persistence.QueryExtensions.SetExpectedVersion{T}(Microsoft.EntityFrameworkCore.DbContext, T, uint?)"/>:
    /// a save without one, over a record that exists, is refused as stale, because someone else created the record meanwhile.
    /// </summary>
    public static RouteHandlerBuilder FirstSaveCreates(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(CreatedByFirstSave.Instance);
}

/// <summary>Marks an edit that refuses a request without its version (<see cref="ConcurrencyExtensions.RequireVersion"/>).</summary>
public sealed class VersionRequired
{
    private VersionRequired()
    {
    }

    public static VersionRequired Instance { get; } = new();

    /// <summary>The optional <c>Version</c> of a request type, if it has one.</summary>
    public static PropertyInfo? Of(Type requestType) =>
        requestType.GetProperty("Version") is { } property && property.PropertyType == typeof(uint?) ? property : null;
}

/// <summary>Marks an edit whose first save creates the record (<see cref="ConcurrencyExtensions.FirstSaveCreates"/>).</summary>
public sealed class CreatedByFirstSave
{
    private CreatedByFirstSave()
    {
    }

    public static CreatedByFirstSave Instance { get; } = new();
}
