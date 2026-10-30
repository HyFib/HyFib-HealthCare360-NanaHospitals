namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>One bill line as the calculator sees it. <paramref name="LineDiscount"/> is the staff discount in rupees.</summary>
public sealed record BillLineInput(
    decimal Gross,
    decimal PackageCovered,
    decimal TariffDiscountPercent,
    decimal LineDiscount,
    bool DiscountAllowed,
    decimal TaxRate,
    bool PayerPayable);

/// <summary>Bill-level discount: a percentage of the discountable value, or a rupee amount (percentage wins).</summary>
public sealed record BillDiscountInput(decimal? Percent, decimal? Amount)
{
    public static BillDiscountInput None { get; } = new(null, null);
}

/// <summary>Payer share terms: co-pay percentage and the payer's cap (pre-auth approval; <c>null</c> = no cap).</summary>
public sealed record SplitTerms(bool HasPayer, decimal CoPayPercent, decimal? CoverageLimit)
{
    public static SplitTerms SelfPay { get; } = new(false, 0m, null);
}

/// <param name="FreeTreatment">On a bill free of cost by law, what the line would have cost; the line itself comes to nothing.</param>
public sealed record BillLineResult(
    decimal Billable,
    decimal TariffDiscount,
    decimal LineDiscount,
    decimal BillDiscountShare,
    decimal Taxable,
    decimal Cgst,
    decimal Sgst,
    decimal Tax,
    decimal Net,
    decimal FreeTreatment = 0m);

public sealed record BillTotals(
    IReadOnlyList<BillLineResult> Lines,
    decimal Gross,
    decimal PackageCovered,
    decimal TariffDiscount,
    decimal LineDiscount,
    decimal BillDiscount,
    decimal Taxable,
    decimal Cgst,
    decimal Sgst,
    decimal Tax,
    decimal RoundOff,
    decimal Net,
    decimal PayerAmount,
    decimal PatientAmount,
    decimal CoPay,
    decimal NonPayable,
    decimal FreeTreatment = 0m)
{
    public decimal TotalDiscount => TariffDiscount + LineDiscount + BillDiscount;

    /// <summary>Discount given by staff; it needs approval above the user's limit. Tariff concessions are excluded.</summary>
    public decimal UserDiscount => LineDiscount + BillDiscount;

    /// <summary>What staff discounts are measured against: billable value after package cover and tariff concessions.</summary>
    public decimal DiscountBase => Gross - PackageCovered - TariffDiscount;
}

/// <summary>
/// Pure bill arithmetic (OPB/IPB, MB-05/06). Every line is rounded to paise with half away from zero; the bill-level
/// discount is spread over discountable lines in proportion to their value (the rounding remainder goes to the largest
/// line); GST is charged on the discounted value and split into CGST/SGST; the payer share honours co-pay, non-payable
/// items and the approved amount; and only the patient's share is rounded to the nearest rupee, because that is the
/// amount collected in cash while insurers settle to the paisa.
/// <para>
/// A credit line (negative gross: a return such as ward stock sent back to pharmacy) keeps its sign throughout: the
/// part its package absorbed goes back to the package, the rest is credited at the same tariff concession the sale had
/// and takes a proportional (negative) share of the bill discount — so no discount is ever given on goods that came
/// back — while staff line discounts never apply to credits.
/// </para>
/// <para>
/// A bill free of cost by law (<c>freeTreatment</c>: the victim of a sexual offence or an acid attack, BNSS 2023 s.397)
/// charges nothing to anyone. Package cover and tariff concessions still say what each line was worth, which is recorded
/// as its free treatment; staff discounts and the payer split do not apply, and with nothing charged there is no taxable
/// value and no tax.
/// </para>
/// </summary>
public static class BillTotalsCalculator
{
    public static BillTotals Calculate(IReadOnlyList<BillLineInput> lines, BillDiscountInput discount, SplitTerms terms, bool freeTreatment = false)
    {
        var count = lines.Count;
        var billable = new decimal[count];
        var tariff = new decimal[count];
        var lineDiscount = new decimal[count];
        var afterLine = new decimal[count];

        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            var gross = Money.Round(line.Gross);
            var tariffPercent = Math.Clamp(line.TariffDiscountPercent, 0m, 100m);
            if (gross >= 0)
            {
                billable[i] = Math.Max(gross - Math.Max(Money.Round(line.PackageCovered), 0m), 0m);
                tariff[i] = line.DiscountAllowed ? Math.Min(Money.PercentOf(billable[i], tariffPercent), billable[i]) : 0m;
                lineDiscount[i] = line.DiscountAllowed && !freeTreatment ? Math.Clamp(Money.Round(line.LineDiscount), 0m, billable[i] - tariff[i]) : 0m;
            }
            else
            {
                billable[i] = Math.Min(gross - Math.Min(Money.Round(line.PackageCovered), 0m), 0m);
                tariff[i] = line.DiscountAllowed ? Math.Max(Money.PercentOf(billable[i], tariffPercent), billable[i]) : 0m;
                lineDiscount[i] = 0m;
            }

            afterLine[i] = billable[i] - tariff[i] - lineDiscount[i];
        }

