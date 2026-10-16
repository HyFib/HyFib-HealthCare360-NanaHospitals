using System.Net;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS SCAL-01 and SCAL-04: the settings for several servers behind a reverse proxy, and for the monthly partitions, are
/// checked as the server starts; the client's address is taken from a proxy's forwarded headers only when the site names
/// that proxy; and a node of several keeps its keys and documents in the shared folder, everything else of its own.
/// </summary>
public sealed class ScaleOutSettingsTests
{
    [Fact]
    public void A_single_server_needs_no_cluster_settings() =>
        PlatformOptionsValidator.ClusterProblems(new ClusterOptions()).ShouldBeEmpty();

    [Fact]
    public void Keys_shared_by_several_servers_must_be_protected_by_a_certificate()
    {
        PlatformOptionsValidator.ClusterProblems(new ClusterOptions { SharedDirectory = @"\\files\hc360" }).ShouldHaveSingleItem()
            .ShouldContain("KeyCertificateThumbprint");
        PlatformOptionsValidator.ClusterProblems(new ClusterOptions { SharedDirectory = @"\\files\hc360", KeyCertificateThumbprint = "AB12" }).ShouldBeEmpty();
        PlatformOptionsValidator.ClusterProblems(new ClusterOptions { SharedDirectory = @"\\files\hc360", KeyCertificatePath = "keys.pfx" }).ShouldBeEmpty();
    }

    [Fact]
    public void A_certificate_is_named_once_and_only_with_a_shared_folder()
    {
        PlatformOptionsValidator.ClusterProblems(new ClusterOptions { SharedDirectory = "s", KeyCertificateThumbprint = "AB12", KeyCertificatePath = "keys.pfx" })
            .ShouldHaveSingleItem().ShouldContain("only one of");
        PlatformOptionsValidator.ClusterProblems(new ClusterOptions { KeyCertificateThumbprint = "AB12" })
            .ShouldHaveSingleItem().ShouldContain("SharedDirectory is not");
    }

    [Fact]
    public void The_shipped_partition_settings_keep_everything_three_months_ahead()
    {
        var partitions = new PartitionOptions();

        (partitions.MonthsAhead, partitions.RetentionMonths.Count).ShouldBe((3, 0));
        PlatformOptionsValidator.PartitionProblems(partitions).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void Months_ahead_outside_one_to_twenty_four_are_refused(int months) =>
        PlatformOptionsValidator.PartitionProblems(new PartitionOptions { MonthsAhead = months }).ShouldHaveSingleItem().ShouldContain("MonthsAhead");

    [Fact]
    public void The_audit_trail_can_never_be_given_a_retention()
    {
        var partitions = new PartitionOptions { RetentionMonths = { ["AUDIT.audit_events"] = 240 } };

        PlatformOptionsValidator.PartitionProblems(partitions).ShouldHaveSingleItem().ShouldContain("ten years");
    }

    [Fact]
    public void A_retention_is_at_least_a_month()
    {
        PlatformOptionsValidator.PartitionProblems(new PartitionOptions { RetentionMonths = { [PartitionedTables.NotificationLogs.Name] = 0 } })
            .ShouldHaveSingleItem().ShouldContain("at least 1 month");
        PlatformOptionsValidator.PartitionProblems(new PartitionOptions { RetentionMonths = { [PartitionedTables.IntegrationLogs.Name] = 24 } })
            .ShouldBeEmpty();
    }

    [Fact]
    public void Proxy_addresses_and_networks_must_be_addresses_and_networks()
    {
        var network = new NetworkOptions { KnownProxies = ["10.0.0.5", "proxy.local"], KnownNetworks = ["10.0.1.0/24", "10.0.2.0"] };

        var problems = ReverseProxy.Problems(network);

        problems.Count.ShouldBe(2);
        problems.ShouldContain(p => p.Contains("'proxy.local'", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("'10.0.2.0'", StringComparison.Ordinal));
        PlatformOptionsValidator.Problems(new PlatformOptions { Network = network }).Count.ShouldBe(2);
    }

    [Fact]
    public void Without_named_proxies_forwarded_headers_are_not_read() =>
        ReverseProxy.Options(new NetworkOptions()).ShouldBeNull();

    [Fact]
    public async Task A_named_proxy_s_forwarded_client_address_and_scheme_are_taken()
    {
        var context = await ForwardAsync(new NetworkOptions { KnownProxies = ["10.0.0.5"] }, from: "10.0.0.5", forwardedFor: "203.0.113.9", proto: "https");

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("203.0.113.9"));
        context.Request.Scheme.ShouldBe("https");
    }

    [Fact]
    public async Task A_proxy_on_a_named_network_is_trusted_too()
    {
        var context = await ForwardAsync(new NetworkOptions { KnownNetworks = ["10.0.0.0/29"] }, from: "10.0.0.6", forwardedFor: "203.0.113.9", proto: "https");

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("203.0.113.9"));
    }

    [Fact]
    public async Task Anyone_else_cannot_claim_another_address_by_sending_the_headers()
    {
        // A workstation on the LAN, or the loopback address, is not a proxy the site named.
        foreach (var from in new[] { "10.0.5.20", "127.0.0.1" })
        {
            var context = await ForwardAsync(new NetworkOptions { KnownProxies = ["10.0.0.5"] }, from, forwardedFor: "203.0.113.9", proto: "https");

            context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(from));
            context.Request.Scheme.ShouldBe("http");
        }
    }

    [Fact]
    public async Task Through_two_named_proxies_the_client_is_the_first_address_that_is_not_one()
    {
        // The client claims 198.51.100.1 itself; the outer proxy appends the address it saw, the inner one its own.
        var network = new NetworkOptions { KnownProxies = ["10.0.0.5", "10.0.0.9"] };

        var context = await ForwardAsync(network, from: "10.0.0.5", forwardedFor: "198.51.100.1, 203.0.113.9, 10.0.0.9", proto: "https");

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("203.0.113.9"));
    }

    [Fact]
    public void A_node_of_several_keeps_keys_and_documents_in_the_shared_folder_and_the_rest_of_its_own()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc360-node");
        var shared = Path.Combine(Path.GetTempPath(), "hc360-shared");
        var single = new DataDirectories(new PlatformOptions { DataDirectory = root });
        var node = new DataDirectories(new PlatformOptions { DataDirectory = root, Cluster = { SharedDirectory = shared } });

        (single.IsShared, single.Keys, single.Files).ShouldBe((false, Path.Combine(root, "keys"), Path.Combine(root, "files")));
        (node.IsShared, node.Shared, node.Keys, node.Files).ShouldBe((true, shared, Path.Combine(shared, "keys"), Path.Combine(shared, "files")));
        (node.Config, node.Certificates, node.Logs, node.Temp).ShouldBe(
            (Path.Combine(root, "config"), Path.Combine(root, "certs"), Path.Combine(root, "logs"), Path.Combine(root, "temp")));
    }

    private static async Task<HttpContext> ForwardAsync(NetworkOptions network, string from, string forwardedFor, string proto)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(from);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Request.Headers["X-Forwarded-Proto"] = proto;

        var options = ReverseProxy.Options(network).ShouldNotBeNull();
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Microsoft.Extensions.Options.Options.Create(options));
        await middleware.Invoke(context);
        return context;
    }
}
