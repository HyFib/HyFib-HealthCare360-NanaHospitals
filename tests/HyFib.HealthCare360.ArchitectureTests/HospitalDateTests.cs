using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// Audit P9: a validator that checks a date against today takes today from the hospital's clock (<c>time.Today()</c> from
/// Patients, <c>HrClock</c>, <c>QualityClock</c>), never from the UTC date, which is still yesterday in India until 05:30.
/// </summary>
public sealed partial class HospitalDateTests
{
    [Fact]
    public void No_validator_takes_today_from_the_UTC_date()
    {
        var source = Path.Combine(RepositoryRoot(), "src", "server");
        var found = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path: path, Code: File.ReadAllText(path)))
            .Where(file => file.Code.Contains("AbstractValidator<", StringComparison.Ordinal))
            .SelectMany(file => UtcDates(file.Code).Select(line => $"{Path.GetRelativePath(source, file.Path)}:{line}"))
            .ToList();

        found.ShouldBeEmpty("Take today from the hospital's clock: time.Today() (Patients), HrClock.LocalToday or QualityClock.LocalToday");
    }

    [Fact]
    public void The_check_finds_the_UTC_date_however_it_is_written()
    {
        UtcDates("var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);").ShouldBe([1]);
        UtcDates("d <= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddDays(1))").ShouldBe([1]);
        UtcDates("\nDateOnly.FromDateTime(time.GetUtcNow().DateTime)").ShouldBe([2]);
        UtcDates("var today = time.Today();").ShouldBeEmpty();
    }

    /// <summary>The lines of <paramref name="code"/> that take a date from the UTC clock.</summary>
    private static List<int> UtcDates(string code) =>
        [.. UtcDate().Matches(code).Select(match => code.AsSpan(0, match.Index).Count('\n') + 1)];

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

    [GeneratedRegex(@"DateOnly\.FromDateTime\(\s*\w+\.GetUtcNow\(\)\.(?:Utc)?DateTime\b")]
    private static partial Regex UtcDate();
}
