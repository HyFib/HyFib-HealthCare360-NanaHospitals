namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// An automatic charge billing refused (<see cref="ChargeRequest.Automatic"/>: a bed's day, a nursing day, a consultant's
/// visit), kept until it posts. Its module posts it again on each run; until now a refusal was only a log
/// line while the day went unbilled. One per source line: cleared when that line posts, or when its module cancels it.
/// </summary>
[NotAudited]
public sealed class ChargeRefusal : Entity
{
    public string SourceModule { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid ServiceItemId { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset? ServiceDate { get; set; }

    /// <summary>Why billing refused it the last time: the error's code and words.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;
    public DateTimeOffset FirstRefusedAt { get; set; }
    public DateTimeOffset LastRefusedAt { get; private set; }

    /// <summary>How many times it was refused, the first time included.</summary>
    public int Attempts { get; private set; }

    public void Refused(string code, string message, DateTimeOffset at)
    {
        (Code, Message, LastRefusedAt) = (code, message.Length <= 300 ? message : message[..299] + "…", at);
        Attempts++;
    }
}
