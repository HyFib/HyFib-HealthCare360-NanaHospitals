namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>
/// A channel an analyser sends that no active test in the catalogue maps (INT-LAB), as its messages show it: when it
/// was first and last sent, how often, and its last value and unit. The analyser measures it, but its value goes on no
/// report until the lab maps it to a test or decides not to (<see cref="IgnoredAnalyserChannel"/>). A channel a test
/// maps later drops off the list, which is worked out when it is read, so mapping it in Lab setup is all it takes.
/// The lab-equipment gateway writes it as the messages arrive; it changes no patient's record.
/// </summary>
[NotAudited]
public sealed class AnalyserChannel : Entity, IFacilityScoped
{
    public const int ChannelCodeLength = 64;
    public const int ValueLength = 64;
    public const int UnitLength = 32;

    public Guid FacilityId { get; set; }
    public Guid InstrumentId { get; set; }

    /// <summary>The channel as the analyser sent it, trimmed and in upper case, as the catalogue's mappings are.</summary>
    public string ChannelCode { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int TimesSeen { get; set; }

    /// <summary>The last value sent on the channel, to tell what it measures (cut to <see cref="ValueLength"/>).</summary>
    public string? LastValue { get; set; }

    public string? LastUnit { get; set; }
}

/// <summary>
/// The lab's decision that an analyser's channel goes on no report (a research parameter, a flag the analyser sends as
/// a channel), with why. While it stands the channel is listed as ignored rather than waiting to be mapped. Taking the
/// decision back removes it; both are in the audit trail.
/// </summary>
public sealed class IgnoredAnalyserChannel : AuditableEntity, IFacilityScoped
{
    public const int ReasonLength = 300;

    public Guid FacilityId { get; set; }
    public Guid InstrumentId { get; set; }
    public string ChannelCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
