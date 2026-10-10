using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Security;

public static class ProtectedValueBuilderExtensions
{
    public const string Annotation = "HC360:ProtectedValue";
    public const string BytesAnnotation = "HC360:ProtectedBytes";
    public const string BlindIndexAnnotation = "HC360:BlindIndexOf";

    /// <summary>
    /// Marks a column that holds <see cref="IFieldProtector.Protect"/> strings, so that after a key rotation
    /// <see cref="FieldKeyMover"/> moves its values to the current key (SRS SEC-07).
    /// </summary>
    public static PropertyBuilder<T> IsProtectedValue<T>(this PropertyBuilder<T> property) => property.HasAnnotation(Annotation, true);

    /// <summary>
    /// Marks a column that holds <see cref="IFieldProtector.ProtectBytes"/> output, encrypted for
    /// <paramref name="purpose"/>, so that after a key rotation <see cref="FieldKeyMover"/> moves its values to the current
    /// key (SRS SEC-07). Without a purpose (when it differs from row to row) the values are not moved: they must expire on
    /// their own. Either way an earlier key is retired only once none of them is encrypted with it.
    /// </summary>
    public static PropertyBuilder<T> IsProtectedBytes<T>(this PropertyBuilder<T> property, string? purpose = null) =>
        property.HasAnnotation(BytesAnnotation, purpose ?? string.Empty);

    /// <summary>
    /// Marks a column that holds <see cref="IFieldProtector.BlindIndex"/> values of the value encrypted in
    /// <paramref name="protectedProperty"/> (a column marked <see cref="IsProtectedValue{T}"/> of the same table), so that
    /// after a key rotation <see cref="FieldKeyMover"/> computes each index again with the current key, from the
    /// decrypted value normalised with <see cref="Masking.Normalize"/> (SRS SEC-07).
    /// </summary>
    public static PropertyBuilder<T> IsBlindIndexOf<T>(this PropertyBuilder<T> property, string protectedProperty) =>
        property.HasAnnotation(BlindIndexAnnotation, protectedProperty);
}

/// <summary>
/// Keeps stored documents from changing while a backup copies them: a backup holds it shared, and moving documents to a
/// new key holds it alone. Across nodes, as a PostgreSQL advisory lock.
/// </summary>
public interface IDocumentStoreLock
{
    /// <summary>Waits until no documents are being rewritten, and keeps them from being until disposed.</summary>
    Task<IAsyncDisposable> HoldSharedAsync(CancellationToken cancellationToken);
}

internal sealed class PostgresDocumentStoreLock(IDbContextFactory<HospitalDbContext> contexts) : IDocumentStoreLock
{
    /// <summary>The advisory lock key; <see cref="FieldKeyMover"/> takes it for a transaction.</summary>
    public const long Key = 4_247_361;

