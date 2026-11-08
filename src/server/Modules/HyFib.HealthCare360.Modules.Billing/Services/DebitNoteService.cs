using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>The debit note asked for, exactly as it will be issued once approved: the taxable value it adds to each line.</summary>
public sealed record DebitNotePayload(Guid BillId, DebitNoteParty Party, string Reason, IReadOnlyList<DebitNoteLinePayload> Lines);

public sealed record DebitNoteLinePayload(Guid BillLineId, decimal TaxableAmount);

/// <summary>
/// Debit notes (supplementary invoices, CGST Act s.34(3)): the billing desk asks for one on a finalized bill, naming the
/// services charged short and the taxable value each is raised by; an approver, never the same person, issues it
/// (<see cref="DebitNoteApproval"/>), on the bill as it was asked against. It adds the value and its GST at each line's
/// rate to what the patient or the payer owes.
/// </summary>
public sealed class DebitNoteService(
    HospitalDbContext db,
    INumberSeries numbers,
    IAuditLogger audit,
    IApprovals approvals,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<Result<ApprovalRequestDto>> RequestAsync(Guid billId, IssueDebitNoteRequest request, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        var payload = new DebitNotePayload(bill.Id, request.Party, request.Reason.Trim(),
            request.Lines.Select(l => new DebitNoteLinePayload(l.BillLineId, Money.Round(l.TaxableAmount))).ToList());
        var lines = Lines(bill, payload);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        var checkedBill = Debitable(bill, payload.Party);
        if (checkedBill.IsFailure)
        {
            return checkedBill.Error!;
        }

        var details = new List<ApprovalDetail>
        {
            new("Bill", bill.BillNumber ?? bill.Id.ToString()),
            new("Charged to", payload.Party == DebitNoteParty.Payer ? "The payer" : "The patient"),
        };
        var total = 0m;
        foreach (var (line, taxable) in lines.Value)
        {
            var share = DebitNotes.Gst(taxable, line.TaxRate, bill.IsInterState);
            total += share.Taxable + share.Gst;
            details.Add(new(line.Description.Length <= 60 ? line.Description : line.Description[..59] + "…", share.Gst == 0m
                ? $"+₹{Money.FormatInr(share.Taxable)}"
                : $"+₹{Money.FormatInr(share.Taxable)} and GST ₹{Money.FormatInr(share.Gst)} at {line.TaxRate.ToString("0.##", CultureInfo.InvariantCulture)}%"));
        }

        details.Add(new("Debit note", $"₹{Money.FormatInr(total)}"));
        return await approvals.RequestAsync(
            DebitNoteApproval.Key,
            new ApprovalSubject(nameof(Bill), bill.Id, bill.Version, bill.FacilityId, bill.PatientId),
            payload,
            $"Debit note of ₹{Money.FormatInr(total)} on bill {bill.BillNumber}",
            details,
            request.Reason,
            ct);
    }

    internal async Task<Result<string>> IssueApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DebitNotePayload>(approval.Payload);
        var bill = await db.Set<Bill>().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == asked.BillId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), asked.BillId);
        }

        if (bill.Version != approval.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"Bill {bill.BillNumber} changed after this was asked for (a payment, a refund, a credit or another debit). Ask again, against the bill as it is now.");
        }

        var lines = Lines(bill, asked);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        var note = new DebitNote
        {
            FacilityId = bill.FacilityId,
            Number = await numbers.NextAsync(BillingNumberSeries.DebitNote, bill.FacilityId, ct),
            BillId = bill.Id,
            PatientId = bill.PatientId,
            Party = asked.Party,
            Reason = asked.Reason,
            RequestedBy = approval.RequestedBy,
            RequestedByName = approval.RequestedByName,
            IssuedBy = currentUser.UserId,
            IssuedByName = currentUser.DisplayName ?? currentUser.UserName,
            IssuedAt = time.GetUtcNow(),
        };
        foreach (var (line, taxable) in lines.Value)
        {
            note.Debit(line, taxable, bill.IsInterState);
        }

        var applied = bill.ApplyDebit(note.Amount, asked.Party == DebitNoteParty.Payer);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        // The bill's version moves with the note, so a payment or credit approved against the bill as it was is stale.
        db.Entry(bill).State = EntityState.Modified;
        db.Add(note);
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.debit_note.issued",
            $"{note.Number}: ₹{Money.FormatInr(note.Amount)} (GST ₹{Money.FormatInr(note.GstAmount)}) on bill {bill.BillNumber}, charged to the {(note.Party == DebitNoteParty.Payer ? "payer" : "patient")} — {note.Reason} (asked for by {note.RequestedByName}, approved by {note.IssuedByName})")
        {
            PatientId = bill.PatientId,
            EntityType = nameof(Bill),
            EntityId = bill.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        return note.Number;
    }

    private static Result Debitable(Bill bill, DebitNoteParty party)
    {
        if (bill.Status is not (BillStatus.Finalized or BillStatus.PartiallyPaid or BillStatus.Paid))
        {
            return Error.Rule("billing.bill_not_debitable", "Debit notes apply to finalized bills that are not cancelled.");
        }

        return party == DebitNoteParty.Payer && !bill.HasPayer
            ? Error.Rule("billing.no_payer", "This bill has no payer to raise a debit note to.")
            : Result.Success();
    }

    /// <summary>The bill lines a debit note raises: lines of this bill that were charged (not returns), each once.</summary>
    private static Result<IReadOnlyList<(BillLine Line, decimal Taxable)>> Lines(Bill bill, DebitNotePayload asked)
    {
        var lines = new List<(BillLine, decimal)>();
        foreach (var (item, index) in asked.Lines.Select((item, index) => (item, index)))
        {
            var line = bill.Lines.FirstOrDefault(l => l.Id == item.BillLineId);
            if (line is null)
            {
                return Error.Field($"lines[{index}].billLineId", "This service is not on the bill.");
            }

            if (line.Quantity < 0 || line.GrossAmount < 0)
            {
                return Error.Rule("billing.debit_on_return", $"{line.Description} is a return; a debit note raises a service charged short.");
            }

            if (asked.Party == DebitNoteParty.Payer && !line.IsPayerPayable)
            {
                return Error.Rule("billing.debit_not_payer_payable", $"The payer does not pay for {line.Description}; charge it to the patient.");
            }

            lines.Add((line, item.TaxableAmount));
        }

        return lines;
    }
}

