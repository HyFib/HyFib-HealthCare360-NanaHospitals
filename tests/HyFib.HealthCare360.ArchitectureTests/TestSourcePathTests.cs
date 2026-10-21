using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// Tests find the repository's files from their output folder (walking up to HyFib.HealthCare360.slnx), never from the
/// compile-time path of their own source file. CI builds are deterministic (Directory.Build.props sets
/// ContinuousIntegrationBuild when CI=true), so the compiler maps every source path to <c>/_/</c>: a test that reads
/// fixtures next to its CallerFilePath passes on a developer's machine and fails in CI, where that folder does not exist.
/// </summary>
public sealed partial class TestSourcePathTests
{
    [Fact]
    public void No_test_finds_its_files_through_the_callers_file_path()
    {
        var tests = Path.Combine(RepositoryRoot(), "tests");
        var offenders = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => CallerFilePath().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(tests, path))
            .ToList();

        offenders.ShouldBeEmpty("Find the files from AppContext.BaseDirectory (see AbdmDocumentTests.FixtureDirectory) instead");
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

        throw new InvalidOperationException("The repository root was not found above the test's folder.");
    }

    // The attribute applied to a parameter, short or qualified, alone or first in its list (not the word in a comment).
    [GeneratedRegex(@"\[\s*(?:global::)?(?:System\.Runtime\.CompilerServices\.)?CallerFilePath(?:Attribute)?\s*[\],(]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CallerFilePath();
}
