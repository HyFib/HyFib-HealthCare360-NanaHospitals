using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// <c>catalogue import --file &lt;file&gt; [--dry-run]</c>: imports the WHO ICD-10 ClaML file or the CMS ICD-10-PCS order
/// file on the Server Node, exactly as Medical records › ICD catalogue › Import does, for a hospital that prefers to
/// copy the file to the server. It reads the server's own settings for the database, and the import record names the
/// Windows account that ran it.
/// </summary>
internal static class CatalogueCli
{
    public static async Task<int> ImportAsync(string path, bool dryRun)
    {
        if (!File.Exists(path))
        {
            await Console.Error.WriteLineAsync($"File not found: {path}");
            return 2;
        }

        await using var app = BuildApplication();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        try
        {
            if ((await db.Database.GetPendingMigrationsAsync()).Any())
            {
                await Console.Error.WriteLineAsync(
                    "The database is not up to date with this version of HC360. Start the HC360 service once (it updates the database), then run the import again.");
                return 3;
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"The HC360 database could not be reached: {ex.Message}");
            return 3;
        }

        var importer = scope.ServiceProvider.GetRequiredService<CatalogueImporter>();
        await using var file = File.OpenRead(path);
        var account = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var result = await importer.ImportAsync(
            new CatalogueImportRequest(file, Path.GetFileName(path), dryRun, $"Server console ({account})", CatalogueImportChannel.Console), CancellationToken.None);
        if (result.IsFailure)
        {
            await Console.Error.WriteLineAsync(result.Error!.Message);
            return 1;
        }

        foreach (var line in Describe(result.Value))
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    /// <summary>What the import did (or would do), in the words the screen uses.</summary>
    internal static IEnumerable<string> Describe(CatalogueImportResult result)
    {
        static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

        yield return result.DryRun
            ? $"Checked {result.FileName}: nothing was changed. Run the command again without --dry-run to import it."
            : $"Imported {result.FileName} into the {result.Release} catalogue.";
        var title = result.SourceTitle ?? "(no title)";
        var version = result.SourceVersion is { } v && !title.Contains(v, StringComparison.Ordinal) ? $", version {v}" : string.Empty;
        yield return $"  The file says it is: {title}{version}";
        yield return $"  SHA-256: {result.Sha256}";
        yield return $"  Codes in the file: {N(result.CodesInFile)}" +
                     (result.Catalogue == CatalogueKind.Icd10 ? $" (chapters {N(result.Chapters)}, blocks {N(result.Blocks)})" : string.Empty);
        var verb = result.DryRun ? "would be" : "were";
        yield return $"  {N(result.Added)} {verb} added, {N(result.Updated)} updated, {N(result.Reinstated)} brought back and {N(result.Withdrawn)} withdrawn; " +
                     $"{N(result.Unchanged)} were already up to date.";
        if (result.LeftAlone > 0)
        {
            yield return $"  {N(result.LeftAlone)} codes added or changed by hand, or saved from the WHO online search, " +
                         "were left as they are.";
        }

        foreach (var warning in result.Warnings)
        {
            yield return $"  Note: {warning}";
        }
    }

    /// <summary>
    /// The server's services without its web host, configured from the same files as the Windows service (appsettings,
    /// hc360.settings.json beside the data, HC360_ environment variables). Nothing is started: no listener, no jobs.
    /// </summary>
    internal static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        var options = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? new PlatformOptions();
        builder.Configuration.AddProtectedJsonFile(ProtectedSettings.PathIn(new DataDirectories(options)));
        builder.Configuration.AddEnvironmentVariables("HC360_");
        options = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? options;

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // The model notes EF Core writes while it builds the model are for developers, not for the person importing.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);

        // The certificates are only created when the server starts, which this command never does.
        var certificates = new LanCertificateManager(new DataDirectories(options), options, new EphemeralDataProtectionProvider(), NullLogger<LanCertificateManager>.Instance);
        builder.AddServerApplication(certificates);
        return builder.Build();
    }
}
