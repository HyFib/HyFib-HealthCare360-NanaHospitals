using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.LoadTest;

[assembly: SuppressMessage("Security", "CA5394:Do not use insecure randomness",
    Justification = "The load test picks its steps and test data by chance; nothing it protects depends on that chance.")]

if (args is ["help" or "--help" or "-h"] or [])
{
    Console.WriteLine(LoadTestOptions.Usage);
    return args.Length == 0 ? 2 : 0;
}

var (options, problem) = LoadTestOptions.Parse(args, Environment.GetEnvironmentVariable);
if (options is null)
{
    await Console.Error.WriteLineAsync(problem);
    await Console.Error.WriteLineAsync();
    await Console.Error.WriteLineAsync(LoadTestOptions.Usage);
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

Func<bool, HttpMessageHandler> connections;
try
{
    connections = Connections.Factory(options.CaCertificatePath, options.Users);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
{
    await Console.Error.WriteLineAsync($"The hospital CA certificate could not be read from {options.CaCertificatePath}: {ex.Message}");
    return 2;
}

try
{
    var runner = new LoadRunner(options, connections, Console.Out);
    var result = await runner.RunAsync(cancel.Token);
    var report = Results.Markdown(result);
    Console.WriteLine();
    Console.WriteLine(report);
    if (options.ReportPath is { } reportPath)
    {
        await File.WriteAllTextAsync(reportPath, report);
    }

    if (options.JsonPath is { } jsonPath)
    {
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, new JsonSerializerOptions(Operations.Json) { WriteIndented = true }));
    }

    return result.Passed(options.Targets) ? 0 : 1;
}
catch (LoadTestException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}
catch (HttpRequestException ex)
{
    await Console.Error.WriteLineAsync($"The server could not be reached: {ex.Message}");
    return 1;
}
