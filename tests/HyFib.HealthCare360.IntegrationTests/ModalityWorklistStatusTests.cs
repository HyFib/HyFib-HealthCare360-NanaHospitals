using System.Text.Json;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;
using HyFib.HealthCare360.Integrations.Pacs.Worklist;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Administration › Integrations › Modality worklist with several servers (SCAL-01): the worklist server runs only on the
/// node that runs the jobs (SCAL-05), which answers no requests, so the dashboard and the worklist's test are always
/// answered by an API node. They say what the node that runs the jobs shares of its worklist server through the database.
/// </summary>
public sealed class ModalityWorklistStatusTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The node that runs the jobs could not open the worklist's port. An API node used to read only its own memory, where
    /// the worklist server never runs: the dashboard showed no listener (and so a healthy worklist) and the test said the
    /// server had not started yet.
    /// </summary>
    [Fact]
    public async Task An_API_node_shows_and_tests_the_worklist_server_of_the_node_that_runs_the_jobs()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var port = Random.Shared.Next(20_000, 60_000);
        var failure = $"Port {port} could not be opened: AddressAlreadyInUse.";
        await using var jobNode = await SecondNode.StartAsync(server, Ct, services =>
        {
            services.AddSingleton<LabGatewayState>();
            services.AddSingleton<LabRelayRegistry>();
            services.AddSingleton<ModalityWorklistState>();
            services.AddSingleton<LabStatusBoard>();
        });
        await using var apiNode = await ApiNodeAsync(port);
        var worklist = jobNode.Services.GetRequiredService<ModalityWorklistState>();
        var board = jobNode.Services.GetRequiredService<LabStatusBoard>();
        try
        {
            worklist.Failed("HC360MWL", port, failure);
            await board.PublishAsync(Ct);

            // The dashboard, on the test server as an API node.
            var listener = (await ListenersAsync(admin)).ShouldHaveSingleItem("the worklist server of the node that runs the jobs");
            listener.GetProperty("state").GetString().ShouldBe("failed");
            listener.GetProperty("lastError").GetString().ShouldBe(failure);

            // The test, on an API node: what the node that runs the jobs could not do, not "has not started yet".
            var failed = await TestOnApiNodeAsync(apiNode);
            failed.Success.ShouldBeFalse();
            failed.Message.ShouldBe(failure);

            // Once it listens, both say so.
            worklist.Listening("HC360MWL", port);
            await board.PublishAsync(Ct);
            (await ListenersAsync(admin)).ShouldHaveSingleItem().GetProperty("state").GetString().ShouldBe("listening");
            var passed = await TestOnApiNodeAsync(apiNode);
            passed.Success.ShouldBeTrue(passed.Message);
            passed.Message.ShouldContain($"listening as HC360MWL on port {port}");
        }
        finally
        {
            await board.WithdrawAsync(Ct);
        }
    }

    /// <summary>
    /// With no server that runs the jobs reporting at all (off, or its service stopped), the worklist's test on an API node
    /// said the worklist server "has not started yet. It starts within a minute", which it never would. It says that the
    /// server that runs background jobs is not reporting; once that server reports, but its worklist server has not
    /// started yet, it says so as before.
    /// </summary>
    [Fact]
    public async Task An_API_node_says_when_no_server_that_runs_the_jobs_reports()
    {
        await using var apiNode = await ApiNodeAsync(Random.Shared.Next(20_000, 60_000));

        var none = await TestOnApiNodeAsync(apiNode);
        none.Success.ShouldBeFalse();
        none.Message.ShouldBe(
            "The server that runs background jobs is not reporting (Administration › System health), so nothing serves the worklist. Start HC360 on that server.");

        // It starts, and reports, before its worklist server listens.
        var jobs = new ServerNodeBoard(server.Services.GetRequiredService<IServiceScopeFactory>(), SystemHealthServersTests.NodeFolder());
        try
        {
            await jobs.PublishAsync(SystemHealthServersTests.JobNode("IT-JOBS-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()), Ct);
            var starting = await TestOnApiNodeAsync(apiNode);
            starting.Success.ShouldBeFalse();
            starting.Message.ShouldBe(
                "The worklist server has not started yet. It starts within a minute of being turned on, on the server that runs background jobs.");
        }
        finally
        {
            await jobs.WithdrawAsync(Ct);
        }
    }

    private static async Task<List<JsonElement>> ListenersAsync(ApiClient admin)
    {
        var integrations = await admin.GetAsync<JsonElement>("/api/v1/admin/integrations");
        var mwl = integrations.EnumerateArray().Single(i => i.GetProperty("name").GetString() == "mwl");
        return [.. mwl.GetProperty("listeners").EnumerateArray()];
    }

    /// <summary>
    /// An API node of the hospital (role <c>api</c>): the worklist set up and turned on, its own worklist server never
    /// started, the worklist's test as the platform registers it, and the servers' parts of System health it reads.
    /// </summary>
    private Task<SecondNode> ApiNodeAsync(int port)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<WorklistSettings>(WorklistSettings.Key, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new WorklistSettings { Enabled = true, Port = port });
        return SecondNode.StartAsync(server, Ct, services =>
        {
            services.AddOptions<PlatformOptions>().Configure(options => options.Role = "api");
            services.AddScoped(_ => settings);
            services.AddScoped(_ => Substitute.For<ICurrentUser>());
            services.AddScoped<IntegrationSettingsReader>();
            services.AddSingleton<LabGatewayState>();
            services.AddSingleton<LabRelayRegistry>();
            services.AddSingleton<LabStatusBoard>();
            services.AddSingleton(SystemHealthServersTests.NodeFolder());
            services.AddSingleton<ServerNodeBoard>();
            services.AddModalityWorklist();
        });
    }

    private static async Task<IntegrationTestResult> TestOnApiNodeAsync(SecondNode apiNode)
    {
        await using var scope = apiNode.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ModalityWorklistSelfTest>().RunAsync(Ct);
    }
}
