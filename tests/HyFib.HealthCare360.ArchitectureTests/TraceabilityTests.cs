using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// docs/requirements/TRACEABILITY.md keeps up with the SRS and the code (SRS §9): every requirement has one row with one
/// of the four verdicts, a row that is not Done says what is missing, a Blocked row names who must act, and every file it
/// links to exists, so a rename or a removal cannot leave it pointing at nothing.
/// </summary>
public sealed partial class TraceabilityTests
{
    private static readonly string[] Verdicts = ["Done", "Partial", "Missing", "Blocked"];

    private static readonly Lazy<string> RootFolder = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyFib.HealthCare360.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test's folder.");
    });

    [Fact]
    public void Every_requirement_in_the_srs_has_one_row_with_a_verdict()
    {
        var required = RequirementIds(Read("docs/requirements/SRS.md"));
        var rows = Rows();

        required.Count.ShouldBeGreaterThan(150, "The SRS's requirement IDs were not found: has its table layout changed?");
        required.Where(id => rows.All(r => r.Id != id)).ShouldBeEmpty("SRS requirements with no row in TRACEABILITY.md");
        rows.Select(r => r.Id).Where(id => !required.Contains(id)).ShouldBeEmpty("Rows for requirements the SRS does not have");
        rows.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty("Requirements with more than one row");
        rows.Where(r => !Verdicts.Contains(r.Verdict)).Select(r => $"{r.Id}: {r.Verdict}").ShouldBeEmpty("Verdicts other than Done, Partial, Missing or Blocked");
    }

    [Fact]
    public void A_row_that_is_not_done_says_what_is_missing_and_a_blocked_one_who_must_act()
    {
        var rows = Rows();

        rows.Where(r => r.Verdict != "Done" && IsEmpty(r.Gap)).Select(r => r.Id).ShouldBeEmpty("Not done, and no gap given");
        rows.Where(r => r.Verdict == "Blocked" && IsEmpty(r.Who)).Select(r => r.Id).ShouldBeEmpty("Blocked, and no one named");
    }

    [Fact]
    public void Every_file_it_links_to_exists()
    {
        var folder = Path.Combine(RootFolder.Value, "docs", "requirements");
        var missing = LinkTarget().Matches(Read("docs/requirements/TRACEABILITY.md"))
            .Select(m => m.Groups["target"].Value)
            .Where(target => !target.StartsWith("http", StringComparison.Ordinal) && !target.StartsWith('#'))
            .Distinct()
            .Where(target =>
            {
                var path = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(target.Split('#')[0])));
                return !File.Exists(path) && !Directory.Exists(path);
            })
            .ToList();

        missing.ShouldBeEmpty("Links in TRACEABILITY.md to files that do not exist");
    }

    /// <summary>
    /// What the SRS names a requirement by: the first cell of a requirement table row (one ID, or a range such as
    /// <c>FO-01..06</c> where one row describes a module), and each ID that opens a sentence of the non-functional
    /// requirements' paragraphs.
    /// </summary>
    private static HashSet<string> RequirementIds(string srs)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in srs.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (TableRequirement().Match(line) is { Success: true } row)
            {
                ids.Add(row.Groups["id"].Value);
            }
            else if (ParagraphStart().IsMatch(line))
            {
                ids.UnionWith(SentenceRequirement().Matches(line).Select(m => m.Groups["id"].Value));
            }
        }

        return ids;
    }

    /// <summary>The matrix's rows: ID, priority, verdict, built in, proved by, gap, who must act.</summary>
    private static List<Row> Rows() =>
        Read("docs/requirements/TRACEABILITY.md").Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith('|'))
            .Select(line => line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray())
            .Where(cells => cells.Length == 7 && RequirementId().IsMatch(cells[0]))
            .Select(cells => new Row(cells[0], cells[2].Trim('*'), cells[5], cells[6]))
            .ToList();

    private static bool IsEmpty(string cell) => string.IsNullOrWhiteSpace(cell) || cell == "—";

    private static string Read(string path) => File.ReadAllText(Path.Combine(RootFolder.Value, path));

    private sealed record Row(string Id, string Verdict, string Gap, string Who);

    [GeneratedRegex(@"^[A-Z]{2,5}-(?:[A-Z0-9]{2,5}|\d{2}\.\.\d{2})$")]
    private static partial Regex RequirementId();

    [GeneratedRegex(@"^\|\s*(?<id>[A-Z]{2,5}-(?:[A-Z0-9]{2,5}|\d{2}\.\.\d{2}))\s*\|")]
    private static partial Regex TableRequirement();

    [GeneratedRegex(@"^[A-Z]{2,5}-\d{2} ")]
    private static partial Regex ParagraphStart();

    [GeneratedRegex(@"(?:^|(?<=\. ))(?<id>[A-Z]{2,5}-\d{2}) (?=\S)")]
    private static partial Regex SentenceRequirement();

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex LinkTarget();
}
