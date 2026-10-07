using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// The account the server signs in to PostgreSQL with (SRS SEC-07). Its password is held here, apart from the connection
/// string, so Administration can change it while the server runs: each data source asks for it when it opens a new
/// physical connection, and connections already open carry on. A connection string without a password (Windows sign-in,
/// or a password file) is used as it is.
/// </summary>
public sealed class DatabaseCredentials
{
    private volatile string _password;

    public DatabaseCredentials(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Username = builder.Username ?? string.Empty;
        _password = builder.Password ?? string.Empty;
    }

    /// <summary>The PostgreSQL role (user) the server signs in as.</summary>
    public string Username { get; }

    /// <summary>False when the server signs in without a password, so there is none to change.</summary>
    public bool HasPassword => _password.Length > 0;

    internal string Password => _password;

    internal void Change(string password) => _password = password;

    /// <summary>
    /// <paramref name="connectionString"/> without its password, and the data source set-up that supplies it when a
    /// connection opens: this account's current password when the string signs in as this account, else its own. Without
    /// a password the string is returned as it is, and the set-up does nothing.
    /// </summary>
    public (string ConnectionString, Action<NpgsqlDataSourceBuilder> Configure) For(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(builder.Password))
        {
            return (connectionString, _ => { });
        }

        var own = builder.Password;
        var sameAccount = string.Equals(builder.Username, Username, StringComparison.Ordinal);
        builder.Password = null;
        return (builder.ConnectionString, dataSource => dataSource.UsePasswordProvider(
            _ => sameAccount ? _password : own,
            (_, _) => ValueTask.FromResult(sameAccount ? _password : own)));
    }

    /// <summary>A connection string with this account's current password, for tools that need it whole (pg_dump).</summary>
    public string WithCurrentPassword(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.Password) && string.Equals(builder.Username, Username, StringComparison.Ordinal))
        {
            builder.Password = _password;
        }

        return builder.ConnectionString;
    }
}

/// <summary>
/// PostgreSQL's SCRAM-SHA-256 form of a password (RFC 5802, RFC 7677), as <c>psql</c>'s <c>\password</c> sends it: the
/// server stores it as it is, and the password itself never appears in a statement, so it cannot reach the database's logs.
/// </summary>
public static class ScramSha256
{
    public const int Iterations = 4096;
    private const int SaltLength = 16;

    /// <summary>The verifier PostgreSQL stores: <c>SCRAM-SHA-256$iterations:salt$StoredKey:ServerKey</c>.</summary>
    public static string Verifier(string password, byte[]? salt = null, int iterations = Iterations)
    {
        salt ??= RandomNumberGenerator.GetBytes(SaltLength);
        var (_, storedKey, serverKey) = Keys(password, salt, iterations);
        return string.Create(CultureInfo.InvariantCulture,
            $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}");
    }

    /// <summary>
    /// The keys RFC 5802 derives from a password. The passwords HC360 makes are ASCII letters, digits, '-' and '_', which
    /// SASLprep leaves as they are.
    /// </summary>
    internal static (byte[] ClientKey, byte[] StoredKey, byte[] ServerKey) Keys(string password, byte[] salt, int iterations)
    {
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        return (clientKey, SHA256.HashData(clientKey), HMACSHA256.HashData(salted, "Server Key"u8));
    }
}
