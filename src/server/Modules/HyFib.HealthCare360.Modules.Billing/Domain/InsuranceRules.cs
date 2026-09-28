namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>How much of a bill a payer can be asked to cover (IPB-06, INS).</summary>
public static class PayerCoverage
{
    /// <summary>
    /// Insurers pay only what they approved (no approval = nothing; the patient pays and claims reimbursement).
    /// Corporates and government schemes extend credit per agreement: capped when an approval exists, otherwise open.
    /// </summary>
    public static decimal? CoverageLimit(PayerType payerType, decimal? approvedAmount) => payerType switch
    {
        PayerType.Insurance => Math.Max(approvedAmount ?? 0m, 0m),
        PayerType.Corporate or PayerType.GovernmentScheme => approvedAmount is { } approved ? Math.Max(approved, 0m) : null,
        _ => 0m,
    };
}

public sealed record ReceivableItem(Guid PayerId, decimal Outstanding, DateOnly Since);

public sealed record AgeingBuckets(decimal Days0To30, decimal Days31To60, decimal Days61To90, decimal Over90)
{
    public static AgeingBuckets Empty { get; } = new(0m, 0m, 0m, 0m);

    public decimal Total => Days0To30 + Days31To60 + Days61To90 + Over90;

    public AgeingBuckets Add(int ageDays, decimal amount) => ageDays switch
    {
        <= 30 => this with { Days0To30 = Days0To30 + amount },
        <= 60 => this with { Days31To60 = Days31To60 + amount },
        <= 90 => this with { Days61To90 = Days61To90 + amount },
        _ => this with { Over90 = Over90 + amount },
    };
}

/// <summary>Receivable ageing by payer (INS-07): 0–30, 31–60, 61–90 and over 90 days since the bill date.</summary>
public static class ReceivablesAgeing
{
    public static IReadOnlyDictionary<Guid, AgeingBuckets> ByPayer(IEnumerable<ReceivableItem> items, DateOnly asOf)
    {
        var result = new Dictionary<Guid, AgeingBuckets>();
        foreach (var item in items.Where(i => i.Outstanding > 0))
        {
            var age = Math.Max(asOf.DayNumber - item.Since.DayNumber, 0);
            result[item.PayerId] = result.GetValueOrDefault(item.PayerId, AgeingBuckets.Empty).Add(age, item.Outstanding);
        }

        return result;
    }
}
