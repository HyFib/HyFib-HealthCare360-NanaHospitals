namespace HyFib.HealthCare360.Integrations.Features.Admin;

/// <summary>
/// The integrations dashboard's rows for System health (SRS DED-17), so both say the same about each integration. The
/// last error stays on the dashboard: System health is open to holders of a different permission.
/// </summary>
internal sealed class IntegrationHealthSource(IntegrationStatusService status) : IIntegrationHealthSource
{
    public async Task<IReadOnlyList<IntegrationHealth>> ListAsync(CancellationToken cancellationToken) =>
        (await status.ListAsync(cancellationToken))
        .Select(row => new IntegrationHealth(row.Name, row.DisplayName, row.Enabled, row.Configured, row.Health, row.LastSuccessAt,
            row.LastFailureAt, row.FailuresLast24Hours))
        .ToList();
}
