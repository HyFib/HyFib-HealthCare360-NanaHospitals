using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HyFib.HealthCare360.Platform.Hosting;

/// <summary>
/// Readiness of a node of several (SCAL-01): it finds the shared keys and can write to the shared folder
/// (<see cref="ClusterOptions.SharedDirectory"/>). A node that cannot is not ready, so the reverse proxy stops sending it
/// requests it would fail: every sign-in, encrypted identifier and document needs the shared folder.
/// </summary>
internal sealed class SharedFolderCheck(DataDirectories directories) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(directories.Keys))
            {
                return HealthCheckResult.Unhealthy($"The shared key folder {directories.Keys} cannot be found.");
            }

            // Beside the documents, never among them: a backup copies every file in the documents folder.
            var probe = Path.Combine(directories.Shared!, $".ready-{Environment.MachineName}-{Guid.NewGuid():N}");
            await File.WriteAllBytesAsync(probe, [1], cancellationToken);
            File.Delete(probe);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy($"The shared folder {directories.Shared} cannot be written.", ex);
        }
    }
}
