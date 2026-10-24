using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>Where the audit chain ended at a moment: its last sealed position, that event's hash and its seal.</summary>
public sealed record AuditAnchor(long ChainIndex, string Hash, string? Mac, DateTimeOffset AnchoredAt);

/// <summary>
/// Anchors of the audit chain kept outside the database (<see cref="DataDirectories.AuditAnchors"/>): every so often the
/// sealer appends the chain's end to a file it only ever appends to, and writes it to the server log too. The chain proves
/// that no event between its ends was changed; an anchor proves where the end was. A chain cut short, or made again from an
/// earlier point by someone who can write the database, no longer matches the anchors (<see cref="AuditChainService.VerifyAsync"/>).
/// The file is plain text, one JSON object a line, so an auditor can keep a copy and compare it with an export.
/// </summary>
public sealed class AuditAnchors(DataDirectories directories, ILogger<AuditAnchors> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _write = new();

    public string Path => directories.AuditAnchors;

    /// <summary>The anchors in the file, oldest first; none when there is no file yet. A line that does not read is skipped and counted.</summary>
    public (IReadOnlyList<AuditAnchor> Anchors, int Unreadable) Read()
    {
        if (!File.Exists(Path))
        {
            return ([], 0);
        }

        var anchors = new List<AuditAnchor>();
        var unreadable = 0;
        foreach (var line in File.ReadLines(Path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<AuditAnchor>(line, Json) is { ChainIndex: > 0, Hash.Length: 64 } anchor)
                {
                    anchors.Add(anchor);
                    continue;
                }
            }
            catch (JsonException)
            {
            }

            unreadable++;
        }

        return (anchors, unreadable);
    }

    /// <summary>The last anchor written, or null.</summary>
    public AuditAnchor? Last() => Read().Anchors.LastOrDefault();

    /// <summary>Appends an anchor, and writes it to the server log (which CERT-In asks to keep for 180 days).</summary>
    public void Append(AuditAnchor anchor)
    {
        lock (_write)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(file);
            writer.WriteLine(JsonSerializer.Serialize(anchor, Json));
            writer.Flush();
            file.Flush(flushToDisk: true);
        }

        logger.LogInformation("Audit chain anchored at position {ChainIndex}: hash {Hash}, seal {Mac}", anchor.ChainIndex, anchor.Hash, anchor.Mac);
    }
}
