using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>Whether Administration can change the database password, and when it was last changed and by whom.</summary>
public sealed record DatabasePasswordStatus(string Username, bool CanChange, string? Reason, DateTimeOffset? LastChangedAt, string? LastChangedBy);

/// <summary>A changed password: when, and which settings of the settings file now hold it.</summary>
public sealed record DatabasePasswordChanged(DateTimeOffset ChangedAt, IReadOnlyList<string> SettingsChanged);

/// <summary>The settings file's protector on this machine: DPAPI on Windows, none elsewhere.</summary>
public sealed class SettingsFileProtection(ISettingProtector? protector)
{
    public ISettingProtector? Protector { get; } = protector;
}

/// <summary>The password of the PostgreSQL account the server signs in as.</summary>
public interface IDatabaseRolePassword
{
    /// <summary>
    /// Sets the signed-in account's password to <paramref name="verifier"/> (<see cref="ScramSha256.Verifier"/>), runs
    /// <paramref name="beforeCommit"/>, then commits. When either step fails, the account keeps its password.
    /// </summary>
    Task ChangeAsync(string verifier, Func<Task> beforeCommit, CancellationToken ct);

    /// <summary>Whether the account can sign in with <paramref name="password"/> now.</summary>
    Task<bool> SignsInWithAsync(string password, CancellationToken ct);
}

internal sealed partial class PostgresRolePassword(IConfiguration configuration, DatabaseCredentials credentials) : IDatabaseRolePassword
{
    public async Task ChangeAsync(string verifier, Func<Task> beforeCommit, CancellationToken ct)
    {
        // The statement takes no parameters, so the verifier is written into it: only the form ScramSha256 makes is accepted.
        if (!VerifierPattern().IsMatch(verifier))
        {
            throw new ArgumentException("Not a SCRAM-SHA-256 verifier.", nameof(verifier));
        }

        await using var connection = new NpgsqlConnection(ConnectionWith(password: null));
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
#pragma warning disable CA2100 // The verifier is checked against its fixed form above.
        await using (var alter = new NpgsqlCommand($"ALTER ROLE CURRENT_USER PASSWORD '{verifier}'", connection, transaction))
#pragma warning restore CA2100
        {
            await alter.ExecuteNonQueryAsync(ct);
        }

        await beforeCommit();
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> SignsInWithAsync(string password, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionWith(password));
            await connection.OpenAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification)
        {
            return false;
        }
    }

    /// <summary>A separate, unpooled connection, so the server's own pool is not involved.</summary>
    private string ConnectionWith(string? password)
    {
        var builder = new NpgsqlConnectionStringBuilder(credentials.WithCurrentPassword(configuration.GetConnectionString("Hospital")!)) { Pooling = false };
        if (password is not null)
        {
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    [GeneratedRegex(@"^SCRAM-SHA-256\$[0-9]+:[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+$")]
    private static partial Regex VerifierPattern();
}

/// <summary>
/// Changes the password of the PostgreSQL account the server signs in as, from Administration (SRS SEC-07): a new random
/// password nobody sees, set on the database and in the settings file together, and used at once for new connections.
/// Connections already open carry on. The settings file is written before the change is committed, and put back when it
/// cannot be, so the two never disagree. Every PostgreSQL connection string in the file that signs in as the same account
/// (a reporting replica, say) gets the new password too. The password never appears in a statement: PostgreSQL gets its
/// SCRAM verifier.
/// </summary>
public sealed class DatabasePasswordService(
    IConfiguration configuration,
    DatabaseCredentials credentials,
    DataDirectories directories,
    SettingsFileProtection protection,
    IDatabaseRolePassword database,
    HospitalDbContext db,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<DatabasePasswordService> logger)
{
    public const string AuditAction = "security.db_password_changed";
    private const string ConnectionKey = "ConnectionStrings:Hospital";

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<DatabasePasswordStatus> StatusAsync(CancellationToken ct)
    {
        var last = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == AuditAction)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.OccurredAt, e.UserName })
            .FirstOrDefaultAsync(ct);
        var refusal = Refusal();
        return new DatabasePasswordStatus(credentials.Username, refusal is null, refusal?.Message, last?.OccurredAt, last?.UserName);
    }

    public async Task<Result<DatabasePasswordChanged>> ChangeAsync(CancellationToken ct)
    {
        if (Refusal() is { } refusal)
        {
            return refusal;
        }

        await OneAtATime.WaitAsync(ct);
        try
        {
            // From here on the request's cancellation is not used: stopping between the file and the database would leave
            // them disagreeing. The change takes a moment.
            var path = ProtectedSettings.PathIn(directories);
            var original = await File.ReadAllBytesAsync(path, CancellationToken.None);
            var password = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            IReadOnlyList<string> changed = [];
            var fileWritten = false;
            try
            {
                await database.ChangeAsync(ScramSha256.Verifier(password), () =>
                {
                    changed = ProtectedSettings.ChangeSecrets(path, protection.Protector!, (key, value) => WithPassword(key, value, password));

                    // Edited since start-up to sign in as another account: the server's account must not get a password
                    // that is written nowhere.
                    if (!changed.Contains(ConnectionKey, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"{ConnectionKey} in {ProtectedSettings.FileName} no longer signs in as {credentials.Username} with a password.");
                    }

                    fileWritten = true;
                    return Task.CompletedTask;
                }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
            {
                // A commit that failed on the way back may still have reached the database: the new password decides.
                if (fileWritten && await database.SignsInWithAsync(password, CancellationToken.None))
                {
                    logger.LogWarning(ex, "The database password change reported an error, but the new password works; keeping it");
                }
                else
                {
                    var temp = path + ".tmp";
                    await File.WriteAllBytesAsync(temp, original, CancellationToken.None);
                    File.Move(temp, path, overwrite: true);
                    logger.LogError(ex, "The database password could not be changed; the old password stays");
                    return Error.Rule("database.password_not_changed",
                        "The database password could not be changed, and the old one still works. The server log says why.");
                }
            }

            credentials.Change(password);
            await audit.WriteAsync(new AuditEntry(AuditCategories.Security, AuditAction,
                $"Changed the password of database account {credentials.Username}; the settings file's {string.Join(", ", changed)} now hold it")
            {
                Severity = AuditSeverity.Warning,
            }, CancellationToken.None);
            return new DatabasePasswordChanged(time.GetUtcNow(), changed);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>Why the password cannot be changed here, or null when it can.</summary>
    private Error? Refusal()
    {
        if (!credentials.HasPassword)
        {
            return Error.Rule("database.password_none",
                "The server signs in to the database without a password (Windows sign-in or a password file), so there is none to change here.");
        }

        if (protection.Protector is null)
        {
            return Error.Rule("database.password_not_windows",
                "The database password can be changed here only on the Windows Server Node, where the settings file keeps it protected.");
        }

        return ProtectedSettings.ComesFromFile(configuration, ConnectionKey, ProtectedSettings.PathIn(directories))
            ? null
            : Error.Rule("database.password_elsewhere",
                "The database connection is set outside the settings file (in environment variables, for example), so change its password there.");
    }

    /// <summary>A PostgreSQL connection string of the file that signs in as the server's account, with the new password.</summary>
    private string? WithPassword(string key, string value, string password)
    {
        if (!key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            return null; // Not PostgreSQL (Redis, say).
        }

        if (string.IsNullOrEmpty(builder.Password) || !string.Equals(builder.Username, credentials.Username, StringComparison.Ordinal))
        {
            return null;
        }

        builder.Password = password;
        return builder.ConnectionString;
    }
}
