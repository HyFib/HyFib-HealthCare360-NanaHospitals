using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SCAL-01: a server of several is known in System health by an id kept in its own data folder for its machine
/// (<see cref="ServerNodeBoard.IdFileName"/>). It keeps the id when it starts again, and a data folder copied to another
/// machine makes an id of its own there. SystemHealthServersTests shows the servers' rows in the database.
/// </summary>
public sealed class ServerNodeIdTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_server_that_starts_again_keeps_its_id()
    {
        var node = Board().Node;

        Board().Node.ShouldBe(node);
        File.ReadAllLines(IdFile).ShouldBe([node, Environment.MachineName]);
    }

    /// <summary>
    /// A machine cloned after HC360 ran on it takes its data folder, and the id kept there, to the copy. The id named no
    /// machine, so the two servers had one: they wrote one row, and each took the other's part for its own and left it out
    /// of System health. On an API node cloned from the node that runs the jobs, System health said that no server runs
    /// them, and the worklist's test that the server that does is not reporting.
    /// </summary>
    [Fact]
    public void A_data_folder_copied_from_another_machine_makes_an_id_of_its_own()
    {
        var copied = Keep(Guid.NewGuid().ToString("N"), "HC360-ORIGINAL");

        var node = Board().Node;

        node.ShouldNotBe(copied);
        File.ReadAllLines(IdFile).ShouldBe([node, Environment.MachineName]);
        Board().Node.ShouldBe(node, "this machine should keep the id it made from then on");
    }

    /// <summary>An id kept without the name of its machine, as it was before, may have been copied from another: it is made again.</summary>
    [Fact]
    public void An_id_kept_without_its_machine_is_made_again()
    {
        var kept = Keep(Guid.NewGuid().ToString("N"));

        Board().Node.ShouldNotBe(kept);
    }

    private string IdFile => Path.Combine(_root, "config", ServerNodeBoard.IdFileName);

    private ServerNodeBoard Board() =>
        new(Substitute.For<IServiceScopeFactory>(), new DataDirectories(new PlatformOptions { DataDirectory = _root }));

    /// <summary>Writes <paramref name="lines"/> to this server's id file, as a server did before; returns the id, the first.</summary>
    private string Keep(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IdFile)!);
        File.WriteAllLines(IdFile, lines);
        return lines[0];
    }
}
