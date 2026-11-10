using HyFib.HealthCare360.Platform.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// The PostgreSQL account that owns the hospital's schema (<c>hc360_owner</c>), apart from the one the server works as
/// (<c>hc360_app</c>, <c>ConnectionStrings:Hospital</c>), where the installer has made them two
/// (<c>ConnectionStrings:HospitalOwner</c>; docs/deployment/DEPLOYMENT.md, "Two database accounts"). The working account may
/// read and write rows, and nothing else: it cannot change a table, nor drop or turn off the audit trail's triggers, so
/// neither a flaw in a request's SQL nor someone holding its password can make the audit trail editable (ASVS V1.4.3,
/// V8.3.7). The owner changes the schema: start-up's migrations and the audit trail's triggers
/// (<see cref="DatabaseInitializer"/>), and the jobs that add monthly partitions and their indexes. Each opens its own
/// connection as the owner, and closes it when done.
/// <para>
/// Without <c>ConnectionStrings:HospitalOwner</c> (a server installed before the split, or a development machine), the one
/// account does everything, as before: <see cref="CreateContext"/> is null and the caller uses its own context.
/// </para>
/// </summary>
public sealed class DatabaseOwner(DbContextOptions<HospitalDbContext>? options, ModuleCatalog modules)
{
    public const string ConnectionName = "HospitalOwner";

    /// <summary>Whether the schema has an owner of its own, apart from the account the server works as.</summary>
    public bool IsSeparate => options is not null;

    /// <summary>A context signed in as the owner, for the caller to dispose; null when there is no separate owner.</summary>
    public HospitalDbContext? CreateContext() => options is null ? null : new HospitalDbContext(options, modules);

    /// <summary>
    /// A <typeparamref name="T"/> of <paramref name="services"/> that works with a context signed in as the owner, given as
    /// <paramref name="context"/> for the caller to dispose; the scope's own <typeparamref name="T"/>, and no context, where
    /// there is no separate owner. For the jobs that change the schema: monthly partitions and their indexes.
    /// </summary>
    public T Create<T>(IServiceProvider services, out HospitalDbContext? context)
        where T : notnull
    {
        context = CreateContext();
        return context is null ? services.GetRequiredService<T>() : ActivatorUtilities.CreateInstance<T>(services, context);
    }
}
