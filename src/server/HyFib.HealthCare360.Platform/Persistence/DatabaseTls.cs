using System.Net;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// The server reaches a PostgreSQL on another machine only over TLS that checks who answers (ASVS V9.2.2, V12.3.1): the
/// patients' records and the database password cross the network. A connection string to another machine must say
/// <c>SSL Mode=VerifyFull</c> (encrypted, the certificate chains to a trusted root and names the host), or
/// <c>Require</c> or <c>VerifyCA</c> with a <c>Root Certificate</c> it must chain to (the hospital's own CA). Without it,
/// Npgsql's default (<c>Prefer</c>) sends everything in clear to a server that offers no TLS, and to anyone in the way that
/// says it is the database. The server does not start (<see cref="Problems"/>) until it is set, or until
/// <see cref="DatabaseOptions.AllowUnverifiedRemoteConnection"/> says it may, for a lab.
/// <para>
/// A PostgreSQL on this machine (localhost, 127.0.0.0/8, ::1, or a Unix socket) is reached without leaving it, and needs none
/// of this.
/// </para>
/// </summary>
public static class DatabaseTls
{
    /// <summary>Connection strings that are not PostgreSQL's.</summary>
    private static readonly string[] NotPostgres = ["Redis"];

    /// <summary>
    /// Why the server must not start with these connection strings: one sentence for each PostgreSQL connection string to
    /// another machine that does not check the server it reaches. None when the escape hatch is on.
    /// </summary>
    public static IReadOnlyList<string> Problems(IConfiguration configuration, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowUnverifiedRemoteConnection)
        {
            return [];
        }

        var problems = new List<string>();
        foreach (var setting in configuration.GetSection("ConnectionStrings").GetChildren())
        {
            if (setting.Value is { Length: > 0 } value
                && !NotPostgres.Contains(setting.Key, StringComparer.OrdinalIgnoreCase)
                && Problem(setting.Key, value) is { } problem)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    /// <summary>Why <paramref name="connectionString"/> may not be used as it is; null when it may (or is not PostgreSQL's).</summary>
    public static string? Problem(string name, string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var remote = RemoteHosts(builder.Host).ToList();
        if (remote.Count == 0 || Verifies(builder))
        {
            return null;
        }

        return $"ConnectionStrings:{name} reaches PostgreSQL on {string.Join(", ", remote)} with SSL Mode={builder.SslMode}" +
               (string.IsNullOrEmpty(builder.RootCertificate) ? string.Empty : $" and Root Certificate {builder.RootCertificate}") +
               ", which does not check that the server answering is the hospital's database: what goes to it (patients' records, " +
               "the database password) could be read or changed on the network. Add \"SSL Mode=VerifyFull\" to it (the database's " +
               "certificate must name the host and chain to a root this machine trusts), or \"SSL Mode=VerifyFull;Root " +
               "Certificate=<the hospital CA's .crt>\". For a lab only, HealthCare360:Database:AllowUnverifiedRemoteConnection=true " +
               "lets it start as it is (docs/deployment/DEPLOYMENT.md, \"The database over TLS\").";
    }

    /// <summary>Whether the connection checks the server's certificate: VerifyFull, or Require or VerifyCA against a pinned root.</summary>
    internal static bool Verifies(NpgsqlConnectionStringBuilder builder) => builder.SslMode switch
    {
        SslMode.VerifyFull => true,
        SslMode.Require or SslMode.VerifyCA => !string.IsNullOrWhiteSpace(builder.RootCertificate),
        _ => false,
    };

    /// <summary>The hosts of a Host setting (Npgsql takes several, comma-separated, each with an optional port) that are not this machine.</summary>
    internal static IEnumerable<string> RemoteHosts(string? hosts)
    {
        if (string.IsNullOrWhiteSpace(hosts))
        {
            // Npgsql's default host is localhost.
            yield break;
        }

        foreach (var entry in hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var host = HostOf(entry);
            if (!IsThisMachine(host))
            {
                yield return host;
            }
        }
    }

    /// <summary>Whether <paramref name="host"/> is reached without leaving this machine: a Unix socket's folder, localhost, or a loopback address.</summary>
    internal static bool IsThisMachine(string host)
    {
        if (host.StartsWith('/') || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    // "db1:5433", "[::1]:5432" or "::1": the host without its port.
    private static string HostOf(string entry)
    {
        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? entry[..(close + 1)] : entry;
        }

        var colon = entry.LastIndexOf(':');
        return colon > 0 && entry.IndexOf(':', StringComparison.Ordinal) == colon && int.TryParse(entry[(colon + 1)..], out _) ? entry[..colon] : entry;
    }
}

/// <summary>System health's warning while a connection to another machine's PostgreSQL is let through unchecked, for a lab.</summary>
internal sealed class DatabaseTlsWarnings(IConfiguration configuration, Microsoft.Extensions.Options.IOptions<PlatformOptions> options) : Networking.IHealthWarningSource
{
    public Task<IReadOnlyList<Networking.HealthWarning>> WarningsAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Database.AllowUnverifiedRemoteConnection)
        {
            return Task.FromResult<IReadOnlyList<Networking.HealthWarning>>([]);
        }

        var unverified = DatabaseTls.Problems(configuration, new DatabaseOptions()).Count;
        return Task.FromResult<IReadOnlyList<Networking.HealthWarning>>(unverified == 0
            ? []
            :
            [
                new("database.tls_unverified", "warning",
                    "The server reaches PostgreSQL on another machine without checking its certificate (HealthCare360:Database:AllowUnverifiedRemoteConnection): fit for a lab only. Set SSL Mode=VerifyFull in the connection string and turn the setting off."),
            ]);
    }
}
