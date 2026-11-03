using HyFib.HealthCare360.Platform.Integrations;

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

    /// <summary>
    /// The settlement line that gave the money back: the gateway settlement's adjustment for an online payment, the
    /// acquirer's chargeback reversal for a terminal payment.
    /// </summary>
    public Guid? ReturnLineId { get; set; }

    /// <summary>
    /// The gateway's own record of this dispute (<c>disp_…</c>). The reason code, phase, status and deadline of a case
    /// that follows one are the gateway's, read by HC360 (<see cref="FollowGateway"/>).
    /// </summary>
    public string? GatewayDisputeId { get; set; }

    /// <summary>The gateway's word for where the dispute stands: <c>open</c>, <c>under_review</c>, <c>won</c>, <c>lost</c>, <c>closed</c>…</summary>
    public string? GatewayStatus { get; set; }

    /// <summary>The dispute's phase at the gateway: <c>chargeback</c>, <c>pre_arbitration</c>, <c>arbitration</c>…</summary>
    public string? GatewayPhase { get; set; }

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

    /// <summary>
    /// The gateway decided the dispute against the hospital and the loss is not booked yet: the next step is to accept it,
    /// which a second person approves. Nothing is answered any more, so the deadline is no longer reminded.
    /// </summary>
    public bool DecidedAgainst => !IsClosed && GatewayStatus == GatewayLost;

    /// <summary>The gateway's word for a dispute decided against the hospital.</summary>
    public const string GatewayLost = "lost";

    /// <summary>What the bank's or the gateway's notice says: the reason and the date to answer by.</summary>
    public Result Describe(string? reasonCode, string? reason, DateOnly? respondBy)
    {
        if (IsClosed)
        {
            return Closed();
        }

        if (GatewayDisputeId is not null && RespondBy is { } gatewayDate && respondBy != gatewayDate)
        {
            return Error.Field("respondBy", $"The gateway sets the date to answer dispute {GatewayDisputeId} by: {gatewayDate:dd-MM-yyyy}.");
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

    /// <summary>
    /// The money of a case already won (by an accountant, or by the gateway's decision) came back in a settlement
    /// (<paramref name="returnLineId"/>). False when the case is not won, or already has its money back.
    /// </summary>
    public bool Returned(Guid returnLineId)
    {
        if (Status != PaymentDisputeStatus.Won || ReturnLineId is not null)
        {
            return false;
        }

        ReturnLineId = returnLineId;
        return true;
    }

    /// <summary>
    /// What the gateway's own record of the dispute says now: read from the gateway by HC360, never taken from a
    /// webhook's body. The case takes the gateway's dispute id, status, phase, reason code and the last day to answer
    /// (<paramref name="respondBy"/>); it is contested once the evidence was submitted there (on
    /// <paramref name="submittedOn"/>), and won when the gateway decided for the hospital. A loss the gateway decided is
    /// not booked here: the case waits for an accountant to accept it and a second person to approve the reversal
    /// (<see cref="DecidedAgainst"/>). A closed case only keeps the gateway's status and phase. Returns what changed, in
    /// words, or null when nothing did: a read that changes nothing leaves the case as it was.
    /// </summary>
    public string? FollowGateway(GatewayDispute dispute, DateOnly? respondBy, DateOnly? submittedOn, DateTimeOffset now)
    {
        var changes = new List<string>();
        if (GatewayDisputeId != dispute.DisputeId)
        {
            GatewayDisputeId = dispute.DisputeId;
            changes.Add($"follows the gateway's dispute {dispute.DisputeId}");
        }

        var status = Clip(dispute.StatusText, 32);
        if (GatewayStatus != status)
        {
            GatewayStatus = status;
            changes.Add($"the gateway says {status?.Replace('_', ' ') ?? "nothing"}");
        }

        var phase = Clip(dispute.Phase, 32);
        if (GatewayPhase != phase)
        {
            GatewayPhase = phase;
            changes.Add($"phase {phase?.Replace('_', ' ') ?? "—"}");
        }

        if (!IsClosed)
        {
            if (Clip(dispute.ReasonCode, 32) is { } code && ReasonCode != code)
            {
                ReasonCode = code;
                changes.Add($"reason code {code}");
            }

            if (respondBy is { } by && RespondBy != by)
            {
                RespondBy = by;
                changes.Add($"answer by {by:dd-MM-yyyy}");
            }

            if (dispute.Status == GatewayDisputeStatus.UnderReview && Status == PaymentDisputeStatus.Open)
            {
                Status = PaymentDisputeStatus.Contested;
                EvidenceSentOn ??= submittedOn;
                EvidenceNote ??= "Submitted at the gateway.";
                changes.Add("evidence submitted at the gateway");
            }
            else if (dispute.Status == GatewayDisputeStatus.Won)
            {
                Close(PaymentDisputeStatus.Won, "The gateway decided the dispute for the hospital.", null, now);
                changes.Add("won: the gateway decided it for the hospital");
            }
        }

        return changes.Count == 0 ? null : string.Join("; ", changes);
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

    /// <summary>When they were told the gateway decided the dispute against the hospital (once).</summary>
    public DateTimeOffset? LostToldAt { get; set; }

    /// <summary>
    /// Not answered, not already decided against the hospital at the gateway, its deadline within
    /// <see cref="PaymentDispute.RemindDays"/> days or past, and not reminded today of that deadline.
    /// </summary>
    public bool IsDue(PaymentDispute dispute, DateOnly today) =>
        dispute.Status == PaymentDisputeStatus.Open && !dispute.DecidedAgainst
        && dispute.RespondBy is { } by && by <= today.AddDays(PaymentDispute.RemindDays)
        && (RemindedOn != today || RemindedFor != by);

    public void Reminded(DateOnly today, DateOnly respondBy)
    {
        RemindedOn = today;
        RemindedFor = respondBy;
    }
}
