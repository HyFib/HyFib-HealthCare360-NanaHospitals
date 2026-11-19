namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// A return (a credit: ward stock sent back unused, say) that came back after the bill that charged it was finalized.
/// <paramref name="Line"/> is the return as a line of that bill would take it: its negative gross, the part its package
/// gives back (negative or nothing), its tariff concession and GST rate, and whether the payer pays for it.
/// </summary>
public sealed record LateReturn(Guid ChargeId, Guid ServiceItemId, string Description, decimal UnitPrice, BillLineInput Line);

/// <summary>What crediting one return takes off the bill, GST included: the payer's part and the patient's.</summary>
public sealed record ReturnCreditPart(Guid ChargeId, decimal Payer, decimal Patient)
{
    public decimal Amount => Payer + Patient;
}

/// <summary>What the credit takes off one of the bill's lines, GST included, for the payer and for the patient.</summary>
public sealed record ReturnCreditLine(Guid BillLineId, decimal Payer, decimal Patient);

/// <summary>A service charged, under the name its charge gave it (the IP pharmacy's names the item and its batch).</summary>
public readonly record struct ChargedItem(Guid ServiceItemId, string Description);

/// <summary>
/// Returns credited on a finalized bill as the bill split them: <see cref="Payer"/> comes off what the payer owes and
/// <see cref="Patient"/> off what the patient owes. <see cref="Lines"/> are the bill lines the credit names: those that
/// charged what came back. A return its package absorbed gives the package its allowance back and nobody money.
/// </summary>
public sealed record ReturnCredit(IReadOnlyList<ReturnCreditPart> Returns, IReadOnlyList<ReturnCreditLine> Lines)
{
    public decimal Payer => Returns.Sum(r => r.Payer);

    public decimal Patient => Returns.Sum(r => r.Patient);

    public decimal Amount => Payer + Patient;
}

/// <summary>
/// Returns that come back after their bill was finalized. After a self-paying bill a return goes on a bill of its own (a
/// supplementary bill of the stay, the visit's next OP bill) and is refunded to the patient. After a bill a payer or a
/// package shared, that would hand the patient money the payer or the package paid: the payer's share is clamped at
/// nothing on a bill of returns, so the whole credit fell to the patient. Such a return is credited on the bill that
/// charged it, which knows the split (<see cref="Split"/>), by a credit note: the patient's part as a credit note to the
/// patient, the payer's as a payer return credit note (<see cref="CreditNoteKind.PayerReturn"/>).
/// </summary>
public static class ReturnCredits
{
    /// <summary>What the IP pharmacy puts before the item's name on a return's charge.</summary>
    private const string ReturnedPrefix = "Returned: ";

    /// <summary>
    /// Whether returns that come back after <paramref name="bill"/> was finalized are credited on it, those of what it
    /// charged (<see cref="IsCreditedOn"/>): when a payer took part of it, or a package absorbed services on it (an
    /// admission's package on its final bill, an OP bill's own).
    /// </summary>
    public static bool CreditedOnTheBill(Bill bill) =>
        bill.HasPayer || bill.PackageId is not null || bill.Lines.Any(l => l.Category == ServiceCategory.Package || l.PackageCoveredAmount != 0m);

    /// <summary>
    /// Whether <paramref name="credit"/>, a return that came back after <paramref name="billed"/> was finalized, is
    /// credited on it: the bill was shared (<see cref="CreditedOnTheBill"/>) and charged what came back. A return names
    /// what it returns, so the bill charged it when one of its lines of the same service and GST rate bears that name.
    /// When none does, the bill still charged it if it charged that service at that rate and no other charge of the stay
    /// or visit bears the name (<paramref name="chargedElsewhere"/>: what was charged and is not on this bill). The
    /// return of something charged after the bill (issued since, on a supplementary bill or waiting for one) goes on a
    /// bill of its own, with it.
    /// </summary>
    public static bool IsCreditedOn(Bill billed, Charge credit, IReadOnlyCollection<ChargedItem> chargedElsewhere)
    {
        ArgumentNullException.ThrowIfNull(billed);
        ArgumentNullException.ThrowIfNull(credit);
        ArgumentNullException.ThrowIfNull(chargedElsewhere);
        if (!credit.IsCredit || !CreditedOnTheBill(billed))
        {
            return false;
        }

        var name = ItemReturned(credit.Description);
        var charged = billed.Lines.Where(l => l.ServiceItemId == credit.ServiceItemId && l.TaxRate == credit.TaxRate && l.Quantity > 0m).ToList();
        return charged.Exists(l => string.Equals(l.Description, name, StringComparison.Ordinal))
            || (charged.Count > 0 && !chargedElsewhere.Contains(new ChargedItem(credit.ServiceItemId, name)));
    }

