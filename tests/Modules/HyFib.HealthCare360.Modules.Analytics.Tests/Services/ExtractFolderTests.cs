using System.Text;
using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Configuration;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Services;

public sealed class ExtractFolderTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "hc360-tests", "extracts-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => Path.Combine(_base, "bi");

    private ExtractFolder Folder(string? configured) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ExtractFolder.ConfigurationKey] = configured }).Build(),
        new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_base, "data") }));

    [Theory]
    [InlineData("daily-revenue", true)]
    [InlineData("op2026", true)]
    [InlineData("Daily-Revenue", false)]
    [InlineData("-revenue", false)]
    [InlineData("revenue.csv", false)]
    [InlineData("../revenue", false)]
    [InlineData("op census", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("con", false)]
    [InlineData("nul", false)]
    [InlineData("com1", false)]
    [InlineData("lpt9", false)]
    [InlineData("console", true)]
    [InlineData("com10", true)]
    public void Extract_names_are_plain_file_names_that_are_not_windows_devices(string? name, bool valid) => ExtractFolder.IsValidName(name).ShouldBe(valid);

    [Fact]
    public void The_folder_is_set_in_the_server_settings_or_is_in_the_data_folder()
    {
        Folder(Root).Root.ShouldBe(Path.GetFullPath(Root));
        Folder(null).Root.ShouldBe(Path.Combine(Path.GetFullPath(Path.Combine(_base, "data")), ExtractFolder.DefaultFolderName));
        Folder(Root).FilePath("NH", "daily-revenue").ShouldBe(Path.Combine(Path.GetFullPath(Root), "NH", "daily-revenue.csv"));
        Folder("power-bi").Root.ShouldBe(Path.Combine(Path.GetFullPath(Path.Combine(_base, "data")), "power-bi"),
            "a relative path is taken from the data folder, not the service's working directory");
    }

    [Fact]
    public async Task A_file_is_replaced_whole_and_never_written_outside_the_folder()
    {
        var folder = Folder(Root);
        var path = folder.FilePath("NH", "daily-revenue");

        await folder.WriteAsync(path, Encoding.UTF8.GetBytes("first"), Ct);
        await folder.WriteAsync(path, Encoding.UTF8.GetBytes("second"), Ct);

        (await File.ReadAllTextAsync(path, Ct)).ShouldBe("second");
        Directory.GetFiles(Path.Combine(folder.Root, ".writing")).ShouldBeEmpty("nothing is left half-written");
        folder.Check().ShouldBeNull();

        await Should.ThrowAsync<ArgumentException>(() => folder.WriteAsync(Path.Combine(folder.Root, "..", "outside.csv"), [], Ct));
        Should.Throw<ArgumentException>(() => folder.FilePath("NH", "../outside"));
        Should.Throw<ArgumentException>(() => folder.FilePath("..", "daily-revenue"));

        folder.Remove("NH", "daily-revenue");
        File.Exists(path).ShouldBeFalse();
        folder.Remove("NH", "daily-revenue");
    }

    [Fact]
    public void A_folder_that_cannot_be_written_is_reported()
    {
        Directory.CreateDirectory(_base);
        var blocked = Path.Combine(_base, "not-a-folder");
        File.WriteAllText(blocked, "a file where the folder should be");

        Folder(blocked).Check().ShouldNotBeNull().ShouldContain("cannot write");
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }
}
