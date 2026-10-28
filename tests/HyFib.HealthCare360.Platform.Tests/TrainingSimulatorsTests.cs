using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// An integration cannot be set to its simulator on a hospital's live server, only on a training or test server
/// (<see cref="TrainingSimulators"/>); the integrations' own reading of a simulator set before is tested with them.
/// </summary>
public sealed class TrainingSimulatorsTests
{
    private static readonly PlatformOptions Live = new();
    private static readonly PlatformOptions Training = new() { SeedDemoData = true };

    [Theory]
    [InlineData("""{"enabled":true,"provider":"simulator"}""")]
    [InlineData("""{"Provider":" SIMULATOR "}""")]
    public void On_a_live_server_an_integration_cannot_name_its_simulator(string json)
    {
        TrainingSimulators.Problem("integrations.payment", JsonNode.Parse(json), Live).ShouldNotBeNull().ShouldContain("training");
        TrainingSimulators.Problem("integrations.payment", JsonNode.Parse(json), Training).ShouldBeNull();
    }

    [Fact]
    public void Real_providers_other_settings_and_cleared_values_are_saved_on_any_server()
    {
        TrainingSimulators.Problem("integrations.payment", JsonNode.Parse("""{"enabled":true,"provider":"razorpay"}"""), Live).ShouldBeNull();
        TrainingSimulators.Problem("integrations.sms", JsonNode.Parse("""{"enabled":false,"provider":null}"""), Live).ShouldBeNull();
        TrainingSimulators.Problem("integrations.sms", null, Live).ShouldBeNull();
        TrainingSimulators.Problem("ui.theme", JsonNode.Parse("""{"provider":"simulator"}"""), Live).ShouldBeNull();
    }
}
