using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// An integration test that registers a patient with the duplicate check on takes the first name from
/// <c>TestPatients.FirstName</c>, never a fixed one. Every test of a run registers into one database, and registration
/// refuses a patient with the same first name and date of birth as one already there (FO-01): with a fixed first name and
/// a random date of birth, two registrations now and then draw the same birthday, and the second fails with 409
/// <c>patient.possible_duplicate</c>.
/// </summary>
public sealed partial class TestPatientNameTests
{
    [Fact]
    public void Every_patient_registered_with_the_duplicate_check_on_has_a_first_name_of_its_own()
    {
        var tests = Path.Combine(RepositoryRoot(), "tests", "HyFib.HealthCare360.IntegrationTests");
        var registrations = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => Registrations(File.ReadAllText(path)).Select(r => (Path: Path.GetRelativePath(tests, path), r.Line, r.FirstName, r.Checked)))
            .ToList();

        registrations.Count(r => r.Checked).ShouldBeGreaterThan(20, "the registrations across the journeys");
        registrations.Where(r => r.Checked && r.FirstName.StartsWith('"')).Select(r => $"{r.Path}:{r.Line} FirstName: {r.FirstName}")
            .ShouldBeEmpty("Take the first name from TestPatients.FirstName, so no other patient of the run can match it");
    }

    [Fact]
    public void The_check_reads_a_registrations_first_name_and_duplicate_check()
    {
        var checkedLiteral = Registrations("""new RegisterPatientRequest(FirstName: "Lata", LastName: Name("a, b"), ConfirmedNotDuplicate: false)""").Single();
        (checkedLiteral.FirstName, checkedLiteral.Checked).ShouldBe(("\"Lata\"", true));

        var issued = Registrations("""new RegisterPatientRequest(Title: "Mrs.", FirstName: TestPatients.FirstName("Lata"), ConfirmedNotDuplicate: false)""").Single();
        (issued.FirstName, issued.Checked).ShouldBe(("TestPatients.FirstName(\"Lata\")", true));

        var confirmed = Registrations("""new RegisterPatientRequest(FirstName: "Asha", ConfirmedNotDuplicate: true)""").Single();
        confirmed.Checked.ShouldBeFalse();
    }

    /// <summary>The <c>RegisterPatientRequest</c>s built in <paramref name="code"/>: line, first name as written, and whether the duplicate check runs.</summary>
    private static IEnumerable<(int Line, string FirstName, bool Checked)> Registrations(string code)
    {
        foreach (Match match in Registration().Matches(code))
        {
            var arguments = Arguments(code, match.Index + match.Length);
            var line = code.AsSpan(0, match.Index).Count('\n') + 1;
            var firstName = arguments.FirstOrDefault(a => a.StartsWith("FirstName:", StringComparison.Ordinal))?["FirstName:".Length..].Trim() ?? "(not named)";
            var confirmed = arguments.FirstOrDefault(a => a.StartsWith("ConfirmedNotDuplicate:", StringComparison.Ordinal))?["ConfirmedNotDuplicate:".Length..].Trim();
            yield return (line, firstName, confirmed != "true");
        }
    }

    // The top-level arguments of the call whose '(' ends just before start, skipping brackets and strings inside them.
    private static List<string> Arguments(string code, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var from = start;
        char? quote = null;
        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            if (quote is { } q)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    arguments.Add(code[from..i].Trim());
                    return arguments;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(code[from..i].Trim());
                from = i + 1;
            }
        }

        throw new InvalidOperationException("A RegisterPatientRequest is not closed.");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyFib.HealthCare360.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (HyFib.HealthCare360.slnx) was not found above the test's folder.");
    }

    [GeneratedRegex(@"\bnew RegisterPatientRequest\(")]
    private static partial Regex Registration();
}
