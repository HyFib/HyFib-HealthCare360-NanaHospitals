using HyFib.HealthCare360.Platform.Hosting;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>The settings file's hospital database connection, without its password.</summary>
/// <param name="WithPassword">Whether it signs in with a password; otherwise with the server's Windows account.</param>
/// <param name="OnThisMachine">Whether PostgreSQL is on this machine, reached through localhost.</param>
public sealed record DatabaseSignInState(string Host, int Port, string Database, string Username, bool WithPassword, bool OnThisMachine);

/// <summary>
/// How the server signs in to PostgreSQL (ASVS V13.2.1): with the password in its connection strings, or with its Windows
/// account (SSPI), which needs no secret at all. Install-HC360Server.ps1 switches between them through
/// "HyFib.HealthCare360.Server.exe database sign-in", and sets PostgreSQL up to match in pg_hba.conf and pg_ident.conf.
/// Only a PostgreSQL on this machine, reached through localhost, takes the server's Windows account: connection strings
/// to another machine keep their passwords.
/// </summary>
public static class DatabaseSignIn
{
    public const string HospitalKey = "ConnectionStrings:Hospital";

    private static readonly string[] Loopback = ["localhost", "127.0.0.1", "::1", "[::1]"];

    /// <exception cref="InvalidOperationException">The file has no hospital connection, or it cannot be opened here.</exception>
    public static DatabaseSignInState Read(string path, ISettingProtector? protector)
    {
        var value = ProtectedSettings.ReadSecret(path, HospitalKey, protector)
                    ?? throw new InvalidOperationException($"{ProtectedSettings.FileName} has no {HospitalKey}.");
        var builder = new NpgsqlConnectionStringBuilder(value);
        return new DatabaseSignInState(builder.Host ?? string.Empty, builder.Port, builder.Database ?? string.Empty,
            builder.Username ?? string.Empty, !string.IsNullOrEmpty(builder.Password), IsOnThisMachine(builder.Host));
    }

    /// <summary>
    /// Takes the password out of every connection string of the file that signs in to this machine's PostgreSQL as the
    /// hospital connection's account, so the server signs in with its Windows account. Returns the settings it changed.
    /// </summary>
    /// <exception cref="InvalidOperationException">PostgreSQL is on another machine, or a setting cannot be opened here.</exception>
    public static IReadOnlyList<string> UseWindows(string path, ISettingProtector protector)
    {
        var hospital = Read(path, protector);
        if (!hospital.OnThisMachine)
        {
            throw new InvalidOperationException(
                $"PostgreSQL is reached at {hospital.Host}, not through localhost: only a PostgreSQL on this machine takes the server's Windows account.");
        }

        return ProtectedSettings.ChangeSecrets(path, protector, (key, value) =>
            OwnConnection(key, value, hospital.Username) is { Password.Length: > 0 } builder ? WithPassword(builder, null) : null);
    }

    /// <summary>
    /// Puts <paramref name="password"/> into every connection string of the file that signs in to this machine's PostgreSQL
    /// as the hospital connection's account, so the server signs in with it again. Returns the settings it changed.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting cannot be opened here.</exception>
    public static IReadOnlyList<string> UsePassword(string path, ISettingProtector protector, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var hospital = Read(path, protector);
        return ProtectedSettings.ChangeSecrets(path, protector, (key, value) =>
            OwnConnection(key, value, hospital.Username) is { } builder && !string.Equals(builder.Password, password, StringComparison.Ordinal)
                ? WithPassword(builder, password)
                : null);
    }

    /// <summary>Whether <paramref name="host"/> is this machine's loopback address, the only one the Windows sign-in is set up on.</summary>
    public static bool IsOnThisMachine(string? host) =>
        host is not null && Loopback.Contains(host.Trim(), StringComparer.OrdinalIgnoreCase);

    // A PostgreSQL connection string of the file that signs in to this machine as the account; null for anything else,
    // such as Redis or a reporting replica on another machine.
    private static NpgsqlConnectionStringBuilder? OwnConnection(string key, string value, string username)
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
            return null;
        }

        return string.Equals(builder.Username, username, StringComparison.Ordinal) && IsOnThisMachine(builder.Host) ? builder : null;
    }

    private static string WithPassword(NpgsqlConnectionStringBuilder builder, string? password)
    {
        builder.Password = password;
        return builder.ConnectionString;
    }
}
