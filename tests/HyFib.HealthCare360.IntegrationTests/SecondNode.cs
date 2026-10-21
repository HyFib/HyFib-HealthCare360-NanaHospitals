using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A second HC360 server on the test server's database, as a reverse proxy would have it beside the first (SCAL-01): a
/// memory of its own (permission and session caches), its own listener for the cache signals, the same database. It has
/// only the services the scale-out tests look at, made as the platform registers them.
/// </summary>
internal sealed class SecondNode : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private SecondNode(ServiceProvider services) => _services = services;

    public IServiceProvider Services => _services;

    public IPermissionService Permissions => _services.GetRequiredService<IPermissionService>();

    public ISessionValidator Sessions => _services.GetRequiredService<ISessionValidator>();

    public IClusterSignals Signals => _services.GetRequiredService<IClusterSignals>();

    /// <summary>
    /// Starts the node and waits until it hears the cache signals, as a node does a moment after it starts.
    /// <paramref name="configure"/> may add services after the node's own, such as a cache that records the signals heard.
    /// </summary>
    public static async Task<SecondNode> StartAsync(HospitalServer server, CancellationToken ct, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var configuration = server.Services.GetRequiredService<IConfiguration>();
        var connection = configuration.GetConnectionString("Hospital")!;
        var catalog = server.Services.GetRequiredService<ModuleCatalog>();
        var options = new DbContextOptionsBuilder<HospitalDbContext>().UseNpgsql(connection).UseSnakeCaseNamingConvention().Options;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new DatabaseCredentials(connection));
        services.AddScoped(_ => new HospitalDbContext(options, catalog));
        services.AddSingleton<ClusterSignals>();
        services.AddSingleton<IClusterSignals>(sp => sp.GetRequiredService<ClusterSignals>());
        services.AddSingleton<PermissionService>();
        services.AddSingleton<IPermissionService>(sp => sp.GetRequiredService<PermissionService>());
        services.AddSingleton<IClusterCache>(sp => sp.GetRequiredService<PermissionService>());
        services.AddSingleton<SessionValidator>();
        services.AddSingleton<ISessionValidator>(sp => sp.GetRequiredService<SessionValidator>());
        services.AddSingleton<IClusterCache>(sp => sp.GetRequiredService<SessionValidator>());
        services.AddSingleton<IClusterStore, ClusterStore>();
        configure?.Invoke(services);

        var node = new SecondNode(services.BuildServiceProvider());
        await node._services.GetRequiredService<ClusterSignals>().StartAsync(ct);
        await WaitUntilListeningAsync(node.Signals, ct);
        await WaitUntilListeningAsync(server.Services.GetRequiredService<IClusterSignals>(), ct);
        return node;
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, for at most <paramref name="timeout"/>; whether it did.</summary>
    public static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var until = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (await condition())
            {
                return true;
            }

            if (DateTimeOffset.UtcNow > until)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.GetRequiredService<ClusterSignals>().StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
    }

    private static async Task WaitUntilListeningAsync(IClusterSignals signals, CancellationToken ct)
    {
        if (!await EventuallyAsync(() => Task.FromResult(signals.IsListening), TimeSpan.FromSeconds(30), ct))
        {
            throw new TimeoutException("The node did not start hearing the cache signals.");
        }
    }
}
