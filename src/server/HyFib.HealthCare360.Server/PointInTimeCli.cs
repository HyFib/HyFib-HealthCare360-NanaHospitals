using System.Globalization;
using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// The Server CLI's commands for a restore to a point in time (SRS REL-02): they decrypt what HC360 encrypted with the
/// backup recovery key, for PostgreSQL's own tools to restore (docs/deployment/DEPLOYMENT.md, "A restore to a point in time").
/// </summary>
internal static class PointInTimeCli
{
    /// <summary>
    /// <c>backup decrypt-base</c>: writes the files pg_basebackup made into a new folder. The key is read only once the folder
    /// is known to be usable, so nobody types it in for nothing.
    /// </summary>
    public static async Task<int> DecryptBaseAsync(string input, string output, Func<byte[]> readKey)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine($"{output} is not empty: give a new folder for the base backup's files.");
            return 2;
        }

        var manifest = await BaseBackupArchive.DecryptToFolderAsync(input, readKey(), output, CancellationToken.None);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Base backup of {manifest.CreatedAt:yyyy-MM-dd HH:mm} UTC, of PostgreSQL {manifest.PostgresVersion}: cluster {manifest.SystemIdentifier}, timeline {manifest.Timeline}."));
        Console.WriteLine($"It decrypts in full and matches its manifest. Its WAL starts at {manifest.StartWalFile}: replay the WAL of cluster {manifest.SystemIdentifier} archived since.");
        foreach (var part in manifest.Parts)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {Path.Combine(output, part.Name)} ({part.Length:N0} bytes)"));
        }

        return 0;
    }
}
