namespace HyFib.HealthCare360.Platform.Integrations;

/// <summary>
/// One integration as System health shows it (SRS DED-17). <c>Health</c> is the word the integrations dashboard uses:
/// <c>healthy</c>, <c>degraded</c>, <c>failing</c>, <c>idle</c>, <c>disabled</c> or <c>not_configured</c>. The last
/// error is left out: it is on the integrations dashboard, which needs its own permission.
/// </summary>
public sealed record IntegrationHealth(
    string Name,
    string DisplayName,
    bool Enabled,
    bool Configured,
    string Health,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    int FailuresLast24Hours)
{
    public const string Failing = "failing";
}

/// <summary>Implemented by the Integrations project, which Platform must not reference; System health reads it.</summary>
public interface IIntegrationHealthSource
{
    Task<IReadOnlyList<IntegrationHealth>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Used when the Integrations project is not loaded (module tests): there are no integrations to report.</summary>
internal sealed class NoIntegrationHealthSource : IIntegrationHealthSource
{
    public Task<IReadOnlyList<IntegrationHealth>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IntegrationHealth>>([]);
}
