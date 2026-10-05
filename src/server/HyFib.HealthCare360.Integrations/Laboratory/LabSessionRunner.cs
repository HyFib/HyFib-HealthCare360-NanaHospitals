using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Hl7;

namespace HyFib.HealthCare360.Integrations.Laboratory;

/// <summary>
/// Runs the analyser's protocol over a connection, whatever carries it: a TCP socket the analyser opened, or a serial
/// line relayed by a lab agent. The protocol, acknowledgements, host queries and orders are the same for both.
/// </summary>
internal static class LabSessionRunner
{
    public static Task RunAsync(
        LabInstrumentSettings instrument, LabRuntimeOptions options, Stream connection, ILabMessageProcessor processor, TimeProvider time, ILogger logger,
        CancellationToken cancellationToken) =>
        instrument.Protocol == LabProtocol.Hl7v2
            ? new MllpSession(instrument, options, processor, time, logger).RunAsync(connection, cancellationToken)
            : new AstmSession(instrument, options, processor, time, logger).RunAsync(connection, cancellationToken);
}