    public async Task<IAsyncDisposable> HoldSharedAsync(CancellationToken cancellationToken)
    {
        var db = await contexts.CreateDbContextAsync(cancellationToken);
        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock_shared({Key})", cancellationToken);
            return new Held(db);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private sealed class Held(HospitalDbContext db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock_shared({Key})");
            }
            finally
            {
                // Closing the session releases the lock in any case.
                await db.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// What is still encrypted with an earlier key: values in the database, and stored documents and scheduled report files.
/// </summary>
public sealed record FieldKeyBacklog(long Values, long Documents)
{
    public bool IsEmpty => Values == 0 && Documents == 0;
}

/// <summary>
/// What is still encrypted with one key: values in the database (protected columns and secret settings), stored
/// documents and scheduled report files, and stored answers to repeated requests, which are not moved but expire within a
/// day.
/// </summary>
public sealed record FieldKeyUse(long Values, long Documents, long Expiring)
{
    public static readonly FieldKeyUse None = new(0, 0, 0);

    public bool IsEmpty => Values == 0 && Documents == 0 && Expiring == 0;
}

/// <summary>What one pass moved, what could not be moved (and stays readable with its own key), and whether documents had to wait for a backup.</summary>
public sealed record FieldKeyMoveResult(int Values, int Documents, int Failed, bool DocumentsWaited);

/// <summary>
/// After a rotation (SRS SEC-07), moves what was encrypted with an earlier key to the current one: the columns marked
/// <see cref="ProtectedValueBuilderExtensions.IsProtectedValue{T}"/> in every module, secret settings, stored documents,
/// and the columns marked <see cref="ProtectedValueBuilderExtensions.IsProtectedBytes{T}"/> with a purpose (scheduled
/// report files). The blind indexes marked <see cref="ProtectedValueBuilderExtensions.IsBlindIndexOf{T}"/> are computed
/// again with the current key from the value they index, once the rotation is a minute old. Each value is decrypted and encrypted again, and written only
/// if it has not changed meanwhile, so it never races a user's edit. Rows are updated directly, without audit entries for
/// each (the rotation itself is audited).
/// Stored answers to repeated requests, kept a day, are not moved: they expire with their key still in the ring. Once
/// nothing is left on an earlier key (<see cref="UsageAsync"/>), it can be retired.
/// </summary>
internal sealed class FieldKeyMover(
    HospitalDbContext db,
    IFieldProtector protector,
    IFieldKeyRing ring,
    DataDirectories directories,
    TimeProvider time,
    ILogger<FieldKeyMover> logger)
{
    private const int ValueBatch = 200;
    private const int DocumentBatch = 20;
    private const string FilesPurpose = "files";

    // The start of a ProtectBytes output that only one encryption has: its version, key id, nonce and tag.
    private const int Head = 1 + 8 + 12 + 16;

    /// <summary>
    /// How long after a rotation blind indexes are left as they are. A search or duplicate check that took the ring's keys
    /// just before the rotation looks for a row by its earlier index; it has run long before this has passed.
    /// </summary>
    public static readonly TimeSpan IndexesSettleAfter = TimeSpan.FromMinutes(1);

    public async Task<FieldKeyBacklog> CountAsync(CancellationToken cancellationToken)
    {
        // Until the first rotation everything is on the one key, so there is nothing to look for.
        if (ring.Keys.Count == 1)
        {
            return new FieldKeyBacklog(0, 0);
        }

        var prefix = ring.CurrentPrefix + "%";
        var header = CurrentHeader();
        long values = 0;
        long files = 0;
        foreach (var column in ProtectedColumns())
        {
            switch (column.Kind)
            {
                case ProtectedKind.Value:
                    values += await db.Database.SqlQueryRaw<long>(column.CountSql, new NpgsqlParameter("prefix", prefix)).SingleAsync(cancellationToken);
                    break;
                case ProtectedKind.File:
                    files += await db.Database.SqlQueryRaw<long>(column.CountSql, new NpgsqlParameter("header", header), new NpgsqlParameter("length", header.Length))
                        .SingleAsync(cancellationToken);
                    break;
                case ProtectedKind.Index:
                    values += await db.Database.SqlQueryRaw<long>(column.CountSql, IndexParameters()).SingleAsync(cancellationToken);
                    break;
            }
        }

        values += (await SecretSettingsAsync(cancellationToken)).Count(s => !s.Protected.StartsWith(ring.CurrentPrefix, StringComparison.Ordinal));
        return new FieldKeyBacklog(values, files + await DocumentsToMove().LongCountAsync(cancellationToken));
    }

    /// <summary>
    /// What each key a rotation added still encrypts, by key id. A key that encrypts nothing is not listed; nor is the
    /// original key, which is never retired.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FieldKeyUse>> UsageAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        var documents = new Dictionary<string, long>(StringComparer.Ordinal);
        var expiring = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var column in ProtectedColumns())
        {
            var counts = column.Kind switch
            {
                ProtectedKind.Value or ProtectedKind.Index => values,
                ProtectedKind.File => documents,
                _ => expiring,
            };
            foreach (var row in await db.Database.SqlQueryRaw<KeyCount>(column.UsageSql).ToListAsync(cancellationToken))
            {
                Add(counts, row.Key, row.Count);
            }
        }

        foreach (var setting in await SecretSettingsAsync(cancellationToken))
        {
            if (KeyOf(setting.Protected) is { } key)
            {
                Add(values, key, 1);
            }
        }

        var original = ring.OriginalKeyId;
        var stored = await db.Set<StoredFile>().IgnoreQueryFilters()
            .Where(f => f.EncryptionKeyId != null && f.EncryptionKeyId != original)
            .GroupBy(f => f.EncryptionKeyId!)
            .Select(g => new { Key = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);
        foreach (var row in stored)
        {
            Add(documents, row.Key, row.Count);
        }

        return values.Keys.Union(documents.Keys).Union(expiring.Keys)
            .Where(key => key != original)
            .ToDictionary(key => key, key => new FieldKeyUse(values.GetValueOrDefault(key), documents.GetValueOrDefault(key), expiring.GetValueOrDefault(key)),
                StringComparer.Ordinal);

        static void Add(Dictionary<string, long> counts, string? key, long count)
        {
            if (!string.IsNullOrEmpty(key))
            {
                var id = key.ToUpperInvariant();
                counts[id] = counts.GetValueOrDefault(id) + count;
            }
        }
    }

    /// <summary>One pass over everything still on an earlier key. A value that cannot be moved is logged and skipped.</summary>
    public async Task<FieldKeyMoveResult> MoveAsync(CancellationToken cancellationToken)
    {
        var values = 0;
        var files = 0;
        var failed = 0;
        foreach (var column in ProtectedColumns())
        {
            var (moved, failures) = column.Kind switch
            {
                ProtectedKind.Value => await MoveColumnAsync(column, cancellationToken),
                ProtectedKind.File => await MoveFilesAsync(column, cancellationToken),
                ProtectedKind.Index when IndexesSettled() => await MoveIndexesAsync(column, cancellationToken),
                _ => (0, 0),
            };
            if (column.Kind == ProtectedKind.File)
            {
                files += moved;
            }
            else
            {
                values += moved;
            }

            failed += failures;
        }

        var (settings, settingFailures) = await MoveSecretSettingsAsync(cancellationToken);
        var (documents, documentFailures, waited) = await MoveDocumentsAsync(cancellationToken);
        return new FieldKeyMoveResult(values + settings, documents + files, failed + settingFailures + documentFailures, waited);
    }

    private async Task<(int Moved, int Failed)> MoveColumnAsync(ProtectedColumn column, CancellationToken cancellationToken)
    {
        var moved = 0;
        var failed = 0;
        var after = Guid.Empty;
        while (true)
        {
            var rows = await db.Database.SqlQueryRaw<ProtectedRow>(column.SelectSql,
                    new NpgsqlParameter("prefix", ring.CurrentPrefix + "%"), new NpgsqlParameter("after", after), new NpgsqlParameter("take", ValueBatch))
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                return (moved, failed);
            }

            foreach (var row in rows)
            {
                if (Reprotect(row.Value, column.Name) is { } value)
                {
                    moved += await db.Database.ExecuteSqlRawAsync(column.UpdateSql,
                        [new NpgsqlParameter("id", row.Id), new NpgsqlParameter("value", value), new NpgsqlParameter("old", row.Value)], cancellationToken);
                }
                else
                {
                    failed++;
                }
            }

            after = rows[^1].Id;
        }
    }

    /// <summary>
    /// Moves a column of encrypted files (scheduled report files): one file in memory at a time, since a report can be
    /// large, and written only if it is still the one read (the same start, which no other encryption has).
    /// </summary>
    private async Task<(int Moved, int Failed)> MoveFilesAsync(ProtectedColumn column, CancellationToken cancellationToken)
    {
        var moved = 0;
        var failed = 0;
        var header = CurrentHeader();
        var after = Guid.Empty;
        while (true)
        {
            var ids = await db.Database.SqlQueryRaw<Guid>(column.SelectSql,
                    new NpgsqlParameter("header", header), new NpgsqlParameter("length", header.Length), new NpgsqlParameter("after", after),
                    new NpgsqlParameter("take", DocumentBatch))
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                return (moved, failed);
            }

            foreach (var id in ids)
            {
                // Null when it was purged meanwhile: then there is nothing to move.
                var stored = await db.Database.SqlQueryRaw<byte[]>(column.ReadSql, new NpgsqlParameter("id", id)).SingleOrDefaultAsync(cancellationToken);
                if (stored is null)
                {
                    continue;
                }

                if (ReprotectBytes(stored, column) is { } value)
                {
                    var head = stored[..Math.Min(stored.Length, Head)];
                    moved += await db.Database.ExecuteSqlRawAsync(column.UpdateSql,
                        [new NpgsqlParameter("id", id), new NpgsqlParameter("value", value), new NpgsqlParameter("head", head), new NpgsqlParameter("length", head.Length)],
                        cancellationToken);
                }
                else
                {
                    failed++;
                }
            }

            after = ids[^1];
        }
    }

