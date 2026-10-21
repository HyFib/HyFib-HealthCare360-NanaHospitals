using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.Extensions.Options;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Npgsql;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// The server without its host: the platform and every module, the API's JSON conventions, SignalR, the endpoints and
/// bringing the database up to date. Program.cs puts the Windows-service host around it (TLS, logging, static files,
/// compression); the integration tests run exactly this on an in-memory test server.
/// </summary>
public static class ServerComposition
{
    public static void AddServerApplication(this WebApplicationBuilder builder, LanCertificateManager certificates)
    {
        builder.Services.AddHospitalPlatform(
            builder.Configuration,
            builder.Environment,
            ServerModules.All,
            typeof(ServerModules).Assembly.GetName().Name!,
            certificates);

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });

        AddRealtime(builder.Services, builder.Configuration);
    }

    /// <summary>
    /// SignalR, and with <c>ConnectionStrings:Redis</c> the Redis backplane (SCAL-02): several API nodes, and the node that
    /// runs the jobs, share real-time groups through Redis, so an event raised on one node reaches clients connected to any
    /// other. A Redis that is down when a node starts, or goes away later, is connected to again in the background: the API
    /// keeps working, and real-time updates reach only the clients of the node that raised them meanwhile.
    /// </summary>
    internal static Microsoft.AspNetCore.SignalR.ISignalRServerBuilder AddRealtime(IServiceCollection services, IConfiguration configuration)
    {
        var signalR = services.AddSignalR()
            .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        if (configuration.GetConnectionString(RedisBackplane.ConnectionName) is { Length: > 0 } redis)
        {
            signalR.AddStackExchangeRedis(redis, redisOptions =>
            {
                redisOptions.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal(RedisBackplane.ChannelPrefix);
                redisOptions.Configuration.AbortOnConnectFail = false;

                // While Redis is away, a publish fails at once and is logged (REL-03). The default backlog would hold it, and
                // the request that raised the event, for the client's whole timeout (5 s), on every write that raises one.
                redisOptions.Configuration.BacklogPolicy = StackExchange.Redis.BacklogPolicy.FailFast;
            });

            // The backplane is given its connection only once it is connected: it subscribes to what the nodes share once,
            // so a connection given while Redis was away would leave the node deaf to the others for good.
            services.AddSingleton<RedisBackplaneConnections>();
            services.AddOptions<RedisOptions>().Configure<RedisBackplaneConnections>((redisOptions, connections) =>
                redisOptions.ConnectionFactory = log => connections.ConnectAsync(redisOptions.Configuration, log));
            services.AddSingleton<RedisBackplane>();
            services.AddHealthChecks().AddCheck<RedisBackplane>(
                "redis-backplane", failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, tags: ["ready"]);
        }

        return signalR;
    }

    /// <summary>
    /// Authentication, authorization and every API endpoint, after the host's own middleware. A worker node
    /// (<c>--role worker</c>, SCAL-05) runs the background jobs only: it answers its health checks and nothing else.
    /// </summary>
    public static void UseServerApplication(this WebApplication app)
    {
        app.UseHospitalPlatform();
        var platform = app.Services.GetRequiredService<IOptions<PlatformOptions>>().Value;
        if (platform.ServesApi)
        {
            app.MapHospitalPlatform();
        }

        // A node that does only part of the work (SCAL-05) sends real-time updates to clients connected to another node, or
        // receives them from it, only through the Redis backplane (SCAL-02): the lab results the worker receives, the bed
        // board changes an API node makes for the clients of another.
        if (!string.Equals(platform.Role, "all", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(app.Configuration.GetConnectionString(RedisBackplane.ConnectionName)))
        {
            app.Logger.LogWarning(
                "This server runs as '{Role}' without ConnectionStrings:Redis: real-time updates raised on another HC360 server will not reach its clients, nor this server's reach theirs",
                platform.Role);
        }

        app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

        // Unknown API routes must be a 404, never the SPA shell.
        app.Map("/api/{**rest}", () => Results.Problem(title: "Not found", statusCode: StatusCodes.Status404NotFound)).AllowAnonymous();
    }

    /// <summary>
    /// Applies migrations and seeds. PostgreSQL may still be starting when the service boots after a power cut, so
    /// connection problems are retried for up to about 3 minutes. An error PostgreSQL reports about the schema or the data
    /// (for example a table that already exists) happens again on every attempt, so it stops start-up at once.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        var initializer = app.Services.GetRequiredService<DatabaseInitializer>();
        var logger = app.Services.GetRequiredService<ILogger<DatabaseInitializer>>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await initializer.InitializeAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < 12 && IsTransientDatabaseError(ex))
            {
                logger.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying in 15 seconds", attempt);
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (DatabaseUpgradeException ex)
            {
                logger.LogCritical(ex, "HC360 cannot start: {Reason}", ex.Message);
                throw;
            }
        }
    }

    private static bool IsTransientDatabaseError(Exception ex) => ex switch
    {
        TimeoutException => true,
        NpgsqlException npgsql => npgsql.IsTransient,
        InvalidOperationException { InnerException: NpgsqlException inner } => inner.IsTransient,
        _ => false,
    };
}
