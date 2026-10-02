using System.Security.Cryptography;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;

/// <summary>
/// An import to run. <see cref="Content"/> must be seekable (an uploaded file or a file on disk). A dry run reads the
/// file and works out what would change, and changes nothing.
/// </summary>
public sealed record CatalogueImportRequest(
    Stream Content,
    string FileName,
    bool DryRun,
    string? ImportedByName = null,
    CatalogueImportChannel Channel = CatalogueImportChannel.Upload);

/// <summary>What an import did, or would do (<see cref="DryRun"/>).</summary>
public sealed record CatalogueImportResult(
    Guid? Id,
    CatalogueKind Catalogue,
    string Release,
    string FileName,
    string Sha256,
    long FileSizeBytes,
    string? SourceTitle,
    string? SourceVersion,
    int CodesInFile,
    int Added,
    int Updated,
    int Unchanged,
    int Reinstated,
    int Withdrawn,
    int LeftAlone,
    int Chapters,
    int Blocks,
    bool DryRun,
    DateTimeOffset At,
    string ImportedBy,
    CatalogueImportChannel Channel,
    int DurationMs,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Imports a catalogue file into the local catalogues (MRD-01, INT-ICD, MB-03): the hospital's own copy of the WHO
/// ICD-10 ClaML file into <c>clinical.icd_codes</c> (release "ICD-10 2019"), or the CMS ICD-10-PCS order file into
/// <c>clinical.procedure_codes</c>.
/// <list type="bullet">
/// <item>A code in the file is added, or brought in line with the file: title, chapter, block, whether it is codable,
/// dagger or asterisk, inclusion terms. Search words (keywords) are never changed, so the curated shorthand stays.
/// ICD-10-PCS codes from HC360's starter list keep their curated everyday titles.</item>
/// <item>A code from the starter list or an earlier import that the file no longer has is <b>withdrawn</b>: made
/// inactive with the date, never deleted, so recorded diagnoses keep their codes. A later file that has it again
/// brings it back.</item>
/// <item>Codes MRD added by hand or saved from the WHO online search are left alone.</item>
/// <item>An ICD-10 import also replaces the chapters and blocks the browser and the morbidity index use.</item>
/// <item>Everything is saved in one transaction with an import record (file name, SHA-256, counts, who, when) and an
/// audit entry. A PostgreSQL advisory lock lets only one import run at a time.</item>
/// </list>
/// </summary>
public sealed class CatalogueImporter(
    HospitalDbContext db,
    IAuditLogger audit,
    ICurrentUser user,
    TimeProvider time,
    IcdClassificationProvider classification,
    ILogger<CatalogueImporter> logger)
{
    /// <summary>One import at a time across every server node (the Clinical module's advisory-lock key).</summary>
    internal const long ImportLockKey = 4_247_301;

    private const int MaxFileNameLength = 300;

    public CatalogueFileLimits Limits { get; init; } = new();

    public async Task<Result<CatalogueImportResult>> ImportAsync(CatalogueImportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = time.GetTimestamp();
        var content = request.Content;
        if (!content.CanSeek)
        {
            throw new ArgumentException("The catalogue file must be given as a seekable stream.", nameof(request));
        }

        if (content.Length > Limits.MaxUploadBytes)
        {
            return Error.Field("file", $"The file is larger than {Limits.MaxUploadBytes / (1024 * 1024)} MB. {CatalogueFile.AcceptedFiles}");
        }

        content.Position = 0;
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(content, ct));
        content.Position = 0;
        var fileName = SafeFileName(request.FileName);

        CatalogueFileContent file;
        try
        {
            file = CatalogueFile.Read(content, fileName, Limits);
        }
        catch (CatalogueFileException ex)
        {
            logger.LogInformation("Catalogue file {FileName} was refused: {Reason}", fileName, ex.Message);
            return Error.Field("file", ex.Message);
        }

        var shownName = file.InnerFileName is { } inner && !string.Equals(inner, fileName, StringComparison.OrdinalIgnoreCase)
            ? CatalogueText.Fit($"{fileName} ({inner})", MaxFileNameLength)
            : fileName;
        var importedBy = request.ImportedByName ?? user.DisplayName ?? user.UserName ?? "Unknown user";
        var now = time.GetUtcNow();

        await using var transaction = request.DryRun ? null : await db.Database.BeginTransactionAsync(ct);
        if (transaction is not null && !await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({ImportLockKey}) AS \"Value\"").SingleAsync(ct))
        {
            return Error.Conflict("catalogue.import_running", "Another catalogue import is running. Try again when it has finished.");
        }

        Outcome outcome;
        string? title;
        string? version;
        string release;
        IReadOnlyList<string> warnings;
        if (file.Icd10 is { } icd10)
        {
            outcome = await ApplyIcd10Async(icd10, request.DryRun, now, ct);
            (title, version, release, warnings) = (Describe(icd10.Title), icd10.Title.Version, CodeReleases.Icd10, icd10.Warnings);
        }
        else
        {
            var pcs = file.Pcs ?? throw new InvalidOperationException("A catalogue file was read without its codes.");
            outcome = await ApplyPcsAsync(pcs, request.DryRun, now, ct);
            (title, version, release, warnings) = ("ICD-10-PCS order file", pcs.Version, CodeReleases.Icd10Pcs, pcs.Warnings);
        }

        CatalogueImport? record = null;
        if (transaction is not null)
        {
            record = new CatalogueImport
            {
                Catalogue = file.Kind,
                Release = release,
                FileName = shownName,
                FileSha256 = sha256,
                FileSizeBytes = content.Length,
                SourceTitle = title,
                SourceVersion = version is null ? null : CatalogueText.Fit(version, 32),
                CodesInFile = outcome.CodesInFile,
                Added = outcome.Added,
                Updated = outcome.Updated,
                Unchanged = outcome.Unchanged,
                Reinstated = outcome.Reinstated,
                Withdrawn = outcome.Withdrawn,
                LeftAlone = outcome.LeftAlone,
                Chapters = outcome.Chapters,
                Blocks = outcome.Blocks,
                ImportedAt = now,
                ImportedByUserId = user.UserId,
                ImportedByName = CatalogueText.Fit(importedBy, 200),
                Channel = request.Channel,
                DurationMs = Elapsed(started),
            };
            db.Add(record);
            audit.Add(new AuditEntry(AuditCategories.Admin, "mrd.catalogue.imported",
                $"{release} catalogue imported from {shownName}: {outcome.Added} added, {outcome.Updated} updated, {outcome.Reinstated} brought back, {outcome.Withdrawn} withdrawn")
            {
                EntityType = nameof(CatalogueImport),
                EntityId = record.Id.ToString(),
                Severity = AuditSeverity.Warning,
                UserName = request.ImportedByName,
                Details = new
                {
                    file = shownName,
                    sha256,
                    sizeBytes = content.Length,
                    title,
                    version,
                    channel = request.Channel.ToString(),
                    outcome.CodesInFile,
                    outcome.Added,
                    outcome.Updated,
                    outcome.Unchanged,
                    outcome.Reinstated,
                    outcome.Withdrawn,
                    outcome.LeftAlone,
                },
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            classification.Invalidate();
            logger.LogInformation(
                "{Release} catalogue imported from {FileName} ({Sha256}) by {ImportedBy}: {Added} added, {Updated} updated, {Reinstated} brought back, {Withdrawn} withdrawn",
                release, shownName, sha256, importedBy, outcome.Added, outcome.Updated, outcome.Reinstated, outcome.Withdrawn);
        }

        return new CatalogueImportResult(
            record?.Id, file.Kind, release, shownName, sha256, content.Length, title, version, outcome.CodesInFile, outcome.Added, outcome.Updated,
            outcome.Unchanged, outcome.Reinstated, outcome.Withdrawn, outcome.LeftAlone, outcome.Chapters, outcome.Blocks, request.DryRun, now,
            importedBy, request.Channel, Elapsed(started), warnings);
    }

    /// <summary>Counts of what an import changed.</summary>
    internal sealed class Outcome
    {
        public int CodesInFile { get; set; }
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int Reinstated { get; set; }
        public int Withdrawn { get; set; }
        public int LeftAlone { get; set; }
        public int Chapters { get; set; }
        public int Blocks { get; set; }
    }

    private async Task<Outcome> ApplyIcd10Async(ClamlClassification file, bool dryRun, DateTimeOffset now, CancellationToken ct)
    {
        var rows = db.Set<IcdCode>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).Where(c => c.Release == CodeReleases.Icd10);
        var existing = await (dryRun ? rows.AsNoTracking() : rows).ToListAsync(ct);
        var outcome = Icd10Changes.Apply(existing, file.Codes, now, added => db.Add(added), dryRun);
        outcome.Chapters = file.Chapters.Count;
        outcome.Blocks = file.Blocks.Count;
        if (!dryRun)
        {
            await ReplaceClassificationAsync(file, ct);
        }

        return outcome;
    }

    private async Task<Outcome> ApplyPcsAsync(PcsOrderFile file, bool dryRun, DateTimeOffset now, CancellationToken ct)
    {
        var rows = db.Set<ProcedureCode>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).Where(c => c.System == CodeReleases.Icd10Pcs);
        var existing = await (dryRun ? rows.AsNoTracking() : rows).ToListAsync(ct);
        return PcsChanges.Apply(existing, file.Codes, now, added => db.Add(added), dryRun);
    }

    /// <summary>Brings the stored chapters and blocks in line with the file's, updating rows in place by code.</summary>
    private async Task ReplaceClassificationAsync(ClamlClassification file, CancellationToken ct)
    {
        var stored = await db.Set<IcdClassificationEntry>().Where(e => e.Release == CodeReleases.Icd10).ToListAsync(ct);
        var byKey = stored.ToDictionary(e => (e.Kind, e.Code));
        var kept = new HashSet<(IcdClassificationKind, string)>();

        void Upsert(IcdClassificationKind kind, string code, string start, string end, string title, string chapter, string? parent, bool holdsCategories, int sequence)
        {
            if (!byKey.TryGetValue((kind, code), out var entry))
            {
                entry = new IcdClassificationEntry { Release = CodeReleases.Icd10, Kind = kind, Code = code };
                db.Add(entry);
            }

            entry.Start = start;
            entry.End = end;
            entry.Title = title;
            entry.ChapterNumber = chapter;
            entry.ParentCode = parent;
            entry.HoldsCategories = holdsCategories;
            entry.Sequence = sequence;
            kept.Add((kind, code));
        }

        foreach (var chapter in file.Chapters)
        {
            Upsert(IcdClassificationKind.Chapter, chapter.Number, chapter.Start, chapter.End, chapter.Title, chapter.Number, null, false, chapter.Sequence);
        }

        foreach (var block in file.Blocks)
        {
            Upsert(IcdClassificationKind.Block, block.Code, block.Start, block.End, block.Title, block.Chapter, block.Parent, block.HoldsCategories, block.Sequence);
        }

        db.RemoveRange(stored.Where(e => !kept.Contains((e.Kind, e.Code))));
    }

    /// <summary>"ICD-10" from the ClaML title's name and version, else its text.</summary>
    private static string? Describe(ClamlTitle title)
    {
        var name = string.Join(' ', new[] { title.Name, title.Version }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return CatalogueText.Fit(name.Length > 0 ? name : title.Text ?? "ClaML file", 200);
    }

    /// <summary>The file's own name, without folders or control characters, for the record and messages.</summary>
    internal static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return CatalogueText.Fit(cleaned.Length == 0 ? "catalogue file" : cleaned, MaxFileNameLength);
    }

    private int Elapsed(long started) => (int)Math.Min(int.MaxValue, time.GetElapsedTime(started).TotalMilliseconds);
}

