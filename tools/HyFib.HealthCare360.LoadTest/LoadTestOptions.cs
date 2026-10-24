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

        Virtual users sign in with the staff accounts given (shared between them), search, open and register patients,
        and list visits and appointments, pausing between steps as staff do. Then the figures are compared with SRS 7.3:
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
                case "server" or "accounts" or "ca" or "report" or "json":
                    break;
                default:
                    return (null, $"--{name} {value} is not understood.");
            }
        }

        return (options, null);
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