        if (freeTreatment)
        {
            return Free(lines, billable, tariff, afterLine);
        }

        var shares = AllocateBillDiscount(lines, afterLine, discount);
        var results = new List<BillLineResult>(count);
        for (var i = 0; i < count; i++)
        {
            var taxable = afterLine[i] - shares[i];
            var tax = lines[i].TaxRate > 0 ? Money.PercentOf(taxable, lines[i].TaxRate) : 0m;
            var (cgst, sgst) = Money.SplitGst(tax);
            results.Add(new BillLineResult(billable[i], tariff[i], lineDiscount[i], shares[i], taxable, cgst, sgst, tax, taxable + tax));
        }

        var netBeforeRounding = results.Sum(r => r.Net);
        var payable = terms.HasPayer ? results.Where((_, i) => lines[i].PayerPayable).Sum(r => r.Net) : 0m;
        var coPay = terms.HasPayer && payable > 0 ? Money.PercentOf(payable, Math.Clamp(terms.CoPayPercent, 0m, 100m)) : 0m;
        var cap = terms.CoverageLimit is { } limit ? Math.Max(Money.Round(limit), 0m) : decimal.MaxValue;
        var payerAmount = terms.HasPayer ? Math.Max(Math.Min(payable - coPay, cap), 0m) : 0m;

        var patientBeforeRounding = netBeforeRounding - payerAmount;
        var patientAmount = Math.Round(patientBeforeRounding, 0, MidpointRounding.AwayFromZero);
        var roundOff = patientAmount - patientBeforeRounding;

        return new BillTotals(
            results,
            Gross: lines.Sum(l => Money.Round(l.Gross)),
            PackageCovered: lines.Sum(l => Money.Round(l.Gross)) - results.Sum(r => r.Billable),
            TariffDiscount: results.Sum(r => r.TariffDiscount),
            LineDiscount: results.Sum(r => r.LineDiscount),
            BillDiscount: results.Sum(r => r.BillDiscountShare),
            Taxable: results.Sum(r => r.Taxable),
            Cgst: results.Sum(r => r.Cgst),
            Sgst: results.Sum(r => r.Sgst),
            Tax: results.Sum(r => r.Tax),
            RoundOff: roundOff,
            Net: payerAmount + patientAmount,
            PayerAmount: payerAmount,
            PatientAmount: patientAmount,
            CoPay: coPay,
            NonPayable: terms.HasPayer ? netBeforeRounding - payable : 0m);
    }

    /// <summary>A bill free of cost by law: every line comes to nothing, and what it would have cost is its free treatment.</summary>
    private static BillTotals Free(IReadOnlyList<BillLineInput> lines, decimal[] billable, decimal[] tariff, decimal[] afterLine)
    {
        var results = Enumerable.Range(0, lines.Count)
            .Select(i => new BillLineResult(billable[i], tariff[i], 0m, 0m, 0m, 0m, 0m, 0m, 0m, afterLine[i]))
            .ToList();
        var gross = lines.Sum(l => Money.Round(l.Gross));
        return new BillTotals(
            results,
            Gross: gross,
            PackageCovered: gross - results.Sum(r => r.Billable),
            TariffDiscount: results.Sum(r => r.TariffDiscount),
            LineDiscount: 0m,
            BillDiscount: 0m,
            Taxable: 0m,
            Cgst: 0m,
            Sgst: 0m,
            Tax: 0m,
            RoundOff: 0m,
            Net: 0m,
            PayerAmount: 0m,
            PatientAmount: 0m,
            CoPay: 0m,
            NonPayable: 0m,
            FreeTreatment: results.Sum(r => r.FreeTreatment));
    }

    private static decimal[] AllocateBillDiscount(IReadOnlyList<BillLineInput> lines, decimal[] afterLine, BillDiscountInput discount)
    {
        var shares = new decimal[lines.Count];

        // Credit lines are part of the basis with their negative value: the discount is a share of what was really
        // supplied, and each line's share keeps the sign (and never exceeds the size) of its value.
        var discountable = Enumerable.Range(0, lines.Count).Where(i => lines[i].DiscountAllowed && afterLine[i] != 0).ToList();
        var basis = discountable.Sum(i => afterLine[i]);
        if (basis <= 0)
        {
            return shares;
        }

        var requested = discount.Percent is { } percent
            ? Money.PercentOf(basis, Math.Clamp(percent, 0m, 100m))
            : Money.Round(Math.Max(discount.Amount ?? 0m, 0m));
        var total = Math.Min(requested, basis);
        if (total <= 0)
        {
            return shares;
        }

        var allocated = 0m;
        foreach (var i in discountable)
        {
            shares[i] = Money.Round(total * afterLine[i] / basis);
            allocated += shares[i];
        }

        var largest = discountable.MaxBy(i => afterLine[i]);
        shares[largest] = Math.Clamp(shares[largest] + (total - allocated), 0m, afterLine[largest]);
        return shares;
    }
}
