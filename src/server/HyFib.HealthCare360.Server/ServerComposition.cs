using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
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

        var signalR = builder.Services.AddSignalR()
            .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        if (builder.Configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            // Scale-out (SCAL-02): several API nodes share real-time groups through Redis.
            signalR.AddStackExchangeRedis(redis, redisOptions => redisOptions.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("hc360"));
        }
    }

    /// <summary>Authentication, authorization and every API endpoint, after the host's own middleware.</summary>
    public static void UseServerApplication(this WebApplication app)
    {
        app.UseHospitalPlatform();
        app.MapHospitalPlatform();

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