/// <summary>The ICD-10 import's rules, apart from the database so they can be tested on their own.</summary>
internal static class Icd10Changes
{
    public static CatalogueImporter.Outcome Apply(
        IReadOnlyList<IcdCode> existing, IReadOnlyList<ClassificationCode> codes, DateTimeOffset now, Action<IcdCode> add, bool dryRun)
    {
        var outcome = new CatalogueImporter.Outcome { CodesInFile = codes.Count };
        var byCode = existing.GroupBy(c => c.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var inFile = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            inFile.Add(code.Code);
            if (!byCode.TryGetValue(code.Code, out var row))
            {
                outcome.Added++;
                if (!dryRun)
                {
                    var added = new IcdCode { Code = code.Code, Release = CodeReleases.Icd10, Source = CodeSource.Import, IsActive = true };
                    Copy(code, added);
                    add(added);
                }

                continue;
            }

            if (row.Source is CodeSource.Manual or CodeSource.Online)
            {
                outcome.LeftAlone++;
                continue;
            }

            var changed = !Matches(row, code);
            var reinstated = Reinstate(row);
            if (changed)
            {
                Copy(code, row);
            }

            row.Source = CodeSource.Import;
            if (reinstated)
            {
                outcome.Reinstated++;
            }
            else if (changed)
            {
                outcome.Updated++;
            }
            else
            {
                outcome.Unchanged++;
            }
        }

        foreach (var row in existing.Where(r => !inFile.Contains(r.Code)))
        {
            if (row.Source is CodeSource.Manual or CodeSource.Online)
            {
                outcome.LeftAlone++;
            }
            else if (row.WithdrawnAt is null && !row.IsDeleted)
            {
                outcome.Withdrawn++;
                row.IsActive = false;
                row.WithdrawnAt = now;
            }
        }

        return outcome;
    }

