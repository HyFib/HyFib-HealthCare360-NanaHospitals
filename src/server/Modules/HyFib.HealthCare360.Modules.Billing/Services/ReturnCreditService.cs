using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>The credit of returns asked for: the bill, the returns it credits, and the split they were asked with.</summary>
public sealed record ReturnCreditPayload(Guid BillId, string Reason, IReadOnlyList<Guid> ChargeIds, decimal Payer, decimal Patient);

/// <summary>
/// Returns that came back after a bill a payer or a package shared was finalized (<see cref="ReturnCredits"/>): they are
/// kept off any later bill, where the patient would be refunded what the payer or the package paid, and credited on the
/// bill that charged them, which splits the credit as it split the sale. The billing desk asks; an approver, never the
/// same person, issues it (<see cref="ReturnCreditApproval"/>), through the credit-note flow: the patient's share as a
/// credit note to the patient, the payer's share as a payer return credit note (<see cref="CreditNoteKind.PayerReturn"/>),
/// each naming the services that came back with their GST. The returns are then billed on that bill, and leave the stay's
/// or the visit's unbilled services.
/// </summary>
public sealed class ReturnCreditService(
    HospitalDbContext db,
    PayerCoverageService coverage,
    INumberSeries numbers,
    IApprovals approvals,
    IEInvoiceQueue einvoices,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>
    /// The returns waiting to be credited on <paramref name="bill"/>: credits of its stay (a final bill) or its visit (an
    /// OP bill), on no bill yet, of what the bill charged (<see cref="ReturnCredits.IsCreditedOn"/>). None unless the bill
    /// is finalized and a payer or a package shared it.
    /// </summary>
    internal static async Task<List<Charge>> PendingAsync(HospitalDbContext db, Bill bill, CancellationToken ct)
    {
        if (bill.Status is not (BillStatus.Finalized or BillStatus.PartiallyPaid or BillStatus.Paid) || !ReturnCredits.CreditedOnTheBill(bill))
        {
            return [];
        }

        var services = bill.Lines.Where(l => l.Quantity > 0m).Select(l => l.ServiceItemId).Distinct().ToList();
        var credits = await OfTheBillsCare(bill, db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]))
            .Where(c => c.Status == ChargeStatus.Unbilled && c.BillId == null && c.Quantity < 0m && services.Contains(c.ServiceItemId))
            .OrderBy(c => c.ServiceDate).ThenBy(c => c.Id)
            .ToListAsync(ct);
        if (credits.Count == 0)
        {
            return credits;
        }

        var elsewhere = await ChargedElsewhereAsync(db, bill, ct);
        return credits.Where(c => ReturnCredits.IsCreditedOn(bill, c, elsewhere)).ToList();
    }

    /// <summary>
    /// What the stay (of a final bill) or the visit (of an OP bill) was charged and <paramref name="bill"/> does not hold:
    /// each service under the name its charge gave it, billed on another bill or waiting for one. A return of one of them
    /// goes with it, not on this bill (<see cref="ReturnCredits.IsCreditedOn"/>).
    /// </summary>
    internal static async Task<List<ChargedItem>> ChargedElsewhereAsync(HospitalDbContext db, Bill bill, CancellationToken ct)
    {
        var charged = await OfTheBillsCare(bill, db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]))
            .Where(c => c.Quantity > 0m && c.Status != ChargeStatus.Cancelled && c.BillId != bill.Id)
            .Select(c => new { c.ServiceItemId, c.Description })
            .Distinct()
            .ToListAsync(ct);
        return charged.Select(c => new ChargedItem(c.ServiceItemId, c.Description)).ToList();
    }

    /// <summary>The charges of <paramref name="bill"/>'s stay (a final bill) or visit (an OP bill); none for any other bill.</summary>
    private static IQueryable<Charge> OfTheBillsCare(Bill bill, IQueryable<Charge> charges) =>
        bill.IsFinalBill && bill.AdmissionId is { } admissionId ? charges.Where(c => c.AdmissionId == admissionId)
        : bill.Type == BillType.OutPatient && bill.VisitId is { } visitId ? charges.Where(c => c.AdmissionId == null && c.VisitId == visitId)
        : charges.Where(_ => false);

    /// <summary>
    /// Asks for the returns waiting on a finalized bill to be credited on it. Nothing changes until an approver, someone
    /// other than whoever asks, approves; the approver sees each return and what the payer and the patient get back.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> RequestAsync(Guid billId, CreditReturnsRequest request, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        var worked = await WorkOutAsync(bill, ct);
        if (worked.IsFailure)
        {
            return worked.Error!;
        }

        var (returns, credit) = worked.Value;
        var details = new List<ApprovalDetail> { new("Bill", bill.BillNumber ?? bill.Id.ToString()) };
        foreach (var (charge, part) in returns.Zip(credit.Returns))
        {
            details.Add(new(Shorten(charge.Description), part.Amount == 0m
                ? "Absorbed by the package: its allowance comes back, and nobody is paid"
                : $"₹{Money.FormatInr(part.Amount)}: the payer's ₹{Money.FormatInr(part.Payer)}, the patient's ₹{Money.FormatInr(part.Patient)}"));
        }

        details.Add(new("Off what the payer owes", $"₹{Money.FormatInr(credit.Payer)}"));
        details.Add(new("Off what the patient owes", $"₹{Money.FormatInr(credit.Patient)}"));
        var payload = new ReturnCreditPayload(bill.Id, request.Reason.Trim(), returns.Select(c => c.Id).ToList(), credit.Payer, credit.Patient);
        return await approvals.RequestAsync(
            ReturnCreditApproval.Key,
            new ApprovalSubject(nameof(Bill), bill.Id, bill.Version, bill.FacilityId, bill.PatientId),
            payload,
            $"Credit of ₹{Money.FormatInr(credit.Amount)} for {Count(returns.Count)} on bill {bill.BillNumber}",
            details,
            request.Reason,
            ct);
    }

    /// <summary>
    /// Issues an approved credit of returns, in the approval's transaction (the approval saves), on the bill as it was asked
    /// against and for the same returns and split; <c>approval.stale</c> otherwise.
    /// </summary>
    internal async Task<Result<string>> IssueApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ReturnCreditPayload>(approval.Payload);
        var bill = await db.Set<Bill>().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == asked.BillId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), asked.BillId);
        }

        if (bill.Version != approval.SubjectVersion)
        {
            return Stale(bill);
        }

        var worked = await WorkOutAsync(bill, ct);
        if (worked.IsFailure)
        {
            return worked.Error!;
        }

        var (returns, credit) = worked.Value;
        if (!returns.Select(c => c.Id).Order().SequenceEqual(asked.ChargeIds.Order()) || credit.Payer != asked.Payer || credit.Patient != asked.Patient)
        {
            return Stale(bill);
        }

        var timeBarred = BillingTime.Today(time) > GstCreditNotes.LastDay(BillingTime.LocalDate(bill.FinalizedAt ?? bill.CreatedAt));
        var before = await CreditedBeforeAsync(bill, ct);
        var issued = new List<CreditNote>();
        if (credit.Patient > 0m)
        {
            var applied = bill.ApplyCredit(credit.Patient);
            if (applied.IsFailure)
            {
                return applied.Error!;
            }

            issued.Add(await IssueAsync(bill, CreditNoteKind.CreditNote, credit.Patient, credit.Lines.Select(l => (l.BillLineId, l.Patient)), before, asked.Reason,
                approval, timeBarred, ct));
        }

        if (credit.Payer > 0m)
        {
            var applied = bill.ApplyPayerCredit(credit.Payer);
            if (applied.IsFailure)
            {
                return applied.Error!;
            }

            issued.Add(await IssueAsync(bill, CreditNoteKind.PayerReturn, credit.Payer, credit.Lines.Select(l => (l.BillLineId, l.Payer)), before, asked.Reason,
                approval, timeBarred, ct));
        }

        // The returns are billed on the bill that charged them, with nothing left to bill; the bill's version moves, so a
        // second credit asked against the bill as it was is stale.
        foreach (var charge in returns)
        {
            var reserved = charge.ReserveFor(bill.Id);
            if (reserved.IsFailure)
            {
                return reserved.Error!;
            }

            charge.MarkBilled();
        }

        db.Entry(bill).State = EntityState.Modified;
        foreach (var note in issued.Where(n => n.Lines.Count > 0 && !timeBarred && bill.TaxAmount != 0m && bill.PayerId is not null))
        {
            // A credit note on a B2B bill is an e-invoice too (GST rule 48(4)): its IRN is asked for once it is issued.
            await einvoices.QueueAsync(CreditNoteEInvoiceSource.Key, note.Id, note.FacilityId, BillingTime.LocalDate(note.IssuedAt), ct);
        }

        var outcome = issued.Count == 0
            ? $"{Count(returns.Count)} absorbed by the package on bill {bill.BillNumber}: nothing to credit"
            : string.Join(" and ", issued.Select(n => $"{n.Number} (₹{Money.FormatInr(n.Amount)} {(n.Kind == CreditNoteKind.PayerReturn ? "to the payer" : "to the patient")})"));
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.returns.credited",
            Fit($"Returns credited on bill {bill.BillNumber}: {outcome} — {asked.Reason} (asked for by {approval.RequestedByName}, approved by {CheckerName()})", 1000))
        {
            PatientId = bill.PatientId,
            EntityType = nameof(Bill),
            EntityId = bill.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { asked.ChargeIds, credit.Payer, credit.Patient, notes = issued.Select(n => n.Number).ToList() },
        });
        return outcome;
    }

    /// <summary>
    /// The returns waiting on the bill and what crediting them takes off the payer and the patient, if they can be credited
    /// on it now: the bill finalized and shared by a payer or a package, the patient's share within what the patient owes
    /// on it, and the payer's within what the payer still owes, with no claim of the bill under way.
    /// </summary>
    private async Task<Result<(List<Charge> Returns, ReturnCredit Credit)>> WorkOutAsync(Bill bill, CancellationToken ct)
    {
        if (bill.Status is not (BillStatus.Finalized or BillStatus.PartiallyPaid or BillStatus.Paid))
        {
            return Error.Rule("billing.bill_not_creditable", "Returns are credited on a finalized bill that is not cancelled.");
        }

        if (!ReturnCredits.CreditedOnTheBill(bill))
        {
            return Error.Rule("billing.returns_not_credited_here",
                $"No payer or package shared bill {bill.BillNumber}: a return after it goes on a bill of its own, and the patient is refunded.");
        }

        var returns = await PendingAsync(db, bill, ct);
        if (returns.Count == 0)
        {
            return Error.Rule("billing.no_returns_to_credit", $"No returns wait to be credited on bill {bill.BillNumber}.");
        }

        var cover = await PackageCoverAsync(bill, returns, ct);
        var isNonPayable = await coverage.NonPayableMatcherAsync(bill.HasPayer ? bill.PayerId : null, ct);
        var late = returns.Select(c => new LateReturn(c.Id, c.ServiceItemId, c.Description, c.UnitPrice,
                new BillLineInput(c.GrossAmount, cover.GetValueOrDefault(c.Id), c.DiscountPercent, 0m, c.IsDiscountAllowed, c.TaxRate,
                    !isNonPayable(c.ServiceItemId, c.Category))))
            .ToList();
        var split = ReturnCredits.Split(bill, late, await CreditedBeforeAsync(bill, ct));
        if (split.IsFailure)
        {
            return split.Error!;
        }

        var credit = split.Value;
        if (credit.Patient > 0m && bill.CanCredit(credit.Patient) is { IsFailure: true } patient)
        {
            return patient.Error!;
        }

        if (credit.Payer > 0m)
        {
            var claim = await db.Set<InsuranceClaim>().AsNoTracking()
                .Where(c => c.BillId == bill.Id && c.Status != ClaimStatus.Rejected && c.Status != ClaimStatus.Settled)
                .Select(c => c.ClaimNumber)
                .FirstOrDefaultAsync(ct);
            if (claim is not null)
            {
                return Error.Rule("billing.return_claim_open",
                    $"Claim {claim} is under way for the payer's share of bill {bill.BillNumber}, which the returns lower by ₹{Money.FormatInr(credit.Payer)}. " +
                    "Credit them once the insurer has settled or rejected the claim; tell the insurer meanwhile, so it pays for what was used.");
            }

            if (credit.Payer > bill.PayerOutstanding)
            {
                return Error.Rule("billing.payer_credit_exceeds_owed",
                    $"The payer owes ₹{Money.FormatInr(bill.PayerOutstanding)} on bill {bill.BillNumber}, less than its ₹{Money.FormatInr(credit.Payer)} share of what came back. " +
                    "Its claim was settled already: what it paid for the returns is taken up with it through the claim desk.");
            }
        }

        return (returns, credit);
    }

    /// <summary>
    /// What the bill's package gives back of each return (negative): the stay's package on a final bill, the bill's own on
    /// an OP bill, worked out over the bill's services and the returns as the bill worked it out (<see cref="PackageCalculator"/>).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, decimal>> PackageCoverAsync(Bill bill, List<Charge> returns, CancellationToken ct)
    {
        var packageId = bill.PackageId ?? (bill.IsFinalBill && bill.IpAccountId is { } accountId
            ? await db.Set<IpAccount>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(a => a.Id == accountId).Select(a => a.PackageId).FirstOrDefaultAsync(ct)
            : null);
        var package = packageId is { } id
            ? await db.Set<Package>().AsNoTracking().Include(p => p.Inclusions).FirstOrDefaultAsync(p => p.Id == id, ct)
            : null;
        if (package is null)
        {
            return new Dictionary<Guid, decimal>();
        }

        var usages = bill.Lines.Where(l => l.Category != ServiceCategory.Package)
            .Select(l => new PackageUsage(l.Id, l.ServiceItemId, l.Category, l.Quantity, l.UnitPrice, l.GrossAmount, l.ServiceDate))
            .Concat(returns.Select(c => new PackageUsage(c.Id, c.ServiceItemId, c.Category, c.Quantity, c.UnitPrice, c.GrossAmount, c.ServiceDate)));
        var returned = returns.Select(c => c.Id).ToHashSet();
        return PackageCalculator.Allocate(package.Price, package.Rules(), usages).Lines
            .Where(a => returned.Contains(a.Key))
            .ToDictionary(a => a.Key, a => a.Covered);
    }

    /// <summary>What earlier credit notes took off each of the bill's lines.</summary>
    private async Task<Dictionary<Guid, decimal>> CreditedBeforeAsync(Bill bill, CancellationToken ct)
    {
        var ids = bill.Lines.Select(l => l.Id).ToList();
        return await db.Set<CreditNoteLine>().AsNoTracking()
            .Where(l => ids.Contains(l.BillLineId))
            .GroupBy(l => l.BillLineId)
            .Select(g => new { g.Key, Amount = g.Sum(l => l.Amount) })
            .ToDictionaryAsync(g => g.Key, g => g.Amount, ct);
    }

    /// <summary>A credit note of the bill for <paramref name="amount"/>, crediting each line its part at the line's own GST.</summary>
    private async Task<CreditNote> IssueAsync(
        Bill bill, CreditNoteKind kind, decimal amount, IEnumerable<(Guid BillLineId, decimal Amount)> lines, Dictionary<Guid, decimal> before, string reason,
        ApprovalRequest approval, bool timeBarred, CancellationToken ct)
    {
        var note = new CreditNote
        {
            FacilityId = bill.FacilityId,
            Number = await numbers.NextAsync(BillingNumberSeries.CreditNote, bill.FacilityId, ct),
            Kind = kind,
            BillId = bill.Id,
            PatientId = bill.PatientId,
            Amount = amount,
            Reason = Fit($"Returned after the bill: {reason}", 300),
            RequestedBy = approval.RequestedBy,
            RequestedByName = approval.RequestedByName,
            IssuedBy = currentUser.UserId,
            IssuedByName = CheckerName(),
            IssuedAt = time.GetUtcNow(),
        };
        foreach (var (lineId, credited) in lines.Where(l => l.Amount > 0m))
        {
            var line = bill.Lines.Single(l => l.Id == lineId);
            note.Credit(line, before.GetValueOrDefault(lineId), credited, bill.IsInterState, timeBarred);
            before[lineId] = before.GetValueOrDefault(lineId) + credited;
        }

        db.Add(note);
        return note;
    }

    private static Error Stale(Bill bill) => Error.Rule("approval.stale",
        $"Bill {bill.BillNumber} or the returns waiting on it changed after this was asked for (a payment, a credit, another return). Ask again, against the bill as it is now.");

    private string CheckerName() => currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";

    private static string Count(int returns) => returns == 1 ? "1 return" : string.Create(CultureInfo.InvariantCulture, $"{returns} returns");

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..59] + "…";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>
/// Returns credited on the finalized bill that charged them (<see cref="ReturnCreditService"/>): the billing desk asks, and
/// an approver, never the same person, issues the credit notes, as with any credit note.
/// </summary>
internal sealed class ReturnCreditApproval(ReturnCreditService returns) : IApprovalAction
{
    public const string Key = "billing.return_credit";

    public string Kind => Key;

    public string Title => "Returns credited on a bill";

    public string ApprovePermission => BillingPermissions.RefundsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => returns.IssueApprovedAsync(request, ct);
}
