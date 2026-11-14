namespace HyFib.HealthCare360.Platform.Hosting;

/// <summary>
/// Keeps a production server out of the Development environment. appsettings.Development.json turns backups and MFA off, seeds
/// demonstration data and opens the API reference, so a server that ever starts as Development (a stray
/// ASPNETCORE_ENVIRONMENT or DOTNET_ENVIRONMENT on the Server Node) would quietly run without them. The Windows service is the
/// production install, so there the server refuses to start and says why, rather than carry on with the settings ignored:
/// IT sees the fault at once, and no one signs in to a server that looks fine and is not. A developer's console run is not
/// the service and is left alone. The project also keeps the file out of every publish.
/// </summary>
public static class DevelopmentGuard
{
    /// <summary>Why the server must not start, or null when it may.</summary>
    public static string? Problem(string? environmentName, bool runsAsWindowsService) =>
        runsAsWindowsService && string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            ? "the HC360 service is set to the Development environment (ASPNETCORE_ENVIRONMENT or DOTNET_ENVIRONMENT), which turns backups and MFA off. "
                + "Remove that setting from the service and the machine's environment variables, then start the service again."
            : null;
}
