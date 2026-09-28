using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>One analyte result reported by a laboratory analyser (HL7 OBX / ASTM R record).</summary>
public sealed record InstrumentResult(string ChannelCode, string Value, string? Units, string? Flags, DateTimeOffset? ResultedAt);

/// <summary>A decoded analyser message for one sample.</summary>
public sealed record InstrumentResultMessage(
    string InstrumentCode,
    string SampleBarcode,
    IReadOnlyList<InstrumentResult> Results,
    string RawMessage,
    DateTimeOffset ReceivedAt);

/// <summary>Tests the analyser should run for a sample (bi-directional host query).</summary>
public sealed record InstrumentOrder(string SampleBarcode, string PatientName, string? Gender, DateOnly? DateOfBirth, IReadOnlyList<string> ChannelCodes);

/// <summary>Implemented by the Diagnostics (LIS) module; used by the lab-equipment gateway in Integrations.</summary>
public interface IInstrumentResultSink
{
    Task<Result> ReceiveAsync(InstrumentResultMessage message, CancellationToken cancellationToken);

    Task<InstrumentOrder?> GetOrderForSampleAsync(string instrumentCode, string sampleBarcode, CancellationToken cancellationToken);
}
