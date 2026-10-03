namespace HyFib.HealthCare360.Integrations.Domain;

public enum LabOrderDownloadStatus
{
    /// <summary>Waiting for the analyser to be connected and its line to be free.</summary>
    Pending = 0,

    /// <summary>The analyser accepted it.</summary>
    Sent,

    /// <summary>Nothing to send when its turn came (tests already resulted or cancelled, analyser no longer bidirectional).</summary>
    Skipped,

    /// <summary>The analyser refused it too many times.</summary>
    Failed,

    /// <summary>The analyser was not connected in time.</summary>
    Expired,
}

/// <summary>
/// One order to send an analyser without being asked (INT-LAB order download), kept until the analyser is connected.
/// Diagnostics asks for it through the outbox (<see cref="InstrumentOrderRequest"/>), one row per analyser. What is
/// sent is built when it is sent, so it is never stale. Rows hold a sample barcode and channel codes, never patient data,
/// and finished rows are deleted after 30 days (the integration log keeps the masked messages).
/// </summary>
[NotAudited]
public sealed class LabOrderDownload : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    public string InstrumentCode { get; set; } = string.Empty;

    public string SampleBarcode { get; set; } = string.Empty;

    public InstrumentOrderAction Action { get; set; }

    /// <summary>The analyser's channel codes concerned when it was requested (the tests to rerun or cancel).</summary>
    public List<string> ChannelCodes { get; set; } = [];

    public LabOrderDownloadStatus Status { get; set; }

    /// <summary>Sends tried while the analyser was connected.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>
    /// A session is sending it until then. The session extends the claim while it sends; a crashed session's claim
    /// simply lapses.
    /// </summary>
    public DateTimeOffset? ClaimedUntil { get; set; }

    /// <summary>The claim's own id: only the session holding the claim records how the send went.</summary>
    public Guid? ClaimedBy { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>The outbox message that asked for it: a redelivered message adds nothing.</summary>
    public Guid SourceMessageId { get; set; }
}
