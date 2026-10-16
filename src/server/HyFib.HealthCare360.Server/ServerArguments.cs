using HyFib.HealthCare360.Platform.Hosting;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// The server's own command-line switches, turned into the configuration keys they stand for before the host reads its
/// arguments. <c>--role all|api|worker</c> sets <c>HealthCare360:Role</c> (SCAL-05): the host's command-line provider
/// would otherwise file it under a key of its own, <c>role</c>, which nothing reads.
/// </summary>
internal static class ServerArguments
{
    private const string RoleSwitch = "--role";
    private const string RoleKey = "--" + PlatformOptions.SectionName + ":" + nameof(PlatformOptions.Role);

    /// <exception cref="ArgumentException">The role is missing or is not one of <see cref="PlatformOptionsValidator.Roles"/>.</exception>
    public static string[] Map(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var mapped = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? role = null;
            if (string.Equals(arg, RoleSwitch, StringComparison.OrdinalIgnoreCase))
            {
                role = i + 1 < args.Length ? args[++i] : string.Empty;
            }
            else if (arg.StartsWith(RoleSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                role = arg[(RoleSwitch.Length + 1)..];
            }

            if (role is null)
            {
                mapped.Add(arg);
                continue;
            }

            var known = PlatformOptionsValidator.Roles.FirstOrDefault(r => string.Equals(r, role.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"--role must be one of {string.Join(", ", PlatformOptionsValidator.Roles)}; it was '{role}'.", nameof(args));
            mapped.Add(RoleKey);
            mapped.Add(known);
        }

        return [.. mapped];
    }
}
