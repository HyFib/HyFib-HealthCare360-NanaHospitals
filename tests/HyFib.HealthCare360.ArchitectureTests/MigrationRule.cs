using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// What a migration's upgrade takes away that the release before it may still read or write (<see cref="MigrationPolicyTests"/>):
/// a table or a column dropped or renamed, a schema or a sequence dropped, a column narrowed (made NOT NULL, shortened, given
/// another type), the default dropped from a required column that the previous release inserts without naming, and a
/// required column added with no default, which the previous release's inserts would leave out. It reads the migration's
/// source file, so it runs without loading the server: its Up method, and the constants and helpers of the file that Up
/// names, and those they name. Down is how a developer steps back, and takes away what Up added: what only Down uses is not
/// read. SQL a migration keeps in another file is not seen.
/// </summary>
internal static partial class MigrationRule
{
    /// <summary>What the upgrade in <paramref name="source"/>, a migration's .cs file, takes away: one sentence each, in order.</summary>
    public static IReadOnlyList<string> Findings(string source)
    {
        var upgrade = new MigrationSource(source).ReachableFrom("Up");
        var created = CreatedTables(upgrade);
        var findings = new List<(int At, string Finding)>();

        foreach (var call in upgrade.Calls(DestructiveCall()))
        {
            var finding = call.Method switch
            {
                "DropTable" when created.Contains(Name(call.Text("schema"), call.Text("name"))) => null,
                "DropTable" => $"drops the table {Name(call.Text("schema"), call.Text("name"))}",
                "DropColumn" => $"drops the column {Name(call.Text("schema"), call.Text("table"), call.Text("name"))}",
                "RenameColumn" => $"renames the column {Name(call.Text("schema"), call.Text("table"), call.Text("name"))} to {call.Text("newName")}",
                "RenameTable" => $"renames the table {Name(call.Text("schema"), call.Text("name"))} to {Name(call.Text("newSchema") ?? call.Text("schema"), call.Text("newName") ?? call.Text("name"))}",
                "DropSchema" => $"drops the schema {call.Text("name")}",
                "DropSequence" => $"drops the sequence {Name(call.Text("schema"), call.Text("name"))}",
                _ => $"renames the sequence {Name(call.Text("schema"), call.Text("name"))}",
            };
            if (finding is not null)
            {
                findings.Add((call.At, finding));
            }
        }

        foreach (var call in upgrade.Calls(AlterColumnCall()))
        {
            var column = Name(call.Text("schema"), call.Text("table"), call.Text("name"));
            findings.AddRange(Narrowing(call).Select(how => (call.At, $"narrows the column {column}: {how}")));
        }

        foreach (var call in upgrade.Calls(AddColumnCall()))
        {
            if (!call.Flag("nullable") && !call.Has("defaultValue") && !call.Has("defaultValueSql") && !call.Has("computedColumnSql"))
            {
                var column = Name(call.Text("schema"), call.Text("table"), call.Text("name"));
                findings.Add((call.At, $"adds the column {column} as required with no default, which the previous release's inserts leave out"));
            }
        }

        foreach (var (at, literal) in upgrade.Strings())
        {
            foreach (var (pattern, what) in DestructiveSql)
            {
                foreach (Match match in pattern.Matches(literal))
                {
                    if (match.Groups["table"].Success && created.Contains(Name(match.Groups["table"].Value)))
                    {
                        continue;
                    }

                    findings.Add((at + match.Index, $"runs SQL that {what}: {Snippet(literal, match.Index)}"));
                }
            }
        }

        return [.. findings.OrderBy(f => f.At).Select(f => f.Finding)];
    }

    /// <summary>How an <c>AlterColumn</c> narrows its column, if it does. Widening (a longer text, a wider number, NULL allowed) is not narrowing.</summary>
    private static IEnumerable<string> Narrowing(MigrationCall call)
    {
        var nullable = call.Flag("nullable");
        if (!nullable && call.Flag("oldNullable"))
        {
            yield return "it no longer takes NULL";
        }

        if (call.Text("type") is { } type && call.Text("oldType") is { } oldType)
        {
            if (!Widens(oldType, type))
            {
                yield return $"its type goes from {oldType} to {type}";
            }
        }
        else if (call.Number("maxLength") is { } length && (call.Number("oldMaxLength") is not { } oldLength || length < oldLength))
        {
            yield return $"its length goes from {call.Number("oldMaxLength")?.ToString(CultureInfo.InvariantCulture) ?? "unlimited"} to {length}";
        }

        if (!nullable && (call.Has("oldDefaultValue") || call.Has("oldDefaultValueSql")) && !call.Has("defaultValue") && !call.Has("defaultValueSql"))
        {
            yield return "it loses the default the previous release inserts by";
        }
    }

