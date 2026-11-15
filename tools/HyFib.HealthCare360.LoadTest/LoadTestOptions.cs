using System.Globalization;

namespace HyFib.HealthCare360.LoadTest;

/// <summary>Whether a missed SRS target fails the run, or is only reported (a machine whose figures mean nothing, as CI's).</summary>
internal enum TargetMode
{
    Enforce,
    Report,

    /// <summary>
    /// The list budgets (PERF-05) fail the run and the timings are only reported: a page's size is the same on any
    /// machine, its time is not (CI's shared runners).
    /// </summary>
    Payloads,
}

/// <summary>One run of the load test, from the command line (see <see cref="Usage"/>).</summary>
internal sealed record LoadTestOptions
{
    public const string PasswordVariable = "HC360_LOADTEST_PASSWORD";

    public required Uri Server { get; init; }

    /// <summary>The hospital CA (PEM) the server's certificate must chain to; null to use the machine's trust.</summary>
    public string? CaCertificatePath { get; init; }

    /// <summary>Staff accounts the virtual users share, each signed in once (sign-in allows 10 a minute per address).</summary>
    public required IReadOnlyList<string> Accounts { get; init; }

    /// <summary>
    /// Accounts for the other roles of the peak mix (<see cref="PeakMix"/>): <c>--billing-accounts</c>, <c>--pharmacy-accounts</c>,
    /// <c>--lab-accounts</c> and <c>--ward-accounts</c>. A role with none is left out of the run.
    /// </summary>
    public IReadOnlyDictionary<Role, IReadOnlyList<string>> RoleAccounts { get; init; } = new Dictionary<Role, IReadOnlyList<string>>();

    /// <summary>The file of portal access tokens (<c>--portal-tokens</c>), one per line: the portal signs in by a code sent to the patient's phone, which a load test cannot read.</summary>
    public string? PortalTokensPath { get; init; }

    /// <summary>The tokens read from <see cref="PortalTokensPath"/>.</summary>
    public IReadOnlyList<string> PortalTokens { get; init; } = [];

    /// <summary>The patient portal's address (<c>--portal-server</c>): it has a port of its own, away from the staff app's.</summary>
    public Uri? PortalServer { get; init; }

    /// <summary>The pharmacy store the counter sells from (<c>--pharmacy-store</c>), when the pharmacy accounts may not list the stores.</summary>
    public Guid? PharmacyStore { get; init; }

    /// <summary>The ward whose medication round is run (<c>--ward</c>), when the ward accounts may not list the wards.</summary>
    public Guid? Ward { get; init; }

