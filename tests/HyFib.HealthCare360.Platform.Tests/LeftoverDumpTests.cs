using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class LeftoverDumpTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "hc360-tests", "dumps-" + Guid.NewGuid().ToString("N"));

    public LeftoverDumpTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void An_unencrypted_dump_left_by_an_interrupted_backup_is_found_but_one_being_written_is_not()
    {
        var now = DateTimeOffset.UtcNow;
        var leftover = Create("0199b0c4.dump", now - TimeSpan.FromDays(1));
        Create("0199b0c5.dump", now - TimeSpan.FromMinutes(5));
        Create("notes.txt", now - TimeSpan.FromDays(3));

        BackupService.LeftoverDumps(_temp, now).Select(f => f.Name).ShouldBe([leftover]);
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private string Create(string name, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(_temp, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return name;
    }
}
