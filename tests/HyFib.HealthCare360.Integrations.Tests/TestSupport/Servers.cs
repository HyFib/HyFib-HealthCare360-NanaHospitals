using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Integrations.Tests.TestSupport;

/// <summary>The server the code under test runs on, which decides whether the simulators may be used.</summary>
internal static class Servers
{
    /// <summary>A training or test server, set up with demonstration data: the simulators stand in for the real ones.</summary>
    public static IOptions<PlatformOptions> Training => Options.Create(new PlatformOptions { SeedDemoData = true });

    /// <summary>A hospital's live server: no simulator is used.</summary>
    public static IOptions<PlatformOptions> Live => Options.Create(new PlatformOptions());
}
