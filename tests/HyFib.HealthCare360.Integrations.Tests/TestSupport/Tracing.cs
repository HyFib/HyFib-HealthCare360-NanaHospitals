using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Platform.Integrations;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.TestSupport;

/// <summary>An <see cref="IntegrationTrace"/> backed by a substitute logger whose entries tests can inspect.</summary>
internal sealed class Tracing
{
    private readonly List<Guid?> _facilities = [];

    public Tracing()
    {
        Trace = new IntegrationTrace(Logger, TimeProvider.System);

        // The facility a row would be stored under: the entry's own, else that of the scope open when it was written.
        Logger.When(l => l.LogAsync(Arg.Any<IntegrationLogEntry>(), Arg.Any<CancellationToken>()))
            .Do(call => _facilities.Add(call.Arg<IntegrationLogEntry>().FacilityId ?? IntegrationLogScope.FacilityId));
    }

    public IIntegrationLogger Logger { get; } = Substitute.For<IIntegrationLogger>();

    public IntegrationTrace Trace { get; }

    public IReadOnlyList<IntegrationLogEntry> Entries =>
        Logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IIntegrationLogger.LogAsync))
            .Select(call => (IntegrationLogEntry)call.GetArguments()[0]!)
            .ToList();

    /// <summary>The facility of each entry, in order, as the platform logger would tag it ahead of the signed-in user's.</summary>
    public IReadOnlyList<Guid?> Facilities => _facilities;

    /// <summary>Everything written to the integration log, for "never logs X" assertions.</summary>
    public string AllText => string.Join("\n", Entries.Select(e => $"{e.Reference}|{e.Request}|{e.Response}|{e.Error}"));
}