    /// <summary>Whether a column of type <paramref name="from"/> holds everything it held once it is <paramref name="to"/>.</summary>
    internal static bool Widens(string from, string to)
    {
        var (a, b) = (Normalized(from), Normalized(to));
        if (a == b)
        {
            return true;
        }

        if (Text().Match(a) is { Success: true } oldText && Text().Match(b) is { Success: true } newText)
        {
            // character varying(n) to a longer one, to one of any length, or to text; any length to text.
            return newText.Groups["kind"].Value == "text"
                   || (oldText.Groups["kind"].Value != "text" && (!newText.Groups["length"].Success
                       || (oldText.Groups["length"].Success && Count(newText.Groups["length"]) >= Count(oldText.Groups["length"]))));
        }

        if (Integers.IndexOf(a) is >= 0 and var oldRank && Integers.IndexOf(b) is >= 0 and var newRank)
        {
            return newRank >= oldRank;
        }

        if (Numeric().Match(a) is { Success: true } oldNumber && Numeric().Match(b) is { Success: true } newNumber)
        {
            // numeric(p, s) to one with as many digits before the point and as many after, or to numeric of any size.
            if (!newNumber.Groups["precision"].Success)
            {
                return true;
            }

            if (!oldNumber.Groups["precision"].Success)
            {
                return false;
            }

            var (oldScale, newScale) = (oldNumber.Groups["scale"].Success ? Count(oldNumber.Groups["scale"]) : 0, newNumber.Groups["scale"].Success ? Count(newNumber.Groups["scale"]) : 0);
            return newScale >= oldScale && Count(newNumber.Groups["precision"]) - newScale >= Count(oldNumber.Groups["precision"]) - oldScale;
        }

        return a == "real" && b == "double precision";
    }

    /// <summary>The tables the upgrade makes itself, which it may drop again: <c>CreateTable</c>, and CREATE TABLE in its SQL.</summary>
    private static HashSet<string> CreatedTables(MigrationSource upgrade)
    {
        var created = upgrade.Calls(CreateTableCall()).Select(call => Name(call.Text("schema"), call.Text("name"))).ToHashSet(StringComparer.Ordinal);
        foreach (var (_, literal) in upgrade.Strings())
        {
            created.UnionWith(CreateTableSql().Matches(literal).Select(match => Name(match.Groups["table"].Value)));
        }

        return created;
    }

    /// <summary>
    /// The statement of <paramref name="literal"/> that holds <paramref name="index"/>, from the start of its line or the end of
    /// the statement before, whichever is later, on one line and cut short.
    /// </summary>
    private static string Snippet(string literal, int index)
    {
        var (start, end) = (Math.Max(literal.LastIndexOf(';', index), literal.LastIndexOf('\n', index)) + 1, literal.IndexOf(';', index));
        var statement = literal[start..(end < 0 ? literal.Length : end)].Trim().TrimStart('$', '@').Trim('"');
        statement = Whitespace().Replace(statement, " ").Trim();
        return statement.Length <= 100 ? statement : statement[..100] + "…";
    }

    /// <summary>A table or column's name as the findings write it: schema, table and column joined by dots, without quotes, in lower case.</summary>
    private static string Name(params string?[] parts) =>
        string.Join('.', parts.Where(part => !string.IsNullOrEmpty(part)).Select(part => part!.Replace("\"", string.Empty, StringComparison.Ordinal).ToLowerInvariant()));

    private static string Normalized(string type) => Whitespace().Replace(type.Trim().ToLowerInvariant(), " ").Replace(", ", ",", StringComparison.Ordinal).Replace("( ", "(", StringComparison.Ordinal);

    private static int Count(Group digits) => int.Parse(digits.Value, CultureInfo.InvariantCulture);

    private static readonly string[] Integers = ["smallint", "integer", "bigint"];

