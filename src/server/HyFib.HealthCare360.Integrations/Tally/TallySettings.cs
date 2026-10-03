namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>Tally configuration (<c>integrations.tally</c>), INT-TALLY. Tally's XML port has no authentication.</summary>
internal sealed record TallySettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>tally</c> or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>Tally Prime's HTTP/XML server (F1 → Settings → Connectivity), usually on the accounts PC.</summary>
    public string Url { get; init; } = "http://localhost:9000";

    /// <summary>Company to post into (<c>SVCURRENTCOMPANY</c>); must be open in Tally. Empty = the active company.</summary>
    public string? CompanyName { get; init; }

    /// <summary>HC360 ledger name → Tally ledger name.</summary>
    public IReadOnlyDictionary<string, string>? LedgerMap { get; init; }

    public string CostCategory { get; init; } = "Primary Cost Category";

    /// <summary>Vouchers per import request (Tally imports large envelopes slowly).</summary>
    public int BatchSize { get; init; } = 50;
}

/// <summary>Tally's vouchers of one day, or why they could not be read.</summary>
internal sealed record TallyDayRead(bool Success, string? Error, IReadOnlyList<TallyVoucherCopy> Vouchers)
{
    public static TallyDayRead Failed(string error) => new(false, error, []);
}

internal interface IAccountingProvider
{
    string Key { get; }

    Task<bool> PingAsync(TallySettings settings, CancellationToken cancellationToken);

    /// <summary>Imports the vouchers and answers for each one (see <see cref="AccountingExportResult.Vouchers"/>).</summary>
    Task<AccountingExportResult> PushVouchersAsync(IReadOnlyList<AccountingVoucher> vouchers, TallySettings settings, CancellationToken cancellationToken);

    /// <summary>Every voucher the company holds on <paramref name="date"/>, for the read-back check.</summary>
    Task<TallyDayRead> ReadDayAsync(DateOnly date, TallySettings settings, CancellationToken cancellationToken);
}
