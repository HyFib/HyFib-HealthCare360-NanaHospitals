using System.IO;
using HyFib.HealthCare360.Desktop.Services;

namespace HyFib.HealthCare360.Desktop.Tests;

/// <summary>
/// Audit P12: DevTools show the signed-in user's token and every response, and call the API with it, so they open only
/// where IT's machine policy allows them. The user's own settings file, which the user can edit, does not turn them on.
/// </summary>
public sealed class DevToolsPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-devtools-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("""{ "kiosk": false }""", false)]
    [InlineData("""{ "allowDevTools": true }""", true)]
    [InlineData("""{ "kiosk": true, "disableDevTools": true, "allowDevTools": true }""", false)]
    public void DevTools_open_only_where_the_machine_policy_allows_them(string? policy, bool open)
    {
        Directory.CreateDirectory(_root);
        var settings = Path.Combine(_root, "settings.json");
        File.WriteAllText(settings, """{ "serverUrl": "https://hc360-srv:7360/", "allowDevTools": true }""");
        var policyPath = Path.Combine(_root, "policy.json");
        if (policy is not null)
        {
            File.WriteAllText(policyPath, policy);
        }

        DesktopSettings.Load(settings, policyPath).AllowDevTools.ShouldBe(open);
    }
}
