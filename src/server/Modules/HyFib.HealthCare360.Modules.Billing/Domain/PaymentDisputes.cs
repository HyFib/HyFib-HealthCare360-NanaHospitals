namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>Where HC360 learnt of a chargeback.</summary>
public enum PaymentDisputeSource
{
    /// <summary>The payment gateway took the money back in a settlement (an adjustment on the payment).</summary>
    Gateway = 0,

    /// <summary>The card or UPI acquirer's settlement report carried a chargeback on a terminal payment.</summary>
    Terminal,

    /// <summary>An accountant opened it from the bank's or the gateway's notice, before a settlement carried it.</summary>
    Notice,
}

public enum PaymentDisputeStatus
{
    /// <summary>The hospital has not answered yet.</summary>
    Open = 0,

    /// <summary>The hospital sent its evidence; the bank or the gateway decides.</summary>
    Contested,

    /// <summary>The money came back to the hospital, or the bank dropped the chargeback.</summary>
    Won,

    /// <summary>The money is gone: the receipt was reversed with a second person's approval, and the bill owes it again.</summary>
    Lost,
}

/// <summary>
/// A chargeback: the patient's bank, through the card network, UPI or the payment gateway, asked for the money of a
/// card, UPI or gateway payment back. One case per payment line of a receipt and per debit that carried it. It is opened
/// by HC360 when a settlement takes money back on a payment it can name, or by an accountant from the bank's notice; it
/// carries the reason and the date the hospital must answer by; the accountant records the evidence sent; it ends won
/// (the money came back) or lost. A loss is never recorded by one person: accepting it asks for the receipt to be
/// reversed (<see cref="ReversalReason.Chargeback"/>), and a second person who approves reversals makes it, which
/// reopens the bill's balance and posts the reversal in the day book (<c>PaymentReversedDisputeHandler</c>).
/// </summary>
public sealed class PaymentDispute : AuditableEntity, IFacilityScoped
{
    public const int NoteMaxLength = 500;

    /// <summary>A case whose answer is due within this many days, and not yet answered, is reminded every day.</summary>
    public const int RemindDays = 2;

    public Guid FacilityId { get; set; }

    /// <summary>The case's number, quoted to the bank and the gateway (<see cref="BillingNumberSeries.PaymentDispute"/>).</summary>
    public string Number { get; set; } = string.Empty;

    public PaymentDisputeSource Source { get; set; }
    public PaymentDisputeStatus Status { get; set; }

    public Guid ReceiptId { get; set; }

    /// <summary>The receipt's payment line the bank took the money of.</summary>
    public Guid ReceiptPaymentId { get; set; }

    public string ReceiptNumber { get; set; } = string.Empty;
    public Guid? BillId { get; set; }
    public Guid PatientId { get; set; }
    public PaymentMode Mode { get; set; }

    /// <summary>The gateway's payment id (<c>pay_…</c>) or the terminal transaction's RRN.</summary>
    public string? PaymentReference { get; set; }

    /// <summary>What the bank took back, or asks for.</summary>
    public decimal Amount { get; set; }

    /// <summary>The gateway settlement's adjustment that took the money (a gateway case).</summary>
    public Guid? GatewaySettlementLineId { get; set; }

    /// <summary>The acquirer's chargeback transaction (a terminal case).</summary>
    public Guid? PosSettlementLineId { get; set; }

    /// <summary>The gateway settlement's adjustment that gave the money back (a case the gateway's return won).</summary>
    public Guid? ReturnLineId { get; set; }

    /// <summary>The network's or the gateway's reason code (for example 10.4, 4837, or "fraudulent").</summary>
    public string? ReasonCode { get; set; }

    public string? Reason { get; set; }

