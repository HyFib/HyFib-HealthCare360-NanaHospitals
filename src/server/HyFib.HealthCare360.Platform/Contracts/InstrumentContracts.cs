using System.Text.Json.Serialization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>One analyte result reported by a laboratory analyser (HL7 OBX / ASTM R record).</summary>
public sealed record InstrumentResult(string ChannelCode, string Value, string? Units, string? Flags, DateTimeOffset? ResultedAt)
{
    /// <summary>
    /// The analyser's result status as it sent it (ASTM R.9, HL7 OBX-11): F for a final result, P for a preliminary
    /// one, C for a correction and so on; null when it sent none.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>The analyser's comment on this result (ASTM C records after the R record, HL7 NTE after the OBX).</summary>
    public string? Comment { get; init; }
}

/// <summary>
/// A decoded analyser message for one sample. <see cref="SampleBarcode"/> is empty when the analyser named no sample:
/// the LIS keeps such results for a person to assign, instead of the gateway dropping them.
/// </summary>
public sealed record InstrumentResultMessage(
    string InstrumentCode,
    string SampleBarcode,
    IReadOnlyList<InstrumentResult> Results,
    string RawMessage,
    DateTimeOffset ReceivedAt);

/// <summary>Tests the analyser should run for a sample (host query answer, or an order HC360 sends unasked).</summary>
public sealed record InstrumentOrder(string SampleBarcode, string PatientName, string? Gender, DateOnly? DateOfBirth, IReadOnlyList<string> ChannelCodes)
{
    /// <summary>The patient's hospital id (UHID), which analysers show next to the name.</summary>
    public string? PatientId { get; init; }

    public OrderPriority Priority { get; init; }

    public DateTimeOffset? CollectedAt { get; init; }

    /// <summary>Specimen type as Lab setup names it (Serum, WholeBlood, Urine…).</summary>
    public string? SpecimenType { get; init; }
}

/// <summary>An analyser as Lab setup registers it. <c>Protocol</c> is <c>Hl7</c> or <c>Astm</c>.</summary>
public sealed record InstrumentProfile(string Code, string Name, Guid FacilityId, bool IsActive, bool IsBidirectional, string Protocol);

/// <summary>Why HC360 sends an analyser an order without being asked (INT-LAB order download).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InstrumentOrderAction>))]
public enum InstrumentOrderAction
{
    /// <summary>The sample reached the lab: run its tests.</summary>
    New = 0,

    /// <summary>A result was reopened: run these tests again.</summary>
    Rerun,

    /// <summary>Tests were cancelled, or the sample was rejected: do not run them.</summary>
    Cancel,
}

/// <summary>An analyser to tell, and the channel codes concerned when the request was made.</summary>
public sealed record InstrumentOrderTarget(string InstrumentCode, IReadOnlyList<string> ChannelCodes);

/// <summary>
/// Outbox payload (<see cref="OutboxType"/>) that Diagnostics writes in the same transaction as the receipt, rerun or
/// cancellation, for each bidirectional analyser of the sample's facility that runs any of its tests. The lab gateway
/// keeps it until the analyser is connected, and builds what it sends at send time
/// (<see cref="IInstrumentResultSink.GetOrderToSendAsync"/>), so a test cancelled in the meantime is never ordered.
/// </summary>
public sealed record InstrumentOrderRequest(string SampleBarcode, InstrumentOrderAction Action, IReadOnlyList<InstrumentOrderTarget> Analysers)
{
    public const string OutboxType = "lab.analyser.order";
}

/// <summary>Implemented by the Diagnostics (LIS) module; used by the lab-equipment gateway in Integrations.</summary>
public interface IInstrumentResultSink
{
    /// <summary>
    /// Stores one sample's results. Every result is kept: on the sample's open tests, or, when it cannot go on one (no
    /// sample id, a sample unknown or not in the lab, a value the test cannot hold, another value for a verified test),
    /// among the unmatched results for a person to assign or discard. An identical resend changes nothing. A failure
    /// means nothing of the sample was stored, so the analyser should send it again.
    /// </summary>
    Task<Result> ReceiveAsync(InstrumentResultMessage message, CancellationToken cancellationToken);

    /// <summary>Host query: the analyser scanned the tube and asks which of its tests are still to run.</summary>
    Task<InstrumentOrder?> GetOrderForSampleAsync(string instrumentCode, string sampleBarcode, CancellationToken cancellationToken);

    /// <summary>
    /// Order download: what HC360 sends the analyser unasked. Only tests that analyser runs are included (its own
    /// channel codes, or plain codes when it works on the tube's bench), and only while the tube is in the lab.
    /// </summary>
    Task<InstrumentOrder?> GetOrderToSendAsync(string instrumentCode, string sampleBarcode, CancellationToken cancellationToken);

    /// <summary>The analyser as Lab setup registers it (active or not), or null when no analyser has the code.</summary>
    Task<InstrumentProfile?> GetInstrumentAsync(string instrumentCode, CancellationToken cancellationToken);
}
