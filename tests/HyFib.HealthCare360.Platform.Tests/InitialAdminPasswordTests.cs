using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class InitialAdminPasswordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-initial-password-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_one_time_password_file_is_deleted_once_and_only_from_the_data_directory()
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        Directory.CreateDirectory(directories.Root);
        var path = InitialAdminPassword.PathIn(directories);
        Path.GetDirectoryName(path).ShouldBe(directories.Root);
        File.WriteAllText(path, "Password: not-a-real-one");

        InitialAdminPassword.Delete(directories).ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
        InitialAdminPassword.Delete(directories).ShouldBeFalse("there is nothing left to delete");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