    /// <summary>Weights of steps, by name, over the defaults (<c>--weights</c>); 0 leaves a step out.</summary>
    public IReadOnlyDictionary<string, int> Weights { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public required string Password { get; init; }

    /// <summary>Virtual users: SRS PERF-01 states its targets at 75 concurrent users.</summary>
    public int Users { get; init; } = 75;

    /// <summary>How long the virtual users take to start, evenly; measuring starts when the last one has.</summary>
    public TimeSpan Ramp { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long to measure once every user has started.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The mean pause of a user between two steps, as a person reads the screen.</summary>
    public TimeSpan Think { get; init; } = TimeSpan.FromSeconds(3);

    public TargetMode Targets { get; init; } = TargetMode.Enforce;

    public string? ReportPath { get; init; }

    public string? JsonPath { get; init; }

    public const string Usage = """
        HyFib HealthCare360 load test (docs/testing/LOAD-TESTS.md)

          run --server <https://host:7360/> --accounts <user1,user2,...> --test-server
              [--ca <hospital-ca.crt>] [--users 75] [--ramp 1m] [--duration 5m] [--think 3s]
              [--targets enforce|report|payloads] [--report <file.md>] [--json <file.json>]
              [--billing-accounts <...>] [--pharmacy-accounts <...>] [--lab-accounts <...>] [--ward-accounts <...>]
              [--pharmacy-store <id>] [--ward <id>] [--portal-tokens <file> --portal-server <https://host:7361/>]
              [--weights "Billing receipt=30,Lab order=0"]

        Virtual users sign in with the staff accounts given (shared between them), search, open and register patients,
        and list visits and appointments, pausing between steps as staff do. With accounts for other roles, the hospital's
        peak is run too: OPD visits and tokens (--accounts), billing receipts, pharmacy counter sales, lab orders and
        result entry, ward medication rounds (MAR), and the patient portal's record views (tokens made by signing in to the
        portal just before the run). Users divide among the roles by the weights of their steps; --weights changes any
        step's weight, 0 leaving it out. A role with no accounts, and a step the server has nothing for, is left out and
        the report says so. Then the figures are compared with SRS 7.3:
        p95 of reads within 300 ms, of writes within 600 ms and of patient search within 500 ms (PERF-01, PERF-02), and
        lists paginated and compressed under 100 KB (PERF-05). It registers patients, so run it only against a test
        server, and say so with --test-server. The accounts' password is read from HC360_LOADTEST_PASSWORD.

        Exit code 0 when every target is met (with --targets report, whatever the targets; with --targets payloads, when
        the lists are within their budget, whatever the timings), 1 when one is missed or more than 1% of requests fail,
        2 for a mistake in the command.
        """;

    /// <summary>The options in <paramref name="args"/>, or why they are not usable.</summary>
    public static (LoadTestOptions? Options, string? Problem) Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        if (args.Count == 0 || args[0] != "run")
        {
            return (null, "Start with \"run\".");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return (null, $"Unexpected \"{args[i]}\".");
            }

            var name = args[i][2..];
            if (name is "test-server")
            {
                flags.Add(name);
            }
            else if (i + 1 < args.Count)
            {
                values[name] = args[++i];
            }
            else
            {
                return (null, $"--{name} needs a value.");
            }
        }

        if (!flags.Contains("test-server"))
        {
            return (null, "The load test registers patients: run it only against a test server, and pass --test-server to say so.");
        }

        if (!values.TryGetValue("server", out var server) || !Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return (null, "Give the server's address with --server, as https://host:7360/.");
        }

        if (!values.TryGetValue("accounts", out var accounts) || accounts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is not { Length: > 0 } names)
        {
            return (null, "Give one or more staff accounts with --accounts, separated by commas.");
        }

        if (environment(PasswordVariable) is not { Length: > 0 } password)
        {
            return (null, $"Give the accounts' password in the {PasswordVariable} environment variable, never on the command line.");
        }

        var options = new LoadTestOptions
        {
            Server = uri,
            Accounts = names,
            Password = password,
            CaCertificatePath = values.GetValueOrDefault("ca"),
            ReportPath = values.GetValueOrDefault("report"),
            JsonPath = values.GetValueOrDefault("json"),
        };

        var roleAccounts = new Dictionary<Role, IReadOnlyList<string>>();
        foreach (var (name, value) in values)
        {
            switch (name)
            {
                case "users" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var users) && users is >= 1 and <= 2000:
                    options = options with { Users = users };
                    break;
                case "ramp" when ParseDuration(value) is { } ramp:
                    options = options with { Ramp = ramp };
                    break;
                case "duration" when ParseDuration(value) is { } duration && duration > TimeSpan.Zero:
                    options = options with { Duration = duration };
                    break;
                case "think" when ParseDuration(value) is { } think:
                    options = options with { Think = think };
                    break;
                case "targets" when value is "enforce" or "report" or "payloads":
                    options = options with
                    {
                        Targets = value switch { "report" => TargetMode.Report, "payloads" => TargetMode.Payloads, _ => TargetMode.Enforce },
                    };
                    break;
                case "billing-accounts" or "pharmacy-accounts" or "lab-accounts" or "ward-accounts":
                    roleAccounts[RoleOf(name)] = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "portal-tokens":
                    options = options with { PortalTokensPath = value };
                    break;
                case "portal-server" when Uri.TryCreate(value, UriKind.Absolute, out var portal) && portal.Scheme == Uri.UriSchemeHttps:
                    options = options with { PortalServer = portal };
                    break;
                case "pharmacy-store" when Guid.TryParse(value, out var store):
                    options = options with { PharmacyStore = store };
                    break;
                case "ward" when Guid.TryParse(value, out var ward):
                    options = options with { Ward = ward };
                    break;
                case "weights":
                    if (ParseWeights(value) is not { } weights)
                    {
                        return (null, $"--weights {value} is not understood: give step names and weights, such as \"Billing receipt=30,Lab order=0\" (steps: {string.Join(", ", StepNames)}).");
                    }

                    options = options with { Weights = weights };
                    break;
                case "server" or "accounts" or "ca" or "report" or "json":
                    break;
                default:
                    return (null, $"--{name} {value} is not understood.");
            }
        }

        if (options.PortalTokensPath is not null && options.PortalServer is null)
        {
            return (null, "Give the patient portal's address with --portal-server, as https://host:7361/ (the portal has a port of its own).");
        }

        return (options with { RoleAccounts = roleAccounts }, null);
    }

    private static Role RoleOf(string option) => option switch
    {
        "billing-accounts" => Role.Billing,
        "pharmacy-accounts" => Role.Pharmacy,
        "lab-accounts" => Role.Lab,
        _ => Role.Ward,
    };

    /// <summary>The names of the steps whose weight may be set.</summary>
    internal static IEnumerable<string> StepNames => [.. Operations.All.Select(o => o.Name), .. PeakMix.Defaults.Keys];

    /// <summary>"Billing receipt=30,Lab order=0" as weights by step name; null when a name is not a step or a weight not a number.</summary>
    internal static IReadOnlyDictionary<string, int>? ParseWeights(string text)
    {
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var known = StepNames.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.LastIndexOf('=');
            if (at <= 0
                || !known.TryGetValue(part[..at].Trim(), out var name)
                || !int.TryParse(part[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var weight)
                || weight > 1000)
            {
                return null;
            }

            weights[name] = weight;
        }

        return weights.Count > 0 ? weights : null;
    }

    /// <summary>"90s", "5m", "1h" or "1500ms"; null when it is none of those.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        if (text.Length < 2)
        {
            return null;
        }

        var (number, unit) = text.EndsWith("ms", StringComparison.Ordinal) ? (text[..^2], "ms") : (text[..^1], text[^1..]);
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return unit switch
        {
            "ms" => TimeSpan.FromMilliseconds(value),
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            _ => null,
        };
    }
}
