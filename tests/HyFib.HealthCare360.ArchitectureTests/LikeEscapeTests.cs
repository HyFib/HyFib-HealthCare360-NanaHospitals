using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// Every <c>EF.Functions.ILike</c> and <c>EF.Functions.Like</c> names its escape character. Without one Npgsql writes
/// <c>ESCAPE ''</c>, so the backslashes <c>ToLikePattern</c> and <c>ToLikeLiteral</c> put before a term's own <c>%</c>,
/// <c>_</c> and <c>\</c> are matched as themselves and the search finds nothing (LikeSearchTests show it against
/// PostgreSQL). Pass <c>QueryExtensions.LikeEscape</c>.
/// </summary>
public sealed partial class LikeEscapeTests
{
    [Fact]
    public void Every_like_query_names_its_escape_character()
    {
        var source = Path.Combine(RepositoryRoot(), "src", "server");
        var calls = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => LikeCalls(File.ReadAllText(path)).Select(call => (Path: Path.GetRelativePath(source, path), call.Line, call.Arguments)))
            .ToList();

        calls.Count.ShouldBeGreaterThan(50, "the searches across the modules");
        calls.Where(c => c.Arguments != 3).Select(c => $"{c.Path}:{c.Line}")
            .ShouldBeEmpty("Pass QueryExtensions.LikeEscape as the third argument of EF.Functions.ILike/Like");
    }

    [Fact]
    public void The_check_counts_arguments_as_the_compiler_does()
    {
        LikeCalls("EF.Functions.ILike(p.FirstName + \" \" + (p.MiddleName ?? \"\"), pattern)").Single().Arguments.ShouldBe(2);
        LikeCalls("EF.Functions.ILike(x, Pick(a, b), QueryExtensions.LikeEscape)").Single().Arguments.ShouldBe(3);
        LikeCalls("EF.Functions.Like(x, \"a,b)\", \"\\\\\")").Single().Arguments.ShouldBe(3);
    }

    /// <summary>The calls in <paramref name="code"/>: line number and how many arguments each passes.</summary>
    private static IEnumerable<(int Line, int Arguments)> LikeCalls(string code)
    {
        foreach (Match match in LikeCall().Matches(code))
        {
            var line = code.AsSpan(0, match.Index).Count('\n') + 1;
            yield return (line, CountArguments(code, match.Index + match.Length));
        }
    }

    // Counts the arguments of the call whose '(' ends just before start, skipping brackets and strings inside it.
    private static int CountArguments(string code, int start)
    {
        var depth = 0;
        var count = 1;
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
                    return count;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                count++;
            }
        }

        throw new InvalidOperationException("A LIKE call is not closed.");
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

    [GeneratedRegex(@"EF\.Functions\.I?Like\(")]
    private static partial Regex LikeCall();
}
