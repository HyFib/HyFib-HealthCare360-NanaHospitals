namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// That a GSTIN's GSTR-1 for a month was filed on the GST portal, as the accountant records it with the ARN the portal
/// gave: recorded once, corrected only with a reason (both audited), never deleted. A paper bill entered afterwards for
/// that month is not in the filed return; it is reported as an amendment in the next one.
/// </summary>
public sealed class GstReturnFiling : AuditableEntity
{
    public const string Gstr1 = "GSTR1";

    public string Gstin { get; set; } = string.Empty;
    public string ReturnType { get; set; } = Gstr1;
    public int Year { get; set; }
    public int Month { get; set; }

    /// <summary>The acknowledgement reference number the portal gave on filing (15 characters).</summary>
    public string Arn { get; set; } = string.Empty;
    public DateOnly FiledOn { get; set; }

    /// <summary>When the filing was recorded in HC360: paper bills entered after this for the month are late.</summary>
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? RecordedByName { get; set; }
    public DateTimeOffset? CorrectedAt { get; set; }
    public string? CorrectedByName { get; set; }
    public string? CorrectionReason { get; set; }
}

/// <summary>A recorded GSTR-1 filing, as the GST returns screen and the API show it.</summary>
public sealed record Gstr1Filing(
    int Year,
    int Month,
    string Arn,
    DateOnly FiledOn,
    DateTimeOffset RecordedAt,
    string? RecordedByName,
    DateTimeOffset? CorrectedAt,
    string? CorrectedByName,
    string? CorrectionReason,
    uint Version);

/// <summary>
/// A paper bill entered during this month for an earlier month whose GSTR-1 had already been filed: it is not in that
/// return, and is reported as an amendment (B2BA or B2CSA) in this one.
/// </summary>
public sealed record Gstr1LateEntry(string Number, DateOnly Date, decimal Value, int FiledYear, int FiledMonth, string FiledArn, DateOnly EnteredOn);