    /// <summary>SQL statements that take away what the previous release uses, and what each does, in the findings' words.</summary>
    private static readonly (Regex Pattern, string What)[] DestructiveSql =
    [
        (DropTableSql(), "drops a table"),
        (new(@"\bDROP\s+COLUMN\b", RegexOptions.IgnoreCase), "drops a column"),
        (new(@"\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?[\w.""%]+\s+RENAME\b(?!\s+CONSTRAINT\b)", RegexOptions.IgnoreCase), "renames a table or a column"),
        (new(@"\bALTER\s+COLUMN\s+[\w""%]+\s+(?:SET\s+DATA\s+)?TYPE\b", RegexOptions.IgnoreCase), "changes a column's type"),
        (new(@"\bALTER\s+COLUMN\s+[\w""%]+\s+SET\s+NOT\s+NULL\b", RegexOptions.IgnoreCase), "makes a column NOT NULL"),
        (new(@"\bALTER\s+COLUMN\s+[\w""%]+\s+DROP\s+DEFAULT\b", RegexOptions.IgnoreCase), "drops a column's default"),
        (new(@"\bDROP\s+SCHEMA\b", RegexOptions.IgnoreCase), "drops a schema"),
        (new(@"\bDROP\s+SEQUENCE\b", RegexOptions.IgnoreCase), "drops a sequence"),
    ];

