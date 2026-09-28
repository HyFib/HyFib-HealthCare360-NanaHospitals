using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Platform.Integrations;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.TestSupport;

/// <summary>An <see cref="IntegrationTrace"/> backed by a substitute logger whose entries tests can inspect.</summary>
internal sealed class Tracing
{
    public Tracing()
    {
        Trace = new IntegrationTrace(Logger, TimeProvider.System);
    }

    public IIntegrationLogger Logger { get; } = Substitute.For<IIntegrationLogger>();

    public IntegrationTrace Trace { get; }

    public IReadOnlyList<IntegrationLogEntry> Entries =>
        Logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IIntegrationLogger.LogAsync))
            .Select(call => (IntegrationLogEntry)call.GetArguments()[0]!)
            .ToList();

    /// <summary>Everything written to the integration log, for "never logs X" assertions.</summary>
    public string AllText => string.Join("\n", Entries.Select(e => $"{e.Reference}|{e.Request}|{e.Response}|{e.Error}"));
}