    /// <summary>
    /// Computes the blind indexes of a column again with the current key, each from the value it indexes, and writes one
    /// only if it has not changed meanwhile.
    /// </summary>
    private async Task<(int Moved, int Failed)> MoveIndexesAsync(ProtectedColumn column, CancellationToken cancellationToken)
    {
        var moved = 0;
        var failed = 0;
        var after = Guid.Empty;
        while (true)
        {
            var rows = await db.Database.SqlQueryRaw<IndexRow>(column.SelectSql,
                    [.. IndexParameters(), new NpgsqlParameter("after", after), new NpgsqlParameter("take", ValueBatch)])
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                return (moved, failed);
            }

            foreach (var row in rows)
            {
                if (Reindex(row, column) is { } index)
                {
                    moved += await db.Database.ExecuteSqlRawAsync(column.UpdateSql,
                        [new NpgsqlParameter("id", row.Id), new NpgsqlParameter("value", index), new NpgsqlParameter("old", row.Index)], cancellationToken);
                }
                else
                {
                    failed++;
                }
            }

            after = rows[^1].Id;
        }
    }

    private async Task<(int Moved, int Failed)> MoveSecretSettingsAsync(CancellationToken cancellationToken)
    {
        var moved = 0;
        var failed = 0;
        var table = db.Model.FindEntityType(typeof(SystemSetting))!;
        var update = ProtectedColumn.UpdateJsonStringSql(table, table.FindProperty(nameof(SystemSetting.Value))!);
        foreach (var setting in await SecretSettingsAsync(cancellationToken))
        {
            if (setting.Protected.StartsWith(ring.CurrentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (Reprotect(setting.Protected, "secret setting") is { } value)
            {
                moved += await db.Database.ExecuteSqlRawAsync(update,
                    [new NpgsqlParameter("id", setting.Id), new NpgsqlParameter("value", JsonSerializer.Serialize(value)), new NpgsqlParameter("old", setting.Json)], cancellationToken);
            }
            else
            {
                failed++;
            }
        }

        return (moved, failed);
    }

    private async Task<(int Moved, int Failed, bool Waited)> MoveDocumentsAsync(CancellationToken cancellationToken)
    {
        var moved = 0;
        var failed = 0;
        var after = Guid.Empty;
        while (true)
        {
            // Documents change only while no backup is copying them (it holds the same lock, shared).
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({PostgresDocumentStoreLock.Key}) AS \"Value\"").SingleAsync(cancellationToken);
            if (!locked)
            {
                return (moved, failed, true);
            }

            var files = await DocumentsToMove().Where(f => f.Id > after).OrderBy(f => f.Id).Take(DocumentBatch).ToListAsync(cancellationToken);
            if (files.Count == 0)
            {
                return (moved, failed, false);
            }

            foreach (var file in files)
            {
                if (await MoveDocumentAsync(file, cancellationToken))
                {
                    moved++;
                }
                else
                {
                    failed++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            after = files[^1].Id;
        }
    }

    private async Task<bool> MoveDocumentAsync(StoredFile file, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directories.Files, file.StoragePath);
        if (!File.Exists(path))
        {
            logger.LogWarning("Stored document {FileId} is missing from the file store; there is nothing to move to the current key", file.Id);
            file.EncryptionKeyId = ring.CurrentKeyId;
            return true;
        }

        var temp = path + ".rekey";
        try
        {
            var stored = await File.ReadAllBytesAsync(path, cancellationToken);
            var keyId = ring.KeyIdOf(stored);
            if (keyId != ring.CurrentKeyId)
            {
                // Written beside the document, then put in its place in one step: a reader sees one or the other. The row
                // names the key the new bytes name, which a rotation meanwhile cannot make wrong.
                var encrypted = protector.ProtectBytes(protector.UnprotectBytes(stored, FilesPurpose), FilesPurpose);
                await File.WriteAllBytesAsync(temp, encrypted, cancellationToken);
                File.Move(temp, path, overwrite: true);
                keyId = ring.KeyIdOf(encrypted);
            }

            file.EncryptionKeyId = keyId;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Stored document {FileId} could not be moved to the current key; it stays readable with its own key", file.Id);
            return false;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private IQueryable<StoredFile> DocumentsToMove()
    {
        var current = ring.CurrentKeyId;
        var original = ring.OriginalKeyId;

        // Deleted documents keep their content (medico-legal retention), so they move too.
        return db.Set<StoredFile>().IgnoreQueryFilters().Where(f => (f.EncryptionKeyId ?? original) != current);
    }

    private string? Reprotect(string value, string what)
    {
        try
        {
            return protector.Protect(protector.Unprotect(value)!);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "A value in {Column} could not be moved to the current key; it stays readable with its own key", what);
            return null;
        }
    }

    private byte[]? ReprotectBytes(byte[] stored, ProtectedColumn column)
    {
        try
        {
            return protector.ProtectBytes(protector.UnprotectBytes(stored, column.Purpose), column.Purpose);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "A value in {Column} could not be moved to the current key; it stays readable with its own key", column.Name);
            return null;
        }
    }

    private string? Reindex(IndexRow row, ProtectedColumn column)
    {
        if (row.Protected is null)
        {
            logger.LogError("An index in {Column} has no value beside it to compute it again from; it stays on its own key", column.Name);
            return null;
        }

        try
        {
            return protector.BlindIndex(Masking.Normalize(protector.Unprotect(row.Protected)!));
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "An index in {Column} could not be computed again with the current key; it stays on its own key", column.Name);
            return null;
        }
    }

    // Whether the current key has been current long enough for blind indexes to move (IndexesSettleAfter).
    private bool IndexesSettled() => ring.CurrentSince is not { } since || time.GetUtcNow() - since >= IndexesSettleAfter;

    // Which blind indexes are not on the current key: the original key's have no key id, a later key's start with it.
    private NpgsqlParameter[] IndexParameters() =>
    [
        new("original", ring.CurrentKeyId == ring.OriginalKeyId),
        new("prefix", ring.CurrentKeyId + ".%"),
    ];

    // The start of everything ProtectBytes writes with the current key: version 1 for the original key, else version 2
    // and the key's id.
    private byte[] CurrentHeader() =>
        ring.CurrentKeyId == ring.OriginalKeyId ? [1] : [2, .. Convert.FromHexString(ring.CurrentKeyId)];

    // The key a v2 string names (v2.<key id>.<payload>); null for a v1 string, which the original key encrypted.
    private static string? KeyOf(string value) =>
        value.Length > 20 && value.StartsWith("v2.", StringComparison.Ordinal) && value[19] == '.' ? value[3..19] : null;

    private async Task<List<SecretSetting>> SecretSettingsAsync(CancellationToken cancellationToken) =>
        (await db.Set<SystemSetting>().IgnoreQueryFilters().AsNoTracking().Where(s => s.IsSecret).Select(s => new { s.Id, s.Value }).ToListAsync(cancellationToken))
            .Select(s => new SecretSetting(s.Id, s.Value, JsonSerializer.Deserialize<string>(s.Value) ?? string.Empty))
            .Where(s => s.Protected.Length > 0)
            .ToList();

    /// <summary>The marked columns of the model, with the SQL for each (table and column names from the model, values as parameters).</summary>
    private List<ProtectedColumn> ProtectedColumns() =>
        db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && e.FindPrimaryKey() is { Properties: [{ ClrType: var key }] } && key == typeof(Guid))
            .SelectMany(e => e.GetProperties().Select(p => ProtectedColumn.For(e, p)).OfType<ProtectedColumn>())
            .ToList();

    private sealed record SecretSetting(Guid Id, string Json, string Protected);

    private sealed class ProtectedRow
    {
        public Guid Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }

    private sealed class IndexRow
    {
        public Guid Id { get; set; }

        public string Index { get; set; } = string.Empty;

        public string? Protected { get; set; }
    }

    private sealed class KeyCount
    {
        public string? Key { get; set; }

        public long Count { get; set; }
    }

    private enum ProtectedKind
    {
        /// <summary><see cref="IFieldProtector.Protect"/> strings, moved.</summary>
        Value,

        /// <summary><see cref="IFieldProtector.ProtectBytes"/> output with one purpose for the column, moved.</summary>
        File,

        /// <summary><see cref="IFieldProtector.ProtectBytes"/> output whose purpose differs from row to row: it expires, and is not moved.</summary>
        Expiring,

        /// <summary><see cref="IFieldProtector.BlindIndex"/> values, computed again from the encrypted value they index.</summary>
        Index,
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Table and column names come from the EF model; values are parameters.")]
    private sealed class ProtectedColumn
    {
        private ProtectedColumn(IEntityType entity, IProperty property, ProtectedKind kind, string purpose, IProperty? indexed = null)
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            var name = TableName(entity);
            var id = Quote(entity.FindPrimaryKey()!.Properties[0].GetColumnName(table)!);
            var column = Quote(property.GetColumnName(table)!);
            Name = $"{entity.GetSchema()}.{entity.GetTableName()}.{property.GetColumnName(table)}";
            Kind = kind;
            Purpose = purpose;
            if (kind == ProtectedKind.Index)
            {
                // Not on the current key: the original key's indexes have no key id; a later key's start with it and a dot.
                var pending = $"{column} IS NOT NULL AND (CASE WHEN @original THEN {column} LIKE '%.%' ELSE {column} NOT LIKE @prefix END)";
                var value = Quote(indexed!.GetColumnName(table)!);
                CountSql = $"SELECT count(*) AS \"Value\" FROM {name} WHERE {pending}";
                SelectSql = $"SELECT {id} AS \"Id\", {column} AS \"Index\", {value} AS \"Protected\" FROM {name} WHERE {pending} AND {id} > @after ORDER BY {id} LIMIT @take";
                UpdateSql = $"UPDATE {name} SET {column} = @value WHERE {id} = @id AND {column} = @old";
                UsageSql = $"SELECT split_part({column}, '.', 1) AS \"Key\", count(*) AS \"Count\" FROM {name} WHERE {column} LIKE '%.%' GROUP BY 1";
            }
            else if (kind == ProtectedKind.Value)
            {
                var pending = $"{column} IS NOT NULL AND {column} NOT LIKE @prefix";
                CountSql = $"SELECT count(*) AS \"Value\" FROM {name} WHERE {pending}";
                SelectSql = $"SELECT {id} AS \"Id\", {column} AS \"Value\" FROM {name} WHERE {pending} AND {id} > @after ORDER BY {id} LIMIT @take";
                UpdateSql = $"UPDATE {name} SET {column} = @value WHERE {id} = @id AND {column} = @old";

                // v2.<key id>.<payload>: the key id is the 16 characters after "v2.".
                UsageSql = $"SELECT substring({column} from 4 for 16) AS \"Key\", count(*) AS \"Count\" FROM {name} WHERE {column} LIKE 'v2.%' GROUP BY 1";
            }
            else
            {
                // Version 2 output starts with the byte 2 and the key id's 8 bytes; version 1 (the original key) with the byte 1.
                var pending = $"{column} IS NOT NULL AND substring({column} from 1 for @length) <> @header";
                CountSql = $"SELECT count(*) AS \"Value\" FROM {name} WHERE {pending}";
                SelectSql = $"SELECT {id} AS \"Value\" FROM {name} WHERE {pending} AND {id} > @after ORDER BY {id} LIMIT @take";
                ReadSql = $"SELECT {column} AS \"Value\" FROM {name} WHERE {id} = @id";
                UpdateSql = $"UPDATE {name} SET {column} = @value WHERE {id} = @id AND substring({column} from 1 for @length) = @head";
                UsageSql = $"SELECT upper(encode(substring({column} from 2 for 8), 'hex')) AS \"Key\", count(*) AS \"Count\" FROM {name} " +
                           $"WHERE substring({column} from 1 for 1) = decode('02', 'hex') GROUP BY 1";
            }
        }

        public string Name { get; }

        public ProtectedKind Kind { get; }

        /// <summary>What the bytes were encrypted for (<see cref="IFieldProtector.ProtectBytes"/>); empty for strings and expiring bytes.</summary>
        public string Purpose { get; }

        public string CountSql { get; }

        public string SelectSql { get; }

        public string ReadSql { get; } = string.Empty;

        public string UpdateSql { get; }

        /// <summary>How many values each key id encrypts, as rows of <see cref="KeyCount"/>.</summary>
        public string UsageSql { get; }

        /// <summary>The column, when it is marked as holding encrypted values; otherwise null.</summary>
        /// <exception cref="InvalidOperationException">A blind index names a value that is not an encrypted column of its table.</exception>
        public static ProtectedColumn? For(IEntityType entity, IProperty property) =>
            property.FindAnnotation(ProtectedValueBuilderExtensions.Annotation)?.Value is true ? new ProtectedColumn(entity, property, ProtectedKind.Value, string.Empty)
            : property.FindAnnotation(ProtectedValueBuilderExtensions.BytesAnnotation)?.Value is string purpose
                ? new ProtectedColumn(entity, property, purpose.Length > 0 ? ProtectedKind.File : ProtectedKind.Expiring, purpose)
            : property.FindAnnotation(ProtectedValueBuilderExtensions.BlindIndexAnnotation)?.Value is string indexed
                ? new ProtectedColumn(entity, property, ProtectedKind.Index, string.Empty, entity.FindProperty(indexed) is { } value
                    && value.FindAnnotation(ProtectedValueBuilderExtensions.Annotation)?.Value is true
                        ? value
                        : throw new InvalidOperationException($"{entity.DisplayName()}.{property.Name} indexes {indexed}, which is not an encrypted column of {entity.DisplayName()}."))
                : null;

        /// <summary>For a jsonb column holding a JSON string: replaces the string if the column still holds the old one.</summary>
        public static string UpdateJsonStringSql(IEntityType entity, IProperty property)
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            var id = Quote(entity.FindPrimaryKey()!.Properties[0].GetColumnName(table)!);
            var column = Quote(property.GetColumnName(table)!);
            return $"UPDATE {TableName(entity)} SET {column} = CAST(@value AS jsonb) WHERE {id} = @id AND {column} = CAST(@old AS jsonb)";
        }

        private static string TableName(IEntityType entity) =>
            entity.GetSchema() is { } schema ? $"{Quote(schema)}.{Quote(entity.GetTableName()!)}" : Quote(entity.GetTableName()!);

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
