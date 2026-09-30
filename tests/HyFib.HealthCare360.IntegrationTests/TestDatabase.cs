using DotNet.Testcontainers.Configurations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A new, empty PostgreSQL database for one test run.
/// <list type="bullet">
/// <item>With <see cref="ServerVariable"/> set to a connection string for a server the tests may create databases on (CI
/// uses the runner's own PostgreSQL), a database is created there and dropped afterwards.</item>
/// <item>Otherwise a throwaway PostgreSQL container named <c>hc360-it-…</c> is started with Docker and removed afterwards.
/// Only that container is ever created or removed: the developer machine's Docker also runs other projects.</item>
/// </list>
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    public const string ServerVariable = "HC360_TEST_DATABASE";

    private readonly PostgreSqlContainer? _container;
    private readonly string? _serverConnectionString;
    private readonly string? _databaseName;

    private TestDatabase(string connectionString, PostgreSqlContainer? container, string? serverConnectionString, string? databaseName)
    {
        ConnectionString = connectionString;
        _container = container;
        _serverConnectionString = serverConnectionString;
        _databaseName = databaseName;
    }

    public string ConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } server)
        {
            var name = "hc360_it_" + Guid.NewGuid().ToString("N");
            await using (var connection = new NpgsqlConnection(server))
            {
                await connection.OpenAsync(cancellationToken);
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" ENCODING 'UTF8'", connection);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            var database = new NpgsqlConnectionStringBuilder(server) { Database = name, Pooling = true };
            return new TestDatabase(database.ConnectionString, null, server, name);
        }

        // No resource-reaper container: this run creates, and disposes, only its own hc360-it container.
        TestcontainersSettings.ResourceReaperEnabled = false;
        var container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithName("hc360-it-" + Guid.NewGuid().ToString("N")[..12])
            .WithDatabase("hc360")
            .Build();
        await container.StartAsync(cancellationToken);
        return new TestDatabase(container.GetConnectionString(), container, null, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
