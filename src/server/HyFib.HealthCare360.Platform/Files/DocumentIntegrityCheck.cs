using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Files;

/// <summary>A stored document as the database records it: its file, and the SHA-256 of its content when it was saved.</summary>
public sealed record DocumentRecord(Guid Id, string StoragePath, string Sha256);

/// <summary>What <see cref="DocumentIntegrityCheck"/> found.</summary>
/// <param name="Checked">Documents the database records.</param>
/// <param name="Missing">Whose file is not in the documents folder.</param>
/// <param name="Unreadable">Whose file does not decrypt with the keys on this machine.</param>
/// <param name="Mismatched">Whose content is not what was saved (its SHA-256 differs from the one recorded then).</param>
/// <param name="WithoutHash">That decrypt but have no SHA-256 recorded to compare with.</param>
/// <param name="Problems">The first problems, one line each, naming the document by id and file.</param>
public sealed record DocumentCheckResult(int Checked, int Missing, int Unreadable, int Mismatched, int WithoutHash, IReadOnlyList<string> Problems)
{
    public bool Ok => Missing == 0 && Unreadable == 0 && Mismatched == 0;
}

/// <summary>
/// After a restore (the restore drill, DEPLOYMENT.md section 8), checks every stored document the restored database records
/// end to end: its file is in place, it decrypts with the encryption keys installed here, and its content's SHA-256 is the
/// one the file store recorded when it was saved (<see cref="StoredFile.Sha256"/>). This does not rely on the backup's own
/// index, which the restore already checked, but on the database, the keys and the files agreeing, which is what the
/// hospital needs of a restore. Documents deleted by users are checked too: their content is kept for retention.
/// </summary>
public static class DocumentIntegrityCheck
{
    private const string Purpose = "files";
    private const int MaxProblems = 50;

    public static Task<DocumentCheckResult> RunAsync(HospitalDbContext db, DataDirectories directories, IFieldProtector protector, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(directories);
        var rows = db.Set<StoredFile>().IgnoreQueryFilters().AsNoTracking().OrderBy(f => f.Id)
            .Select(f => new DocumentRecord(f.Id, f.StoragePath, f.Sha256)).AsAsyncEnumerable();
        return CheckAsync(rows, directories.Files, protector, ct);
    }

    public static async Task<DocumentCheckResult> CheckAsync(IAsyncEnumerable<DocumentRecord> documents, string documentsFolder, IFieldProtector protector, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(protector);
        int count = 0, missing = 0, unreadable = 0, mismatched = 0, withoutHash = 0;
        var problems = new List<string>();
        void Problem(DocumentRecord document, string what)
        {
            if (problems.Count < MaxProblems)
            {
                problems.Add($"{document.Id} ({document.StoragePath}): {what}");
            }
        }

        await foreach (var document in documents.WithCancellation(ct))
        {
            count++;
            var path = Path.Combine(documentsFolder, document.StoragePath);
            if (!File.Exists(path))
            {
                missing++;
                Problem(document, "the file is missing");
                continue;
            }

            byte[] content;
            try
            {
                content = protector.UnprotectBytes(await File.ReadAllBytesAsync(path, ct), Purpose);
            }
            catch (CryptographicException ex)
            {
                unreadable++;
                Problem(document, $"it does not decrypt with the keys on this machine ({ex.Message})");
                continue;
            }

            if (string.IsNullOrWhiteSpace(document.Sha256))
            {
                withoutHash++;
                continue;
            }

            if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), document.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                mismatched++;
                Problem(document, "its content is not what was saved");
            }
        }

        return new DocumentCheckResult(count, missing, unreadable, mismatched, withoutHash, problems);
    }

    /// <summary>The summary line the restore drill reads (Invoke-HC360RestoreDrill.ps1, ConvertFrom-DocumentCheck).</summary>
    public static string Summary(DocumentCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"Documents checked: {result.Checked}; missing: {result.Missing}; unreadable: {result.Unreadable}; not as saved: {result.Mismatched}; without a recorded hash: {result.WithoutHash}.";
    }
}