    /// <summary>
    /// The late charges a new bill takes after <paramref name="billed"/>, the finalized bill they follow (none: every
    /// one): all but the returns credited on it (<see cref="IsCreditedOn"/>), so all of them after a self-paying bill.
    /// </summary>
    public static IReadOnlyList<Charge> ForNewBill(Bill? billed, IReadOnlyList<Charge> late, IReadOnlyCollection<ChargedItem> chargedElsewhere) =>
        billed is null ? late : late.Where(c => !IsCreditedOn(billed, c, chargedElsewhere)).ToList();

    /// <summary>What a return names as returned: its description, less the IP pharmacy's "Returned: ".</summary>
    public static string ItemReturned(string description) =>
        description.StartsWith(ReturnedPrefix, StringComparison.Ordinal) ? description[ReturnedPrefix.Length..] : description;

    /// <summary>
    /// What crediting <paramref name="returns"/> on the finalized <paramref name="bill"/> takes off the payer's and the
    /// patient's shares, as the bill split them. Each return is taken as the bill would have taken it: its package gives
    /// back what it absorbed, the rest is credited at the sale's tariff concession with a proportional share of the bill
    /// discount, and GST at its rate. The payer's share is worked out again with the returns on the bill's co-pay and cap,
    /// so a payer capped at its approved amount still pays it, and the whole credit is the patient's. Each return's credit
    /// is set against the bill lines that charged the same service at the same GST rate (the line it came from first),
    /// none beyond what is left of it after <paramref name="creditedBefore"/>, what earlier credit notes took off each line.
    /// </summary>
    public static Result<ReturnCredit> Split(Bill bill, IReadOnlyList<LateReturn> returns, IReadOnlyDictionary<Guid, decimal> creditedBefore)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(returns);
        ArgumentNullException.ThrowIfNull(creditedBefore);

        // A bill free of cost by law charged nothing, so a return of it gives nothing back.
        var free = bill.FreeTreatmentReason is not null;
        var discountBase = bill.Lines.Where(l => l.IsDiscountAllowed).Sum(l => l.TaxableAmount + l.BillDiscountShare);
        var amounts = returns.Select(r => free ? 0m : -Net(r.Line, bill.BillDiscountAmount, discountBase)).ToArray();
        var payer = free ? 0m : PayerPart(bill, returns, amounts);

        // The payer's part goes to the returns it pays for, in proportion; the rest of each is the patient's.
        var payerShares = new decimal[returns.Count];
        var payable = Enumerable.Range(0, returns.Count).Where(i => returns[i].Line.PayerPayable && amounts[i] > 0m).ToList();
        if (payer > 0m && payable.Count > 0)
        {
            var basis = payable.Sum(i => amounts[i]);
            foreach (var i in payable)
            {
                payerShares[i] = Money.Round(payer * amounts[i] / basis);
            }

            var largest = payable.MaxBy(i => amounts[i]);
            payerShares[largest] += payer - payerShares.Sum();
            foreach (var i in payable)
            {
                payerShares[i] = Math.Clamp(payerShares[i], 0m, amounts[i]);
            }
        }

