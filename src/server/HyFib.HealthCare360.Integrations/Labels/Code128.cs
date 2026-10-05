using System.Text;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>Code 128 code set B (printable ASCII, one character per symbol) or C (two digits per symbol).</summary>
internal enum Code128Set
{
    B,
    C,
}

/// <summary>Characters encoded in one code set, in order.</summary>
internal readonly record struct Code128Run(Code128Set Set, string Characters);

/// <summary>
/// A Code 128 symbol with its code sets chosen here rather than by the printer: B for text, C for runs of digits, with the
/// fewest symbols (the shortest barcode). Both printers are then told exactly which sets to use (ZPL invocation codes,
/// TSPL "128M" control codes), so the printed barcode is exactly <see cref="Modules"/> modules wide and HC360 can choose
/// a module width in whole printer dots that fits the label.
/// </summary>
internal sealed class Code128Plan
{
    public const int MaxLength = 48;

    private Code128Plan(string value, IReadOnlyList<Code128Run> runs, int symbols)
    {
        Value = value;
        Runs = runs;
        Modules = (11 * (symbols + 2)) + 13;
    }

    /// <summary>What the barcode says.</summary>
    public string Value { get; }

    public IReadOnlyList<Code128Run> Runs { get; }

    /// <summary>Width in modules: start, data and code-set switches, check (11 each) and stop (13). Quiet zones excluded.</summary>
    public int Modules { get; }

    /// <summary>
    /// Characters a barcode may hold: printable ASCII except <c>!</c> (TSPL's control-code prefix), <c>&gt;</c> (ZPL's), the
    /// double quote and the backslash (TSPL string syntax). Hospital numbers use capital letters, digits, <c>-</c> and <c>/</c>.
    /// </summary>
    public static bool IsEncodable(char c) => c is >= ' ' and <= '~' and not '!' and not '>' and not '"' and not '\\';

    public static bool IsEncodable(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLength && value.All(IsEncodable);

    /// <exception cref="LabelLayoutException">The value is empty, too long or has a character a barcode may not hold.</exception>
    public static Code128Plan For(string value)
    {
        if (!IsEncodable(value))
        {
            throw new LabelLayoutException(
                $"A barcode holds 1 to {MaxLength} letters, digits, spaces and common marks (not ! > \" or \\).");
        }

        var n = value.Length;
        const int B = 0;
        const int C = 1;

        // cost[i, set]: fewest symbols for value[i..] when the barcode is already in that set at position i.
        var cost = new int[n + 1, 2];
        var next = new (int Index, int Set)[n + 1, 2];
        for (var i = n - 1; i >= 0; i--)
        {
            var pair = i + 1 < n && char.IsAsciiDigit(value[i]) && char.IsAsciiDigit(value[i + 1]);

            // In set B: one character, or switch to C for a pair of digits.
            cost[i, B] = 1 + cost[i + 1, B];
            next[i, B] = (i + 1, B);
            if (pair && 2 + cost[i + 2, C] < cost[i, B])
            {
                cost[i, B] = 2 + cost[i + 2, C];
                next[i, B] = (i + 2, C);
            }

            // In set C: a pair of digits, or switch to B for one character.
            cost[i, C] = 2 + cost[i + 1, B];
            next[i, C] = (i + 1, B);
            if (pair && 1 + cost[i + 2, C] <= cost[i, C])
            {
                cost[i, C] = 1 + cost[i + 2, C];
                next[i, C] = (i + 2, C);
            }
        }

        var start = cost[0, C] < cost[0, B] ? C : B;
        var runs = new List<Code128Run>();
        var run = new StringBuilder();
        var (index, set) = (0, start);
        while (index < n)
        {
            var (to, toSet) = next[index, set];
            if (toSet != set && run.Length > 0)
            {
                runs.Add(new Code128Run(set == B ? Code128Set.B : Code128Set.C, run.ToString()));
                run.Clear();
            }

            run.Append(value, index, to - index);
            (index, set) = (to, toSet);
        }

        runs.Add(new Code128Run(set == B ? Code128Set.B : Code128Set.C, run.ToString()));
        return new Code128Plan(value, runs, cost[0, start]);
    }

    /// <summary>ZPL field data for <c>^BC…,N</c> (no mode): start <c>&gt;:</c> (B) or <c>&gt;;</c> (C), switches <c>&gt;6</c> and <c>&gt;5</c>.</summary>
    public string ZplData()
    {
        var data = new StringBuilder(Value.Length + 8);
        for (var i = 0; i < Runs.Count; i++)
        {
            var b = Runs[i].Set == Code128Set.B;
            data.Append(i == 0 ? (b ? ">:" : ">;") : (b ? ">6" : ">5")).Append(Runs[i].Characters);
        }

        return data.ToString();
    }

    /// <summary>TSPL "128M" data: start <c>!104</c> (B) or <c>!105</c> (C), switches <c>!100</c> (to B) and <c>!099</c> (to C).</summary>
    public string TsplData()
    {
        var data = new StringBuilder(Value.Length + 12);
        for (var i = 0; i < Runs.Count; i++)
        {
            var b = Runs[i].Set == Code128Set.B;
            data.Append(i == 0 ? (b ? "!104" : "!105") : (b ? "!100" : "!099")).Append(Runs[i].Characters);
        }

        return data.ToString();
    }
}