    [GeneratedRegex(@"\b(?<method>DropTable|DropColumn|RenameColumn|RenameTable|DropSchema|DropSequence|RenameSequence)\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex DestructiveCall();

    [GeneratedRegex(@"\b(?<method>AlterColumn)\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex AlterColumnCall();

    [GeneratedRegex(@"\b(?<method>AddColumn)\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex AddColumnCall();

    [GeneratedRegex(@"\b(?<method>CreateTable)\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex CreateTableCall();

    // A table's name as written, or the placeholder that stands for it in format() (%s, %I).
    [GeneratedRegex(@"\bDROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?<table>[\w.""%]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DropTableSql();

    [GeneratedRegex(@"\bCREATE\s+(?:(?:GLOBAL|LOCAL)\s+)?(?:TEMP\s+|TEMPORARY\s+|UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>[\w.""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableSql();

    [GeneratedRegex(@"^(?<kind>character varying|varchar|text)(?:\((?<length>\d+)\))?$")]
    private static partial Regex Text();

    [GeneratedRegex(@"^(?:numeric|decimal)(?:\((?<precision>\d+)(?:,(?<scale>\d+))?\))?$")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>A call in a migration's code: the method, where it starts, and its arguments by name, as written.</summary>
internal sealed class MigrationCall(string method, int at, IReadOnlyDictionary<string, string> arguments)
{
    public string Method => method;

    public int At => at;

    public bool Has(string name) => arguments.ContainsKey(name);

    /// <summary>The argument's text when it is a plain string literal ("…" or @"…"); null otherwise.</summary>
    public string? Text(string name) =>
        arguments.TryGetValue(name, out var value) && value.TrimStart('@') is ['"', .. var inside, '"'] ? inside.Replace("\"\"", "\"", StringComparison.Ordinal) : null;

    /// <summary>Whether the argument is <c>true</c>; false when it is anything else or missing, as EF Core takes a missing flag.</summary>
    public bool Flag(string name) => arguments.TryGetValue(name, out var value) && value == "true";

    public int? Number(string name) =>
        arguments.TryGetValue(name, out var value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
}

/// <summary>
/// A C# source file read just far enough for <see cref="MigrationRule"/>: its comments blanked out, which characters are in
/// string or character literals (verbatim, interpolated and raw ones included), and calls in its code with their arguments.
/// </summary>
internal sealed partial class MigrationSource
{
    private readonly char[] _code;
    private readonly bool[] _literal;

    public MigrationSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _code = source.ToCharArray();
        _literal = new bool[_code.Length];
        Scan(0, hole: false);
    }

    private MigrationSource(char[] code, bool[] literal) => (_code, _literal) = (code, literal);

    private int Length => _code.Length;

    /// <summary>
    /// A copy that keeps only what the method <paramref name="method"/> reaches: its body, and every constant, field and
    /// method of the file that it names, and those they name; the rest is blanked out.
    /// </summary>
    public MigrationSource ReachableFrom(string method)
    {
        var (code, literal) = (new char[Length], new bool[Length]);
        Array.Fill(code, ' ');
        if (Declaration(method, members: -1) is not { } entry)
        {
            return new MigrationSource(code, literal);
        }

        var (reached, named) = (new List<(int Start, int End)>(), new HashSet<string>(StringComparer.Ordinal) { method });
        var pending = new Queue<(int Start, int End)>([entry]);
        var members = Depth(entry.Start);
        while (pending.TryDequeue(out var span))
        {
            reached.Add(span);
            foreach (var identifier in Identifiers(span))
            {
                if (named.Add(identifier) && Declaration(identifier, members) is { } member)
                {
                    pending.Enqueue(member);
                }
            }
        }

        foreach (var (start, end) in reached)
        {
            Array.Copy(_code, start, code, start, end - start + 1);
            Array.Copy(_literal, start, literal, start, end - start + 1);
        }

        return new MigrationSource(code, literal);
    }

    /// <summary>The calls in the code that <paramref name="call"/> finds (it ends at the opening parenthesis), with their arguments.</summary>
    public IEnumerable<MigrationCall> Calls(Regex call)
    {
        ArgumentNullException.ThrowIfNull(call);
        foreach (Match match in call.Matches(new string(_code)))
        {
            var open = match.Index + match.Length - 1;
            if (_literal[match.Index] || Matching(open) is not (> 0 and var close))
            {
                continue;
            }

            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var argument in Arguments(open + 1, close))
            {
                var colon = argument.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && argument[..colon].Trim() is { Length: > 0 } argumentName && argumentName.All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    arguments[argumentName] = argument[(colon + 1)..].Trim();
                }
            }

            yield return new MigrationCall(match.Groups["method"].Value, match.Index, arguments);
        }
    }

    /// <summary>Each string literal in the code, as written (quotes and prefix included), and where it starts.</summary>
    public IEnumerable<(int At, string Literal)> Strings()
    {
        for (var i = 0; i < Length; i++)
        {
            if (!_literal[i])
            {
                continue;
            }

            var start = i;
            while (i < Length && _literal[i])
            {
                i++;
            }

            yield return (start, new string(_code, start, i - start));
        }
    }

    // Where name is declared in the code: a method from its name to the end of its body, or a field or a property from its name
    // to the end of its initializer or expression. A field or property counts only among the members at depth members (as deep
    // in braces as the method reached from); with -1, only a method is looked for.
    private (int Start, int End)? Declaration(string name, int members)
    {
        var text = new string(_code);
        foreach (Match match in new Regex($@"\b{Regex.Escape(name)}\s*\(").Matches(text))
        {
            if (_literal[match.Index] || PreviousCode(match.Index - 1) == '.' || Matching(match.Index + match.Length - 1) is not (> 0 and var parameters))
            {
                continue;
            }

            // A declaration has a body (or an expression body) after its parameters; a call has neither.
            var next = NextCode(parameters + 1);
            var end = next < Length && _code[next] == '{' ? Matching(next)
                : next + 1 < Length && _code[next] == '=' && _code[next + 1] == '>' ? EndOfStatement(next) : -1;
            if (end > 0)
            {
                return (match.Index, end);
            }
        }

        if (members < 0)
        {
            return null;
        }

        foreach (Match match in new Regex($@"\b{Regex.Escape(name)}\s*(?:=(?![=>])|=>)").Matches(text))
        {
            if (!_literal[match.Index] && Depth(match.Index) == members && EndOfStatement(match.Index) is > 0 and var end)
            {
                return (match.Index, end);
            }
        }

        return null;
    }

    // The identifiers in the code of a span, outside literals.
    private IEnumerable<string> Identifiers((int Start, int End) span)
    {
        var text = new string(_code, span.Start, span.End - span.Start + 1);
        foreach (Match match in Identifier().Matches(text))
        {
            if (!_literal[span.Start + match.Index])
            {
                yield return match.Value;
            }
        }
    }

    // How deep in braces the code is at i.
    private int Depth(int i)
    {
        var depth = 0;
        for (var j = 0; j < i; j++)
        {
            if (!_literal[j])
            {
                depth += _code[j] switch { '{' => 1, '}' => -1, _ => 0 };
            }
        }

        return depth;
    }

    // The semicolon that ends the statement at i, outside literals and brackets; -1 when there is none.
    private int EndOfStatement(int i)
    {
        var depth = 0;
        for (var j = i; j < Length; j++)
        {
            if (_literal[j])
            {
                continue;
            }

            switch (_code[j])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ';' when depth == 0:
                    return j;
            }
        }

        return -1;
    }

    // The last character of code before i (literals and whitespace skipped); '\0' at the start.
    private char PreviousCode(int i)
    {
        while (i >= 0 && (_literal[i] || char.IsWhiteSpace(_code[i])))
        {
            i--;
        }

        return i >= 0 ? _code[i] : '\0';
    }

    // The arguments between from and to (exclusive), split at the commas that are not nested or in a literal.
    private List<string> Arguments(int from, int to)
    {
        var arguments = new List<string>();
        var (depth, start) = (0, from);
        for (var i = from; i < to; i++)
        {
            if (_literal[i])
            {
                continue;
            }

            switch (_code[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    arguments.Add(new string(_code, start, i - start));
                    start = i + 1;
                    break;
            }
        }

        arguments.Add(new string(_code, start, to - start));
        return arguments;
    }

    // The bracket that closes the one at open, in the code; -1 when there is none.
    private int Matching(int open)
    {
        var (opening, closing) = _code[open] switch { '(' => ('(', ')'), '{' => ('{', '}'), _ => ('[', ']') };
        var depth = 0;
        for (var i = open; i < Length; i++)
        {
            if (_literal[i])
            {
                continue;
            }

            if (_code[i] == opening)
            {
                depth++;
            }
            else if (_code[i] == closing && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private int NextCode(int from)
    {
        var i = from;
        while (i < Length && (_literal[i] || char.IsWhiteSpace(_code[i])))
        {
            i++;
        }

        return i;
    }

    private char At(int i) => i < Length ? _code[i] : '\0';

    // Code from i: comments blanked, literals marked. In an interpolation hole, it stops at the brace that closes the hole.
    private int Scan(int i, bool hole)
    {
        var depth = 0;
        while (i < Length)
        {
            var c = _code[i];
            if (c == '/' && At(i + 1) == '/')
            {
                var end = Array.IndexOf(_code, '\n', i);
                i = Blank(i, end < 0 ? Length : end);
            }
            else if (c == '/' && At(i + 1) == '*')
            {
                var end = new string(_code).IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = Blank(i, end < 0 ? Length : end + 2);
            }
            else if (LiteralStart(i) is { } literal)
            {
                i = ScanString(i, literal.Dollars, literal.Verbatim, literal.Quote);
            }
            else if (c == '\'')
            {
                i = ScanCharacter(i);
            }
            else
            {
                if (hole && c == '{')
                {
                    depth++;
                }
                else if (hole && c == '}' && depth-- == 0)
                {
                    return i;
                }

                i++;
            }
        }

        return i;
    }

    // A string literal starting at i ("…", @"…", $"…", $@"…", @$"…", """…""", $$"""…"""): its prefix and its first quote.
    private (int Dollars, bool Verbatim, int Quote)? LiteralStart(int i)
    {
        var (j, dollars, verbatim) = (i, 0, false);
        while (j < Length && (_code[j] == '$' || (_code[j] == '@' && !verbatim)))
        {
            if (_code[j] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }

            j++;
        }

        return At(j) == '"' && (j == i || dollars > 0 || verbatim) ? (dollars, verbatim, j) : null;
    }

    private int ScanString(int start, int dollars, bool verbatim, int quote)
    {
        var quotes = 0;
        while (At(quote + quotes) == '"')
        {
            quotes++;
        }

        int end;
        if (!verbatim && quotes >= 3)
        {
            // A raw string ends at the next run of as many quotes as it opened with.
            var close = new string(_code).IndexOf(new string('"', quotes), quote + quotes, StringComparison.Ordinal);
            end = close < 0 ? Length : close + quotes;
        }
        else if (!verbatim && quotes == 2)
        {
            end = quote + 2;
        }
        else
        {
            end = ScanQuoted(quote + 1, verbatim, interpolated: dollars > 0);
        }

        Array.Fill(_literal, true, start, end - start);
        return end;
    }

    private int ScanQuoted(int i, bool verbatim, bool interpolated)
    {
        while (i < Length)
        {
            var c = _code[i];
            if (!verbatim && c == '\\')
            {
                i += 2;
            }
            else if (!verbatim && c == '\n')
            {
                return i;
            }
            else if (c == '"' && verbatim && At(i + 1) == '"')
            {
                i += 2;
            }
            else if (c == '"')
            {
                return i + 1;
            }
            else if (interpolated && c == '{' && At(i + 1) == '{')
            {
                i += 2;
            }
            else if (interpolated && c == '{')
            {
                i = Scan(i + 1, hole: true) + 1;
            }
            else
            {
                i++;
            }
        }

        return Length;
    }

    private int ScanCharacter(int start)
    {
        var i = start + 1 + (At(start + 1) == '\\' ? 2 : 1);
        while (i < Length && _code[i] != '\'' && _code[i] != '\n')
        {
            i++;
        }

        var end = Math.Min(i + 1, Length);
        Array.Fill(_literal, true, start, end - start);
        return end;
    }

    private int Blank(int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (_code[i] is not ('\n' or '\r'))
            {
                _code[i] = ' ';
            }
        }

        return to;
    }

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex Identifier();
}
