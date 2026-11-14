namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// What the CI and release workflows promise about main and about tags (operations review): a run on main is never
/// cancelled by the next merge, and a release is refused unless CI succeeded for the tagged commit. The workflows run on
/// GitHub; these tests read the files, so a change that drops either promise fails here before it reaches a tag.
/// </summary>
public sealed class ReleaseWorkflowTests
{
    [Fact]
    public void CI_cancels_only_pull_request_runs_never_a_run_on_main()
    {
        var ci = Read(".github", "workflows", "ci.yml");

        ci.ShouldContain("cancel-in-progress: ${{ github.event_name == 'pull_request' }}");
        ci.ShouldNotContain("cancel-in-progress: true");
    }

    [Fact]
    public void The_release_checks_that_CI_succeeded_for_the_tagged_commit_before_it_builds_anything()
    {
        var release = Read(".github", "workflows", "release.yml");

        var gate = release.IndexOf("Refuse to release a commit CI has not passed", StringComparison.Ordinal);
        gate.ShouldBeGreaterThan(0);
        // Before the hour of build and tests, and before anything is signed or published.
        gate.ShouldBeLessThan(release.IndexOf("Publish HC360 (server, desktop, deployment scripts)", StringComparison.Ordinal));
        release.ShouldContain("actions/workflows/ci.yml/runs?head_sha=$env:TAGGED_SHA");
        release.ShouldContain("$latest.conclusion -ne 'success'");
        release.ShouldContain("actions: read");
        // The job's own token: no new secret.
        release.ShouldContain("GH_TOKEN: ${{ github.token }}");
    }

    internal static string Read(params string[] relative) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. relative]));

    internal static string RepositoryRoot()
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
}
