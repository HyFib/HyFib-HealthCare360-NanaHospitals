using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;

namespace HyFib.HealthCare360.Server;

/// <summary>The server's <c>audit verify-export</c>: an auditor's check of an audit export, without the database.</summary>
internal static class AuditCli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int VerifyExport(string exportFile, string? anchorFile)
    {
        AuditExport? export;
        try
        {
            using var input = File.OpenRead(exportFile);
            export = JsonSerializer.Deserialize<AuditExport>(input, Json);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"{exportFile} is not an audit export: {ex.Message}");
            return 2;
        }

        if (export is null)
        {
            Console.Error.WriteLine($"{exportFile} is empty.");
            return 2;
        }

        var own = new List<AuditAnchor>();
        if (anchorFile is not null)
        {
            foreach (var line in File.ReadLines(anchorFile).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                if (JsonSerializer.Deserialize<AuditAnchor>(line, Json) is { } anchor)
                {
                    own.Add(anchor);
                }
            }
        }

        var check = AuditExportVerifier.Verify(export, own);
        Console.WriteLine($"Exported {export.ExportedAt:u} by {export.ExportedBy ?? "an unknown user"}.");
        Console.WriteLine(check.Message);
        return check.Valid ? 0 : 3;
    }
}