        var parts = returns.Select((r, i) => new ReturnCreditPart(r.ChargeId, payerShares[i], amounts[i] - payerShares[i])).ToList();
        var left = bill.Lines.ToDictionary(l => l.Id, l => l.NetAmount - creditedBefore.GetValueOrDefault(l.Id));
        var credited = new Dictionary<Guid, (decimal Payer, decimal Patient)>();
        for (var i = 0; i < returns.Count; i++)
        {
            if (parts[i].Amount == 0m)
            {
                continue;
            }

            var candidates = Candidates(bill, returns[i]);
            var unplaced = Place(candidates, left, credited, parts[i].Payer, payer: true) + Place(candidates, left, credited, parts[i].Patient, payer: false);
            if (unplaced > 0m)
            {
                return Error.Rule("billing.return_not_on_bill",
                    $"{returns[i].Description} is not on bill {bill.BillNumber}, or is credited on it already, so its return cannot be credited on it.");
            }
        }

        return new ReturnCredit(parts, credited.Select(c => new ReturnCreditLine(c.Key, c.Value.Payer, c.Value.Patient)).ToList());
    }

    /// <summary>A return's value as a line of the bill: negative, GST included (as <see cref="BillTotalsCalculator"/> takes a credit line).</summary>
    private static decimal Net(BillLineInput line, decimal billDiscount, decimal discountBase)
    {
        var gross = Money.Round(line.Gross);
        var billable = Math.Min(gross - Math.Min(Money.Round(line.PackageCovered), 0m), 0m);
        var tariff = line.DiscountAllowed ? Math.Max(Money.PercentOf(billable, Math.Clamp(line.TariffDiscountPercent, 0m, 100m)), billable) : 0m;
        var afterLine = billable - tariff;
        var share = line.DiscountAllowed && discountBase > 0m ? Math.Max(Money.Round(afterLine * billDiscount / discountBase), afterLine) : 0m;
        var taxable = afterLine - share;
        var tax = line.TaxRate > 0m ? Money.PercentOf(taxable, line.TaxRate) : 0m;
        return taxable + tax;
    }

    /// <summary>How much less the payer pays once the returns are taken off what it pays for, on the bill's co-pay and cap.</summary>
    private static decimal PayerPart(Bill bill, IReadOnlyList<LateReturn> returns, decimal[] amounts)
    {
        if (!bill.HasPayer)
        {
            return 0m;
        }

        var returned = Enumerable.Range(0, returns.Count).Where(i => returns[i].Line.PayerPayable).Sum(i => amounts[i]);
        var payable = bill.Lines.Where(l => l.IsPayerPayable).Sum(l => l.NetAmount) - returned;
        var coPay = payable > 0m ? Money.PercentOf(payable, Math.Clamp(bill.CoPayPercent, 0m, 100m)) : 0m;
        var payerAfter = Math.Max(Math.Min(payable - coPay, bill.PayerCoverageLimit ?? decimal.MaxValue), 0m);
        return Math.Clamp(bill.PayerAmount - payerAfter, 0m, returned);
    }

    /// <summary>
    /// The bill lines a return is credited against: charged lines of the same service and GST rate, worth something; the
    /// one it came from first (the same item, named as the return names it), then at its price, then the latest.
    /// </summary>
    private static List<BillLine> Candidates(Bill bill, LateReturn returned)
    {
        var name = ItemReturned(returned.Description);
        return bill.Lines
            .Where(l => l.ServiceItemId == returned.ServiceItemId && l.TaxRate == returned.Line.TaxRate && l.Quantity > 0m && l.NetAmount > 0m)
            .OrderByDescending(l => string.Equals(l.Description, name, StringComparison.Ordinal))
            .ThenByDescending(l => l.UnitPrice == returned.UnitPrice)
            .ThenByDescending(l => l.SortOrder)
            .ToList();
    }

    /// <summary>Sets <paramref name="amount"/> against the candidates, each up to what is left of it; returns what found no room.</summary>
    private static decimal Place(
        List<BillLine> candidates, Dictionary<Guid, decimal> left, Dictionary<Guid, (decimal Payer, decimal Patient)> credited, decimal amount, bool payer)
    {
        foreach (var line in candidates)
        {
            if (amount <= 0m)
            {
                break;
            }

            var room = Math.Min(left[line.Id], amount);
            if (room <= 0m)
            {
                continue;
            }

            var before = credited.GetValueOrDefault(line.Id);
            credited[line.Id] = payer ? (before.Payer + room, before.Patient) : (before.Payer, before.Patient + room);
            left[line.Id] -= room;
            amount -= room;
        }

        return amount;
    }
}
