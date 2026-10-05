using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Pacs.Worklist;

namespace HyFib.HealthCare360.Integrations.Features.Admin;

/// <summary>
/// Builds the integrations dashboard: configured provider and switch from settings, last success/failure from the
/// integration log (index on integration + time keeps each lookup cheap), and live listener state for analysers and
/// the modality worklist server.
/// </summary>
internal sealed class IntegrationStatusService(
    HospitalDbContext db, IntegrationSettingsReader settings, LabGatewayState lab, ModalityWorklistState worklist, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<IReadOnlyList<IntegrationStatusDto>> ListAsync(CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow().AddHours(-24);
        var facility = currentUser.FacilityId;
        var rows = new List<IntegrationStatusDto>();
        foreach (var name in IntegrationNames.All)
        {
            var (configured, enabled, provider) = await ReadSwitchAsync(name, cancellationToken);
            var logs = db.Set<IntegrationLog>().AsNoTracking().Where(l => l.Integration == name && (l.FacilityId == null || l.FacilityId == facility));
            var lastSuccess = await logs.Where(l => l.Success).MaxAsync(l => (DateTimeOffset?)l.CreatedAt, cancellationToken);
            var lastFailure = await logs.Where(l => !l.Success).OrderByDescending(l => l.CreatedAt)
                .Select(l => new { l.CreatedAt, l.Error })
                .FirstOrDefaultAsync(cancellationToken);
            var failures = await logs.CountAsync(l => !l.Success && l.CreatedAt >= since, cancellationToken);
            var listeners = name == IntegrationNames.Lab ? lab.Snapshot() : name == IntegrationNames.Mwl ? worklist.Snapshot() : null;
            var health = Health(configured, enabled, lastSuccess, lastFailure?.CreatedAt, failures, listeners);
            rows.Add(new IntegrationStatusDto(name, IntegrationNames.DisplayName(name), provider, enabled, configured, health,
                lastSuccess, lastFailure?.CreatedAt, lastFailure?.Error, failures, listeners));
        }

        return rows;
    }

    internal static string Health(bool configured, bool enabled, DateTimeOffset? lastSuccess, DateTimeOffset? lastFailure, int failuresLastDay,
        IReadOnlyList<LabListenerStatus>? listeners)
    {
        if (!configured)
        {
            return "not_configured";
        }

        if (!enabled)
        {
            return "disabled";
        }

        if (listeners is not null && listeners.Any(l => l.State == "failed"))
        {
            return "failing";
        }

        if (lastFailure is not null && (lastSuccess is null || lastFailure > lastSuccess))
        {
            return "failing";
        }

        if (failuresLastDay > 0)
        {
            return "degraded";
        }

        return lastSuccess is null ? "idle" : "healthy";
    }

    private async Task<(bool Configured, bool Enabled, string? Provider)> ReadSwitchAsync(string name, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case IntegrationNames.Labels:
                // Label building is local to the server: always available.
                return (true, true, "zpl/tspl");
            case IntegrationNames.Lab:
                var gateway = await settings.ReadValueAsync<LabGatewaySettings>(LabSettingKeys.Gateway, null, cancellationToken);
                return (gateway is not null, gateway?.Enabled == true, "tcp");
            case IntegrationNames.Mwl:
                var server = await settings.ReadValueAsync<WorklistSettings>(WorklistSettings.Key, null, cancellationToken);
                return (server is not null, server?.Enabled == true, "dicom");
            default:
                var config = await settings.ReadAsync<IntegrationSummarySettings, NoSecrets>(name, null, requireEnabled: false, cancellationToken);
                return (config.Settings is not null, config.Settings?.Enabled == true, config.Settings?.Provider?.Trim().ToLowerInvariant());
        }
    }
}