    private static bool Matches(IcdCode row, ClassificationCode code) =>
        row.Title == code.Title && row.Chapter == code.Chapter && row.Block == code.Block && row.IsBillable == code.IsBillable && row.Usage == code.Usage
        && row.IncludedTerms == code.IncludedTerms;

    /// <summary>The file's facts; the keywords (curated search words) are kept.</summary>
    private static void Copy(ClassificationCode code, IcdCode row)
    {
        row.Title = code.Title;
        row.Chapter = code.Chapter;
        row.Block = code.Block;
        row.IsBillable = code.IsBillable;
        row.Usage = code.Usage;
        row.IncludedTerms = code.IncludedTerms;
        row.RefreshSearchText();
    }

    /// <summary>Back in the file after an earlier import withdrew it (or after it was deleted): in use again.</summary>
    private static bool Reinstate(IcdCode row)
    {
        if (row.WithdrawnAt is null && !row.IsDeleted)
        {
            return false;
        }

        row.IsActive = true;
        row.WithdrawnAt = null;
        row.IsDeleted = false;
        row.DeletedAt = null;
        row.DeletedBy = null;
        return true;
    }
}

/// <summary>The ICD-10-PCS import's rules (see <see cref="Icd10Changes"/>).</summary>
internal static class PcsChanges
{
    public static CatalogueImporter.Outcome Apply(
        IReadOnlyList<ProcedureCode> existing, IReadOnlyList<PcsCode> codes, DateTimeOffset now, Action<ProcedureCode> add, bool dryRun)
    {
        var outcome = new CatalogueImporter.Outcome { CodesInFile = codes.Count };
        var byCode = existing.GroupBy(c => c.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var inFile = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            inFile.Add(code.Code);
            if (!byCode.TryGetValue(code.Code, out var row))
            {
                outcome.Added++;
                if (!dryRun)
                {
                    var added = new ProcedureCode
                    {
                        Code = code.Code,
                        Title = code.Title,
                        Category = code.Category,
                        System = CodeReleases.Icd10Pcs,
                        Source = CodeSource.Import,
                        IsActive = true,
                    };
                    added.RefreshSearchText();
                    add(added);
                }

                continue;
            }

            if (row.Source is CodeSource.Manual or CodeSource.Online)
            {
                outcome.LeftAlone++;
                continue;
            }

            var reinstated = Reinstate(row);

            // The starter list's codes keep their everyday titles ("Laparoscopic appendicectomy (…)") and categories.
            var changed = row.Source == CodeSource.Import && (row.Title != code.Title || row.Category != code.Category);
            if (changed)
            {
                row.Title = code.Title;
                row.Category = code.Category;
                row.RefreshSearchText();
            }

            if (reinstated)
            {
                outcome.Reinstated++;
            }
            else if (changed)
            {
                outcome.Updated++;
            }
            else
            {
                outcome.Unchanged++;
            }
        }

        foreach (var row in existing.Where(r => !inFile.Contains(r.Code)))
        {
            if (row.Source is CodeSource.Manual or CodeSource.Online)
            {
                outcome.LeftAlone++;
            }
            else if (row.WithdrawnAt is null && !row.IsDeleted)
            {
                outcome.Withdrawn++;
                row.IsActive = false;
                row.WithdrawnAt = now;
            }
        }

        return outcome;
    }

    private static bool Reinstate(ProcedureCode row)
    {
        if (row.WithdrawnAt is null && !row.IsDeleted)
        {
            return false;
        }

        row.IsActive = true;
        row.WithdrawnAt = null;
        row.IsDeleted = false;
        row.DeletedAt = null;
        row.DeletedBy = null;
        return true;
    }
}
