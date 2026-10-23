using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// SRS SEC-08: the Administration permissions (<see cref="PlatformPermissions.IsAdministrative"/>: users and roles,
/// settings, devices, backups, integrations, the audit trail, the server's own credentials) work only in a session signed
/// in on an enrolled HC360 Desktop, whose device credential the server checked at sign-in and whose access tokens carry the
/// device (<see cref="HcClaims.Device"/>). Revoking the desktop ends its sessions at once. A browser keeps everything else
/// its user holds. While no workstation or tablet is enrolled, a browser may still list the devices and make an enrolment
/// code (<see cref="FirstDesktopEnrolment"/>), so that a new installation, or one whose only desktops are kiosks and queue
/// displays, can enrol the first one. The <see cref="SettingKey"/> setting turns the rule off or on; without it, the
/// server's settings file decides (<see cref="SecurityOptions.AdministrationRequiresDesktop"/>, on).
/// </summary>
public static class AdministrationOnDesktop
{
    public const string SettingKey = "security.administrationRequiresDesktop";

    public const string ErrorCode = "auth.device_required";

    public const string Message =
        "Administration works only in HC360 Desktop on a computer IT has enrolled. Sign in there, or ask IT to enrol this computer.";

    /// <summary>
    /// Why <paramref name="value"/> cannot be saved as <paramref name="key"/>, or null (and for any other key): the setting
    /// is true or false, as it is read, for the whole hospital; clearing it (null) leaves the settings file to decide.
    /// </summary>
    public static string? Problem(string key, JsonNode? value, Guid? facilityId) => key switch
    {
        SettingKey when facilityId is not null => "This setting applies to the whole hospital: save it for all facilities.",
        SettingKey when value is not null
            && !(value is JsonValue v && (v.TryGetValue<bool>(out _) || (v.TryGetValue<string>(out var text) && bool.TryParse(text.Trim(), out _)))) =>
            "Enter true or false.",
        _ => null,
    };

    /// <summary>The enrolled HC360 Desktop the session was signed in on, if any (<see cref="IAdministrationDesktops.AdministersFromAsync"/> says whether it may administer).</summary>
    public static Guid? DeviceOf(ClaimsPrincipal user) => Guid.TryParse(user.FindFirst(HcClaims.Device)?.Value, out var id) ? id : null;

    /// <summary>The kinds of desktop someone administers the hospital from: not a shared kiosk or a queue display.</summary>
    public static bool Administers(DeviceKind kind) => kind is DeviceKind.Workstation or DeviceKind.Tablet;

    /// <summary>
    /// What a session that is not on an enrolled desktop may use of what its user holds: every permission but the
    /// administrative ones (all-access stands for each of the others), and, during the first enrolment, managing devices.
    /// </summary>
    public static IReadOnlySet<string> Elsewhere(IReadOnlySet<string> granted, PermissionRegistry registry, bool firstEnrolment)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(registry);
        var held = granted.Contains(PermissionRegistry.Wildcard) ? registry.All.Select(p => p.Name) : granted;
        return held.Where(p => !PlatformPermissions.IsAdministrative(p) || (firstEnrolment && p == PlatformPermissions.DevicesManage))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Lets a browser call the endpoint while no workstation or tablet is enrolled (see <see cref="AdministrationOnDesktop"/>).</summary>
    public static TBuilder AllowedForFirstDesktopEnrolment<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(FirstDesktopEnrolment.Instance);
}

/// <summary>Endpoint metadata: a browser may call the endpoint while the hospital enrols its first desktop.</summary>
public sealed class FirstDesktopEnrolment
{
    public static readonly FirstDesktopEnrolment Instance = new();

    private FirstDesktopEnrolment()
    {
    }

    /// <summary>The request is for an endpoint marked so.</summary>
    public static bool Marks(object? resource) =>
        resource is HttpContext http && http.GetEndpoint()?.Metadata.GetMetadata<FirstDesktopEnrolment>() is not null;
}

/// <summary>Whether <see cref="AdministrationOnDesktop"/> applies, and whether the first desktop is enrolled.</summary>
public interface IAdministrationDesktops
{
    /// <summary>The rule is on: as the <see cref="AdministrationOnDesktop.SettingKey"/> setting says, else the settings file.</summary>
    ValueTask<bool> RequiredAsync(CancellationToken cancellationToken = default);

