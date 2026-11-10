namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>Who a debit note charges: the patient, or the bill's payer (insurer, TPA or corporate).</summary>
public enum DebitNoteParty
{
    Patient = 0,
    Payer,
}

/// <summary>
/// A debit note (a supplementary invoice, CGST Act 2017 s.34(3), CGST rule 53): it raises what a finalized bill charged
/// for services already on it (a rate charged short, a quantity under-billed), line by line, the added taxable value
/// taxed at the line's own rate, IGST to another state or CGST and SGST within it. It has its own number series
/// (<c>billing.debitnote</c>), names the bill it supplements, and adds to what the patient or the payer owes. To a payer
/// registered for GST on a bill with GST it is an e-invoice (<c>DBN</c>, GST rule 48(4)). One person asks for it and
/// another issues it by approving (maker-checker), as with a credit note.
/// </summary>
public sealed class DebitNote : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid BillId { get; set; }
    public Guid PatientId { get; set; }
    public DebitNoteParty Party { get; set; }

    /// <summary>What the note adds to the bill, GST included.</summary>
    public decimal Amount { get; private set; }

    public decimal TaxableAmount { get; private set; }
    public decimal CgstAmount { get; private set; }
    public decimal SgstAmount { get; private set; }
    public decimal IgstAmount { get; private set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? RequestedBy { get; set; }
    public string? RequestedByName { get; set; }
    public Guid? IssuedBy { get; set; }
    public string? IssuedByName { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public List<DebitNoteLine> Lines { get; set; } = [];

    public decimal GstAmount => CgstAmount + SgstAmount + IgstAmount;

    /// <summary>Adds <paramref name="taxable"/> to <paramref name="line"/>, with its GST at the line's rate.</summary>
    public DebitNoteLine Debit(BillLine line, decimal taxable, bool interState)
    {
        var share = DebitNotes.Gst(taxable, line.TaxRate, interState);
        var debited = new DebitNoteLine
        {
            DebitNoteId = Id,
            BillLineId = line.Id,
            Description = line.Description,
            SacCode = line.SacCode,
            GstRate = line.TaxRate,
            TaxableAmount = share.Taxable,
            CgstAmount = share.Cgst,
            SgstAmount = share.Sgst,
            IgstAmount = share.Igst,
            Amount = share.Taxable + share.Gst,
            SortOrder = Lines.Count,
        };
        Lines.Add(debited);
        TaxableAmount += debited.TaxableAmount;
        CgstAmount += debited.CgstAmount;
        SgstAmount += debited.SgstAmount;
        IgstAmount += debited.IgstAmount;
        Amount += debited.Amount;
        return debited;
    }
}

/// <summary>A bill line a debit note raises: the taxable value it adds and the GST on it at the line's rate.</summary>
[AuditedWithParent]
public sealed class DebitNoteLine : Entity
{
    public Guid DebitNoteId { get; set; }
    public Guid BillLineId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? SacCode { get; set; }
    public decimal GstRate { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal IgstAmount { get; set; }
    public decimal Amount { get; set; }
    public int SortOrder { get; set; }

    public decimal GstAmount => CgstAmount + SgstAmount + IgstAmount;
}

public static class DebitNotes
{
    /// <summary>
    /// The GST on <paramref name="taxable"/> added at <paramref name="rate"/>%: IGST to another state, else CGST and SGST in
    /// halves as bills split them (<see cref="Money.SplitGst"/>).
    /// </summary>
    public static GstShare Gst(decimal taxable, decimal rate, bool interState)
    {
        var value = Money.Round(taxable);
        var gst = Money.Round(value * rate / 100m);
        if (interState)
        {
            return new GstShare(value, 0m, 0m, gst);
        }

        var split = Money.SplitGst(gst);
        return new GstShare(value, split.Cgst, split.Sgst, 0m);
    }
}
