using System.Reflection;
using FluentValidation;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HyFib.HealthCare360.Platform.Modules;

/// <summary>
/// A bounded context (for example Patients or Billing). The Server host composes every module:
/// services, EF Core configurations (discovered from the module assembly), permissions and endpoints.
/// </summary>
public interface IModule
{
    /// <summary>Stable module key; also its database schema name (for example "patients").</summary>
    string Name { get; }

    /// <summary>Permissions this module declares (grouped for the role editor).</summary>
    IEnumerable<PermissionDefinition> Permissions { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

/// <summary>The set of modules loaded into this process.</summary>
public sealed class ModuleCatalog
{
    public ModuleCatalog(IEnumerable<IModule> modules)
    {
        Modules = modules.ToList();
        Assemblies = new[] { typeof(ModuleCatalog).Assembly }
            .Concat(Modules.Select(m => m.GetType().Assembly))
            .Distinct()
            .ToList();
    }

    public IReadOnlyList<IModule> Modules { get; }

    /// <summary>Platform assembly first, then module assemblies (used for EF configuration discovery).</summary>
    public IReadOnlyList<Assembly> Assemblies { get; }
}

public static class ModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the modules and scans their assemblies for validators, seeders, domain-event handlers
    /// and outbox handlers so modules never need to touch shared registration code.
    /// </summary>
    public static ModuleCatalog AddHospitalModules(this IServiceCollection services, IConfiguration configuration, IEnumerable<IModule> modules)
    {
        var catalog = new ModuleCatalog(modules);
        services.AddSingleton(catalog);

        foreach (var assembly in catalog.Assemblies)
        {
            services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped, includeInternalTypes: true);
            RegisterImplementations(services, assembly, typeof(ISeeder));
            RegisterImplementations(services, assembly, typeof(IOutboxHandler));
            RegisterOpenGeneric(services, assembly, typeof(IDomainEventHandler<>));
        }

        foreach (var module in catalog.Modules)
        {
            module.AddServices(services, configuration);
        }

        services.AddSingleton(sp => new PermissionRegistry(
            PlatformPermissions.All.Concat(catalog.Modules.SelectMany(m => m.Permissions))));

        return catalog;
    }

    public static IEndpointRouteBuilder MapHospitalModules(this IEndpointRouteBuilder endpoints)
    {
        var catalog = endpoints.ServiceProvider.GetRequiredService<ModuleCatalog>();
        foreach (var module in catalog.Modules)
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }

    private static void RegisterImplementations(IServiceCollection services, Assembly assembly, Type contract)
    {
        foreach (var type in SafeGetTypes(assembly).Where(t => t is { IsClass: true, IsAbstract: false } && contract.IsAssignableFrom(t)))
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped(contract, type));
        }
    }

    private static void RegisterOpenGeneric(IServiceCollection services, Assembly assembly, Type openGeneric)
    {
        foreach (var type in SafeGetTypes(assembly).Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }))
        {
            foreach (var iface in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric))
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped(iface, type));
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
