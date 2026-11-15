using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// After a server update, HC360 Desktop and lab agents below the minimum version are named in System health and told to
/// update in their heartbeat's answer (<see cref="OutdatedClients"/>); none is ever refused.
/// </summary>
public sealed class OutdatedClientsTests
{
    private static readonly ClientVersionOptions Defaults = new();

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("1.0.0-rc.1", 1, 0, 0)]
    [InlineData("1.4.0+abc123", 1, 4, 0)]
    [InlineData("v2.0.1", 2, 0, 1)]
    public void A_reported_version_is_read_as_its_numbers(string reported, int major, int minor, int build) =>
        OutdatedClients.Parse(reported).ShouldBe(new Version(major, minor, build));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void Text_that_is_not_a_version_is_none(string? reported) => OutdatedClients.Parse(reported).ShouldBeNull();

    [Theory]
    [InlineData("1.1.4", DeviceKind.Workstation, "1.1.0")]
    [InlineData("1.0.0", DeviceKind.LabAgent, "1.0.0")]
    [InlineData("2.3.0-rc.2", DeviceKind.Tablet, "2.3.0")]
    public void Unset_the_minimum_is_the_servers_release(string server, DeviceKind kind, string expected) =>
        OutdatedClients.MinimumFor(kind, Defaults, server).ShouldBe(Version.Parse(expected));

    [Fact]
    public void A_set_minimum_wins_and_the_two_kinds_have_their_own()
    {
        var options = new ClientVersionOptions { MinimumDesktopVersion = "1.0.5", MinimumLabAgentVersion = "0.9" };

        OutdatedClients.MinimumFor(DeviceKind.Kiosk, options, "1.3.0").ShouldBe(new Version(1, 0, 5));
        OutdatedClients.MinimumFor(DeviceKind.LabAgent, options, "1.3.0").ShouldBe(new Version(0, 9, 0));
    }

    [Theory]
    [InlineData("1.0.9", false)]
    [InlineData("1.1.0", false)]
    [InlineData("1.1.0-rc.1", false)]
    [InlineData("1.0.0", true)]
    [InlineData("0.9.9", true)]
    [InlineData(null, true)]
    public void A_version_below_the_minimum_or_not_reported_is_below(string? reported, bool below) =>
        OutdatedClients.IsBelow(reported, new Version(1, 0, 9)).ShouldBe(below);

    [Fact]
    public void A_desktop_below_the_minimum_is_told_to_update_in_the_header()
    {
        var http = new DefaultHttpContext();

        OutdatedClients.ApplyHeader(http.Response, DeviceKind.Workstation, "1.0.2", Defaults, "1.1.0");

        http.Response.Headers[OutdatedClients.UpdateRequiredHeader].ToString().ShouldBe("1.1.0");
    }

    [Fact]
    public void A_lab_agent_below_the_minimum_is_told_to_update_in_the_header()
    {
        var http = new DefaultHttpContext();

        OutdatedClients.ApplyHeader(http.Response, DeviceKind.LabAgent, null, Defaults, "1.1.0");

        http.Response.Headers[OutdatedClients.UpdateRequiredHeader].ToString().ShouldBe("1.1.0");
    }

    [Fact]
    public void An_up_to_date_device_gets_no_header()
    {
        var http = new DefaultHttpContext();

        OutdatedClients.ApplyHeader(http.Response, DeviceKind.Workstation, "1.1.2", Defaults, "1.1.5");

        http.Response.Headers.ContainsKey(OutdatedClients.UpdateRequiredHeader).ShouldBeFalse();
    }

    [Fact]
    public void Nothing_to_say_when_every_device_is_up_to_date() =>
        OutdatedClients.Warning(new OutdatedClientList([], [], "1.1.0", "1.1.0")).ShouldBeNull();

    [Fact]
    public void The_warning_names_the_desktops_and_lab_agents_below_the_minimum_and_stays_a_warning()
    {
        var warning = OutdatedClients.Warning(new OutdatedClientList(["OPD 1 (1.0.2)"], ["Lab PC (no version)"], "1.1.0", "1.1.0")).ShouldNotBeNull();

        warning.Code.ShouldBe("clients.outdated");
        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldContain("2 device(s)");
        warning.Message.ShouldContain("HC360 Desktop below 1.1.0: OPD 1 (1.0.2).");
        warning.Message.ShouldContain("Lab agent below 1.1.0: Lab PC (no version).");
        warning.Message.ShouldContain("keep working");
    }

    [Fact]
    public void A_long_list_is_cut_after_ten_names()
    {
        var names = Enumerable.Range(1, 13).Select(n => $"PC {n} (1.0.0)").ToList();

        var warning = OutdatedClients.Warning(new OutdatedClientList(names, [], "1.1.0", "1.1.0")).ShouldNotBeNull();

        warning.Message.ShouldContain("PC 10 (1.0.0) and 3 more.");
        warning.Message.ShouldNotContain("PC 11");
    }
}
