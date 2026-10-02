using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Imaging;

/// <summary>Which studies the linking job looks for in PACS on each run, so every study gets its turn.</summary>
public sealed class PacsLinkScheduleTests
{
    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(10);

    private static PacsLinkCandidate Candidate(ImagingStudyStatus status, int hoursAgo) => new(Guid.CreateVersion7(), status, Now.AddHours(-hoursAgo));

    [Fact]
    public void Finished_scans_come_first_then_scans_under_way_then_waiting_studies()
    {
        var ordered = Candidate(ImagingStudyStatus.Ordered, 30);
        var scheduled = Candidate(ImagingStudyStatus.Scheduled, 20);
        var arrived = Candidate(ImagingStudyStatus.Arrived, 10);
        var started = Candidate(ImagingStudyStatus.InProgress, 5);
        var acquired = Candidate(ImagingStudyStatus.Acquired, 1);

        var batch = PacsLinkSchedule.Pick([ordered, scheduled, arrived, started, acquired], _ => null, Now, 50, Recheck);

        batch.ShouldBe([acquired.Id, started.Id, arrived.Id, scheduled.Id, ordered.Id]);
    }

    [Fact]
    public void A_study_looked_for_recently_waits_and_the_longest_waiting_go_first()
    {
        var justChecked = Candidate(ImagingStudyStatus.Acquired, 1);
        var checkedEarlier = Candidate(ImagingStudyStatus.Acquired, 2);
        var neverChecked = Candidate(ImagingStudyStatus.Acquired, 3);
        var checks = new Dictionary<Guid, DateTimeOffset> { [justChecked.Id] = Now.AddMinutes(-2), [checkedEarlier.Id] = Now.AddMinutes(-30) };

        var batch = PacsLinkSchedule.Pick([justChecked, checkedEarlier, neverChecked], id => checks.TryGetValue(id, out var at) ? at : null, Now, 50, Recheck);

        batch.ShouldBe([neverChecked.Id, checkedEarlier.Id]);
    }

    [Fact]
    public void A_run_looks_for_at_most_one_batch()
    {
        var candidates = Enumerable.Range(0, 120).Select(i => Candidate(ImagingStudyStatus.Ordered, i)).ToList();

        PacsLinkSchedule.Pick(candidates, _ => null, Now, ImagingService.ReconcileBatchSize, Recheck).Count.ShouldBe(50);
    }

    [Fact]
    public void Remembers_checks_reports_a_mismatch_once_and_forgets_old_studies()
    {
        var attempts = new PacsLinkAttempts();
        var study = Guid.CreateVersion7();

        attempts.Checked([study], Now);
        attempts.LastChecked(study).ShouldBe(Now);
        attempts.FirstReport(study, Now).ShouldBeTrue();
        attempts.FirstReport(study, Now).ShouldBeFalse();

        attempts.Prune(Now.AddMinutes(1));

        attempts.LastChecked(study).ShouldBeNull();
        attempts.FirstReport(study, Now).ShouldBeTrue();
    }
}