    /// <summary>A workstation or tablet is enrolled, and not revoked: the first enrolment is over.</summary>
    ValueTask<bool> EnrolledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The session's device (<see cref="AdministrationOnDesktop.DeviceOf"/>) is a workstation or tablet, where its user may
    /// administer; a kiosk or queue display signs staff in too, but nobody administers from one.
    /// </summary>
    ValueTask<bool> AdministersFromAsync(Guid? deviceId, CancellationToken cancellationToken = default);
}

internal sealed class AdministrationDesktops(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options) : IAdministrationDesktops
{
    // A device's kind is set when it is enrolled and never changes; revoking it ends its sessions (SessionValidator).
    private readonly ConcurrentDictionary<Guid, bool> _administers = new();

    public async ValueTask<bool> RequiredAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var setting = await scope.ServiceProvider.GetRequiredService<ISettingsService>()
            .GetAsync<JsonElement?>(AdministrationOnDesktop.SettingKey, cancellationToken: cancellationToken);
        return setting switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            { ValueKind: JsonValueKind.String } text when bool.TryParse(text.GetString()?.Trim(), out var on) => on,
            _ => options.Value.Security.AdministrationRequiresDesktop,
        };
    }

    // Kiosks and queue displays are desktops too, but nobody administers the hospital from one.
    public async ValueTask<bool> EnrolledAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Device>()
            .AnyAsync(d => !d.IsRevoked && (d.Kind == DeviceKind.Workstation || d.Kind == DeviceKind.Tablet), cancellationToken);
    }

    public async ValueTask<bool> AdministersFromAsync(Guid? deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId is not { } id)
        {
            return false;
        }

        if (_administers.TryGetValue(id, out var administers))
        {
            return administers;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var kind = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Device>()
            .Where(d => d.Id == id).Select(d => (DeviceKind?)d.Kind).FirstOrDefaultAsync(cancellationToken);
        if (kind is null)
        {
            return false;
        }

        return _administers[id] = AdministrationOnDesktop.Administers(kind.Value);
    }
}

/// <summary>Why a request was refused: only an administrative permission allows it, and it did not come from an enrolled desktop.</summary>
public sealed class DesktopRequired(IAuthorizationHandler handler) : AuthorizationFailureReason(handler, AdministrationOnDesktop.Message);

/// <summary>
/// Answers a request refused for <see cref="DesktopRequired"/> with a 403 that says why (<see cref="AdministrationOnDesktop.ErrorCode"/>),
/// which the staff app shows, rather than a bare one. Every result goes to the handler registered before first.
/// </summary>
internal sealed class DesktopRequiredResultHandler(IAuthorizationMiddlewareResultHandler inner) : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        await inner.HandleAsync(next, context, policy, authorizeResult);
        if (authorizeResult.Forbidden && !context.Response.HasStarted
            && authorizeResult.AuthorizationFailure?.FailureReasons.OfType<DesktopRequired>().Any() == true)
        {
            await Error.Forbidden(AdministrationOnDesktop.ErrorCode, AdministrationOnDesktop.Message).ToProblem().ExecuteAsync(context);
        }
    }

    /// <summary>Decorates the handler registered so far (the ASP.NET Core default unless another one was added).</summary>
    public static void Register(IServiceCollection services)
    {
        var previous = services.LastOrDefault(d => d.ServiceType == typeof(IAuthorizationMiddlewareResultHandler) && !d.IsKeyedService);
        services.RemoveAll<IAuthorizationMiddlewareResultHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler>(sp => new DesktopRequiredResultHandler(previous switch
        {
            { ImplementationInstance: IAuthorizationMiddlewareResultHandler instance } => instance,
            { ImplementationFactory: { } factory } => (IAuthorizationMiddlewareResultHandler)factory(sp),
            { ImplementationType: { } type } when type != typeof(DesktopRequiredResultHandler) =>
                (IAuthorizationMiddlewareResultHandler)ActivatorUtilities.CreateInstance(sp, type),
            _ => new AuthorizationMiddlewareResultHandler(),
        }));
    }
}