/// <summary>A debit note on a finalized bill: asked for by one person, issued by an approver, never the same person.</summary>
internal sealed class DebitNoteApproval(DebitNoteService notes) : IApprovalAction
{
    public const string Key = "billing.debit_note";

    public string Kind => Key;

    public string Title => "Debit note";

    public string ApprovePermission => BillingPermissions.RefundsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => notes.IssueApprovedAsync(request, ct);
}

/// <summary>
/// Billing's debit notes that are e-invoices (GST rule 48(4)): a debit note charged to the payer, on a bill with GST to a
/// payer registered for GST. It goes to the portal as a <c>DBN</c> to the bill's buyer, naming the bill, each service at
/// the taxable value and GST it adds (one unit at that value).
/// </summary>
internal sealed class DebitNoteEInvoiceSource(HospitalDbContext db) : IEInvoiceSource
{
    public const string Key = "debitnote";

    public string Source => Key;

    public async Task<IReadOnlyList<EInvoiceCandidate>> ListAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (start, end) = BillingTime.Range(from, to);
        var notes = await (
                from n in db.Set<DebitNote>().AsNoTracking()
                join b in db.Set<Bill>() on n.BillId equals b.Id
                join p in db.Set<Payer>() on b.PayerId equals p.Id
                where n.FacilityId == facilityId && n.Party == DebitNoteParty.Payer && b.TaxAmount != 0 && p.Gstin != null
                      && n.IssuedAt >= start && n.IssuedAt < end
                select new { n.Id, n.Number, n.IssuedAt, p.Gstin, p.Name, n.Amount, b.BillNumber })
            .ToListAsync(cancellationToken);
        return notes
            .Select(n => new EInvoiceCandidate(Source, n.Id, n.Number, BillingTime.LocalDate(n.IssuedAt), n.Gstin!, n.Name, n.Amount, IsCancelled: false,
                EInvoiceDocumentTypes.DebitNote, n.BillNumber))
            .ToList();
    }

    public async Task<EInvoiceDocument?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var note = await db.Set<DebitNote>().AsNoTracking().Include(n => n.Lines).FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (note is not { Party: DebitNoteParty.Payer })
        {
            return null;
        }

        var bill = await db.Set<Bill>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == note.BillId, cancellationToken);
        if (bill is not { PayerId: { } payerId, FinalizedAt: { } invoiced } || bill.TaxAmount == 0)
        {
            return null;
        }

        var payer = await db.Set<Payer>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == payerId, cancellationToken);
        if (payer?.Gstin is not { } gstin)
        {
            return null;
        }

        var items = note.Lines
            .OrderBy(l => l.SortOrder)
            .Select(l => new EInvoiceItem(
                l.Description, IsService: true, l.SacCode, 1m, l.TaxableAmount, l.TaxableAmount, 0m, l.TaxableAmount, l.GstRate, l.IgstAmount,
                l.CgstAmount, l.SgstAmount, l.Amount))
            .ToList();
        return new EInvoiceDocument(
            Source, note.Id, note.FacilityId, note.Number, BillingTime.LocalDate(note.IssuedAt),
            new EInvoiceBuyer(gstin, payer.Name, payer.Address, payer.Phone, payer.Email), bill.PlaceOfSupply ?? Gstin.StateCode(gstin) ?? string.Empty,
            items, 0m, note.Amount, IsCancelled: false, Type: EInvoiceDocumentTypes.DebitNote,
            Preceding: new EInvoicePreceding(BillEInvoiceSource.Key, bill.Id, bill.BillNumber ?? string.Empty, BillingTime.LocalDate(invoiced)));
    }
}
