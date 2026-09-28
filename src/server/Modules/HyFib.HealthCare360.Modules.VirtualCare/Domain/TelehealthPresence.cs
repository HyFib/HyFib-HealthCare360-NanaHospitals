namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

public enum TelehealthRole
{
    Clinician = 0,

    /// <summary>A signed-in patient-portal user.</summary>
    Patient,

    /// <summary>The patient joining through the SMS/WhatsApp link without signing in.</summary>
    Guest,
}

/// <summary>
/// One connection that joined a consultation (TEL-05 participants): who, when they joined, were admitted and left,
/// and the worst connection quality their client reported. Rows are written by the signalling hub and keep the
/// SignalR connection id, so any API node (with the Redis backplane) can admit a waiting patient.
/// </summary>
[NotAudited]
public sealed class TelehealthParticipant : Entity
{
    public Guid SessionId { get; set; }
    public TelehealthRole Role { get; set; }
    public Guid? UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? AdmittedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
    public bool AudioOnly { get; set; }
    public int? WorstRoundTripMs { get; set; }
    public decimal? MaxPacketLossPercent { get; set; }
    public int? LowestBitrateKbps { get; set; }
    public DateTimeOffset? LastQualityAt { get; set; }

    public bool IsPatientSide => Role != TelehealthRole.Clinician;

    public bool IsWaiting => IsPatientSide && AdmittedAt is null && LeftAt is null;

    public void Admit(DateTimeOffset now) => AdmittedAt ??= now;

    public void Leave(DateTimeOffset now) => LeftAt ??= now;

    /// <summary>Keeps the worst values seen (what matters when reviewing a poor call); implausible numbers are ignored.</summary>
    public void RecordQuality(int? roundTripMs, decimal? packetLossPercent, int? bitrateKbps, bool audioOnly, DateTimeOffset now)
    {
        if (roundTripMs is >= 0 and <= 60_000)
        {
            WorstRoundTripMs = Math.Max(WorstRoundTripMs ?? 0, roundTripMs.Value);
        }

        if (packetLossPercent is >= 0 and <= 100)
        {
            MaxPacketLossPercent = Math.Max(MaxPacketLossPercent ?? 0, Math.Round(packetLossPercent.Value, 2));
        }

        if (bitrateKbps is > 0 and <= 100_000)
        {
            LowestBitrateKbps = LowestBitrateKbps is { } lowest ? Math.Min(lowest, bitrateKbps.Value) : bitrateKbps.Value;
        }

        AudioOnly |= audioOnly;
        LastQualityAt = now;
    }
}

/// <summary>In-call chat (TEL-02), kept with the consultation for the clinical record.</summary>
[NotAudited]
public sealed class TelehealthMessage : Entity
{
    public const int MaxLength = 2000;

    public Guid SessionId { get; set; }
    public TelehealthRole SenderRole { get; set; }
    public Guid? SenderUserId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }

    public static Result<TelehealthMessage> Create(Guid sessionId, TelehealthRole role, Guid? userId, string senderName, string? text, DateTimeOffset now)
    {
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean))
        {
            return Error.Field("text", "Type a message.");
        }

        if (clean.Length > MaxLength)
        {
            return Error.Field("text", $"A message can have at most {MaxLength} characters.");
        }

        if (clean.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            return Error.Field("text", "The message contains characters that are not allowed.");
        }

        return new TelehealthMessage
        {
            SessionId = sessionId,
            SenderRole = role,
            SenderUserId = userId,
            SenderName = senderName.Length > 200 ? senderName[..200] : senderName,
            Text = clean,
            SentAt = now,
        };
    }
}
