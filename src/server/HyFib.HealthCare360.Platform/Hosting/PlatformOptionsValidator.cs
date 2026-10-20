using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Hosting;

/// <summary>
/// Checks the settings the SRS puts a ceiling on, as the server starts (SRS SEC-02): an access token lasts at most 15
/// minutes and a session at most 12 hours after sign-in. A site setting beyond either would quietly weaken every sign-in,
/// so the server refuses to start and says which setting to change instead. Development machines are exempt: the
/// integration tests keep one sign-in for a whole run, longer than any production token may last. The process role must
/// be one the server knows (SCAL-05), keys shared by several nodes must be protected by a certificate (SCAL-01), and the
/// partition settings must be in range (SCAL-04).
/// </summary>
public sealed class PlatformOptionsValidator(IHostEnvironment environment) : IValidateOptions<PlatformOptions>
{
    public const int MaxAccessTokenMinutes = 15;
    public const int MaxSessionHours = 12;

    public ValidateOptionsResult Validate(string? name, PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> problems = environment.IsDevelopment() ? [] : Problems(options);
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    public static IReadOnlyList<string> Problems(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var security = options.Security;
        var problems = new List<string>();
        if (security.AccessTokenMinutes is < 1 or > MaxAccessTokenMinutes)
        {
            problems.Add(
                $"HealthCare360:Security:AccessTokenMinutes is {security.AccessTokenMinutes}; it must be between 1 and {MaxAccessTokenMinutes} (SRS SEC-02).");
        }

        if (security.SessionHours is < 1 or > MaxSessionHours)
        {
            problems.Add(
                $"HealthCare360:Security:SessionHours is {security.SessionHours}; it must be between 1 and {MaxSessionHours} (SRS SEC-02).");
        }

        // Any other value would quietly run as "all" (RunsJobs and ServesApi test for api and worker only), SCAL-05.
        if (options.Role is not { } role || !Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"HealthCare360:Role is '{options.Role}'; it must be one of {string.Join(", ", Roles)}.");
        }

        problems.AddRange(ClusterProblems(options.Cluster));
        problems.AddRange(Http.ReverseProxy.Problems(options.Network));
        problems.AddRange(PartitionProblems(options.Partitions));
        return problems;
    }

    /// <summary>
    /// Keys in a folder several nodes share must be protected with a certificate every node holds (SCAL-01): machine DPAPI
    /// can be opened only on the machine that wrote them, and an unprotected ring would leave every key in clear on the share.
    /// </summary>
    public static IReadOnlyList<string> ClusterProblems(ClusterOptions cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var problems = new List<string>();
        var thumbprint = !string.IsNullOrWhiteSpace(cluster.KeyCertificateThumbprint);
        var file = !string.IsNullOrWhiteSpace(cluster.KeyCertificatePath);
        if (cluster.IsShared && !thumbprint && !file)
        {
            problems.Add("HealthCare360:Cluster:SharedDirectory is set, so the keys there must be protected with a certificate every node has: set " +
                         "HealthCare360:Cluster:KeyCertificateThumbprint (Local Computer › Personal store) or KeyCertificatePath (SCAL-01).");
        }

        if (thumbprint && file)
        {
            problems.Add("Set only one of HealthCare360:Cluster:KeyCertificateThumbprint and KeyCertificatePath.");
        }

        if ((thumbprint || file) && !cluster.IsShared)
        {
            problems.Add("A cluster key certificate is set, but HealthCare360:Cluster:SharedDirectory is not: on a single Server Node the keys stay protected by the machine.");
        }

        return problems;
    }

    public static IReadOnlyList<string> PartitionProblems(PartitionOptions partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        var problems = new List<string>();
        if (partitions.MonthsAhead is < 1 or > 24)
        {
            problems.Add($"HealthCare360:Partitions:MonthsAhead is {partitions.MonthsAhead}; it must be between 1 and 24 (SCAL-04).");
        }

        return problems;
    }

    public static IReadOnlyList<string> Roles { get; } = ["all", "api", "worker"];
}
