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

    /// <summary>
    /// <c>backup decrypt-wal</c>: decrypts a folder of encrypted WAL files (one cluster's, <c>wal\&lt;system identifier&gt;</c>)
    /// into a folder PostgreSQL's restore_command reads. Exit code 3 when some open with another recovery key, which it names:
    /// a run with that key, into the same folder, adds them.
    /// </summary>
    public static async Task<int> DecryptWalAsync(string input, string output, Func<byte[]> readKey)
    {
        if (!Directory.Exists(input))
        {
            Console.Error.WriteLine($"There is no folder {input}.");
            return 2;
        }

        var result = await WalArchive.DecryptFolderAsync(input, readKey(), output, CancellationToken.None);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Decrypted {result.Decrypted} WAL file(s) into {output}, each checked as it was written."));
        foreach (var (keyId, count) in result.OtherKeys)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{count} file(s) open with recovery key {keyId}: run this again with that key, into the same folder."));
        }

        return result.OtherKeys.Count == 0 ? 0 : 3;
    }
}