    /// <summary>The last day to send the hospital's evidence, from the bank's or the gateway's notice.</summary>
    public DateOnly? RespondBy { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>Who opened it; null when HC360 opened it from a settlement.</summary>
    public string? OpenedByName { get; set; }

    public DateOnly? EvidenceSentOn { get; set; }

    /// <summary>What was sent, to whom (the bill, the receipt, the patient's consent, the discharge summary…).</summary>
    public string? EvidenceNote { get; set; }

    public string? EvidenceByName { get; set; }

    /// <summary>The last time someone asked to accept the loss, and the record the reversal was asked against.</summary>
    public DateTimeOffset? LossAskedAt { get; set; }

    public string? LossAskedByName { get; set; }
    public string? LossSubjectType { get; set; }
    public Guid? LossSubjectId { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Who closed it; null when HC360 did (the gateway returned the money, or the reversal was approved).</summary>
    public string? ClosedByName { get; set; }

    public string? Outcome { get; set; }

    /// <summary>The receipt reversal that made the loss.</summary>
    public Guid? ReversalId { get; set; }

    public string? ReversalNumber { get; set; }

    public bool IsClosed => Status is PaymentDisputeStatus.Won or PaymentDisputeStatus.Lost;

    /// <summary>What the bank's or the gateway's notice says: the reason and the date to answer by.</summary>
    public Result Describe(string? reasonCode, string? reason, DateOnly? respondBy)
    {
        if (IsClosed)
        {
            return Closed();
        }

        ReasonCode = Clip(reasonCode, 32);
        Reason = Clip(reason, NoteMaxLength);
        RespondBy = respondBy;
        return Result.Success();
    }

    /// <summary>The hospital sent its evidence on <paramref name="sentOn"/>; the case waits for the decision.</summary>
    public Result RecordEvidence(DateOnly sentOn, string note, string? byName, DateOnly today)
    {
        if (IsClosed)
        {
            return Closed();
        }

        if (sentOn > today)
        {
            return Error.Field("sentOn", "The evidence cannot have been sent after today.");
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Say what was sent, and to whom.");
        }

        EvidenceSentOn = sentOn;
        EvidenceNote = Clip(note, NoteMaxLength);
        EvidenceByName = Clip(byName, 200);
        Status = PaymentDisputeStatus.Contested;
        return Result.Success();
    }

    /// <summary>An accountant records that the bank decided for the hospital (or dropped the chargeback).</summary>
    public Result Win(string note, string? byName, DateTimeOffset now)
    {
        if (IsClosed)
        {
            return Closed();
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Say how the money came back, or how the bank decided.");
        }

        Close(PaymentDisputeStatus.Won, note, byName, now);
        return Result.Success();
    }

    /// <summary>
    /// The gateway gave the money back in a settlement (<paramref name="returnLineId"/>): the case is won without anyone
    /// saying so.
    /// </summary>
    public bool WonByReturn(string outcome, Guid returnLineId, DateTimeOffset now)
    {
        if (IsClosed)
        {
            return false;
        }

        ReturnLineId = returnLineId;
        Close(PaymentDisputeStatus.Won, outcome, null, now);
        return true;
    }

    /// <summary>Someone asked to accept the loss: a receipt reversal waits for a second person (<paramref name="subjectType"/>, <paramref name="subjectId"/>).</summary>
    public Result LossAsked(string? byName, string subjectType, Guid subjectId, DateTimeOffset now)
    {
        if (IsClosed)
        {
            return Closed();
        }

        LossAskedAt = now;
        LossAskedByName = Clip(byName, 200);
        LossSubjectType = subjectType;
        LossSubjectId = subjectId;
        return Result.Success();
    }

    /// <summary>The reversal was approved and made: the money is gone, and the bill owes it again.</summary>
    public bool Lose(Guid reversalId, string reversalNumber, DateTimeOffset at)
    {
        if (IsClosed)
        {
            return false;
        }

        ReversalId = reversalId;
        ReversalNumber = reversalNumber;
        Close(PaymentDisputeStatus.Lost, $"Receipt {ReceiptNumber} reversed ({reversalNumber}): the bill owes the money again.", null, at);
        return true;
    }

    private void Close(PaymentDisputeStatus status, string outcome, string? byName, DateTimeOffset now)
    {
        Status = status;
        Outcome = Clip(outcome, NoteMaxLength);
        ClosedByName = Clip(byName, 200);
        ClosedAt = now;
    }

    private Error Closed() => Error.Rule("billing.dispute_closed", $"Chargeback {Number} is closed: it was {(Status == PaymentDisputeStatus.Won ? "won" : "lost")}.");

    private static string? Clip(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= max ? text : text[..max];
    }
}

/// <summary>
/// When those who reconcile payments were told of a chargeback case, and the last day (and the deadline) they were
/// reminded of. Kept apart from the case, so telling them never changes a case an accountant has open. HC360 tells
/// them of a case it opened from a settlement once the case is saved (the reconciliation job), so a settlement that
/// failed to save never announces one; a case opened from the notice needs no telling.
/// </summary>
public sealed class PaymentDisputeReminder
{
    public Guid DisputeId { get; set; }

    public DateTimeOffset? AnnouncedAt { get; set; }

    /// <summary>The last day they were reminded of the deadline.</summary>
    public DateOnly? RemindedOn { get; set; }

    /// <summary>The deadline that reminder was about: a deadline changed since is reminded again the same day.</summary>
    public DateOnly? RemindedFor { get; set; }

    /// <summary>
    /// Not answered, its deadline within <see cref="PaymentDispute.RemindDays"/> days or past, and not reminded today of
    /// that deadline.
    /// </summary>
    public bool IsDue(PaymentDispute dispute, DateOnly today) =>
        dispute.Status == PaymentDisputeStatus.Open && dispute.RespondBy is { } by && by <= today.AddDays(PaymentDispute.RemindDays)
        && (RemindedOn != today || RemindedFor != by);

    public void Reminded(DateOnly today, DateOnly respondBy)
    {
        RemindedOn = today;
        RemindedFor = respondBy;
    }
}
