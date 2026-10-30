using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

public enum WardIndentStatus
{
    Pending = 0,
    PartiallyIssued,
    Issued,
    Cancelled,
}

public enum IndentUrgency
{
    Routine = 0,

    /// <summary>Needed immediately (emergency drug, deteriorating patient); shown first at the IP counter.</summary>
    Stat,
}

/// <summary>A nurse's request for medicines/consumables for one admitted patient (PH-03 ward indent).</summary>
public sealed class WardIndent : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid? VisitId { get; set; }
    public string? WardName { get; set; }
    public string? BedLabel { get; set; }
    public IndentUrgency Urgency { get; set; }
    public WardIndentStatus Status { get; private set; }
    public Guid? RaisedByUserId { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public Guid? PrescriptionId { get; set; }
    public string? Remarks { get; set; }
    public DateTimeOffset? LastIssuedAt { get; private set; }
    public string? CancelReason { get; private set; }
    public List<WardIndentLine> Lines { get; set; } = [];

    public bool IsOpen => Status is WardIndentStatus.Pending or WardIndentStatus.PartiallyIssued;

    public Result RecordIssued(Guid lineId, decimal quantity, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("ward_indent.closed", $"Ward indent {Number} is {Status}.");
        }

        var line = Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound(nameof(WardIndentLine), lineId);
        }

        if (quantity <= 0 || quantity > line.RemainingQuantity)
        {
            return Error.Rule("ward_indent.over_issue", $"{line.ItemName}: only {StockErrors.Format(line.RemainingQuantity)} is pending on this indent.");
        }

        line.IssuedQuantity += quantity;
        LastIssuedAt = now;
        Status = Lines.All(l => l.RemainingQuantity <= 0) ? WardIndentStatus.Issued : WardIndentStatus.PartiallyIssued;
        return Result.Success();
    }

    /// <summary>Cancelled by the ward or on discharge; issued quantities stay issued (return them with a ward return).</summary>
    public Result Cancel(string reason)
    {
        if (!IsOpen)
        {
            return Error.Rule("ward_indent.closed", $"Ward indent {Number} is already {Status}.");
        }

        Status = Lines.Any(l => l.IssuedQuantity > 0) ? WardIndentStatus.Issued : WardIndentStatus.Cancelled;
        CancelReason = reason;
        return Result.Success();
    }
}

public sealed class WardIndentLine : Entity
{
    public Guid WardIndentId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public decimal RequestedQuantity { get; set; }
    public decimal IssuedQuantity { get; internal set; }
    public string? Remarks { get; set; }

    public decimal RemainingQuantity => Math.Max(RequestedQuantity - IssuedQuantity, 0);
}

/// <summary>
/// Patient-wise issue from the IP pharmacy to the ward (PH-03). Every line is charged to the IP account through
/// <c>IChargePoster</c> at MRP (idempotent on the line id) and the issue feeds the nursing MAR via
/// <see cref="MedicationDispensed"/>.
/// </summary>
public sealed class IpIssue : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid StoreId { get; set; }
    public Guid PatientId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? WardIndentId { get; set; }
    public Guid? QueueEntryId { get; set; }
    public Guid? PrescriptionId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public Guid? IssuedByUserId { get; set; }
    public decimal TotalAmount { get; set; }

    /// <summary>Touched by every ward return so two returns of one line cannot both succeed.</summary>
    public DateTimeOffset? LastReturnedAt { get; private set; }

    public List<IpIssueLine> Lines { get; set; } = [];

    public void MarkIssued() => Raise(new MedicationDispensed(
        Id, PatientId, AdmissionId, PrescriptionId,
        Lines.Select(l => new DispensedLine(l.PrescriptionLineId, l.ItemId, l.ItemName, l.BatchNumber, l.Quantity)).ToList(),
        IssuedAt));

    /// <summary>Returns part of a line from the ward and gives the credit due at the line's unit price, as it was charged.</summary>
    public Result<decimal> ReturnLine(Guid lineId, decimal quantity, DateTimeOffset now)
    {
        var line = Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound(nameof(IpIssueLine), lineId);
        }

        if (quantity <= 0 || quantity > line.Quantity - line.ReturnedQuantity)
        {
            return Error.Rule("ward_return.exceeds_issued", $"{line.ItemName}: at most {StockErrors.Format(line.Quantity - line.ReturnedQuantity)} can be returned.");
        }

        line.ReturnedQuantity += quantity;
        LastReturnedAt = now;
        return Money.Round(quantity * line.UnitPrice);
    }
}

public sealed class IpIssueLine : Entity
{
    public Guid IpIssueId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public ItemType ItemType { get; set; }
    public DrugSchedule Schedule { get; set; }
    public bool IsControlled { get; set; }
    public Guid BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitMrp { get; set; }

    /// <summary>
    /// What a unit is charged: its MRP, or for a price-controlled implant its NPPA ceiling price when that is lower (a pack
    /// printed before the ceiling was cut). A return is credited at the same price.
    /// </summary>
    public decimal UnitPrice { get; set; }

    public decimal Amount { get; set; }
    public Guid? WardIndentLineId { get; set; }
    public Guid? QueueLineId { get; set; }
    public Guid? PrescriptionLineId { get; set; }

    /// <summary>Charge id returned by billing (null until posted).</summary>
    public Guid? ChargeId { get; set; }

    public decimal ReturnedQuantity { get; internal set; }
}

public enum ChargeReversal
{
    /// <summary>The whole line came back before billing: the original charge was cancelled.</summary>
    Cancelled = 0,

    /// <summary>A negative-quantity credit line was posted against the IP account.</summary>
    Credited,
}

/// <summary>Medicines returned from the ward (PH-03): restocked into their batch and taken off the patient's IP bill.</summary>
public sealed class WardReturn : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid IpIssueId { get; set; }
    public Guid PatientId { get; set; }
    public Guid AdmissionId { get; set; }
    public DateTimeOffset ReturnedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal CreditAmount { get; set; }
    public List<WardReturnLine> Lines { get; set; } = [];
}

public sealed class WardReturnLine : Entity
{
    public Guid WardReturnId { get; set; }
    public Guid IpIssueLineId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public Guid BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal CreditAmount { get; set; }
    public bool Restocked { get; set; }
    public ChargeReversal ChargeReversal { get; set; }
}
