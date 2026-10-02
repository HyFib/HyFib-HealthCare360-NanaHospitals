using HyFib.HealthCare360.Platform.Modules;

namespace HyFib.HealthCare360.Platform.Numbering;

/// <summary>
/// A kind of number the hospital issues (UHID, OP bill, accession number…). Each module declares its series once, in
/// <see cref="IModule.NumberSeries"/>, and passes the definition to every <see cref="INumberSeries.NextAsync"/> call, so
/// the administrators' list of number formats is complete before any number is issued.
/// </summary>
/// <param name="Key">Stable key of the series' counter (<c>platform.number_series.key</c>), for example "billing.bill.op".</param>
/// <param name="Name">What staff call these numbers, in plain words: "OP bill", "UHID (patient number)".</param>
/// <param name="Area">The part of the system that issues them, as the staff app's menu names it: "Billing", "Laboratory".</param>
/// <param name="DefaultFormat">The format until an administrator changes it (see <see cref="NumberFormat"/> for the parts).</param>
/// <param name="DefaultReset">When counting starts again from 1, until an administrator changes it.</param>
/// <param name="MaxLength">
/// The length of the shortest column the numbers are stored in: no format may make longer numbers.
/// </param>
public sealed record NumberSeriesDefinition(
    string Key,
    string Name,
    string Area,
    string DefaultFormat,
    NumberResetPolicy DefaultReset,
    int MaxLength)
{
    /// <summary>True: each facility counts its own numbers in its own format. False: one counter for all facilities.</summary>
    public bool PerFacility { get; init; } = true;

    /// <summary>
    /// Null when administrators may change the format. Otherwise, in a plain sentence, why it is fixed (other systems read
    /// numbers of this shape). The next number of a locked series can still be raised.
    /// </summary>
    public string? LockedReason { get; init; }

    /// <summary>An internal series that administrators never see, such as a department's daily queue tokens.</summary>
    public bool Hidden { get; init; }

    public bool IsLocked => LockedReason is not null;
}

/// <summary>Every number series the loaded modules declare (hidden ones with a key per use, such as queue tokens, excepted).</summary>
public sealed class NumberSeriesCatalog
{
    private readonly Dictionary<string, NumberSeriesDefinition> _byKey = new(StringComparer.Ordinal);

    public NumberSeriesCatalog(IEnumerable<NumberSeriesDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            if (!_byKey.TryAdd(definition.Key, definition))
            {
                throw new InvalidOperationException($"Number series '{definition.Key}' is declared more than once.");
            }
        }
    }

    public IReadOnlyCollection<NumberSeriesDefinition> All => _byKey.Values;

    public NumberSeriesDefinition? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>True when <paramref name="series"/> is the declared definition of its key.</summary>
    public bool IsDeclared(NumberSeriesDefinition series) => _byKey.TryGetValue(series.Key, out var declared) && declared == series;
}
