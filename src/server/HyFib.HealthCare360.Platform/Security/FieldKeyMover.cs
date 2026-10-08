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

    /// <summary>
    /// Marks a column that holds <see cref="IFieldProtector.Protect"/> strings, so that after a key rotation
    /// <see cref="FieldKeyMover"/> moves its values to the current key (SRS SEC-07).
    /// </summary>
    public static PropertyBuilder<T> IsProtectedValue<T>(this PropertyBuilder<T> property) => property.HasAnnotation(Annotation, true);
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

/// <summary>What is still encrypted with an earlier key: values in the database, and stored documents.</summary>
public sealed record FieldKeyBacklog(long Values, long Documents)
{
    public bool IsEmpty => Values == 0 && Documents == 0;
}

/// <summary>What one pass moved, what could not be moved (and stays readable with its own key), and whether documents had to wait for a backup.</summary>
public sealed record FieldKeyMoveResult(int Values, int Documents, int Failed, bool DocumentsWaited);

/// <summary>
/// After a rotation (SRS SEC-07), moves what was encrypted with an earlier key to the current one: the columns marked
/// <see cref="ProtectedValueBuilderExtensions.IsProtectedValue{T}"/> in every module, secret settings, and stored
/// documents. Each value is decrypted and encrypted again, and written only if it has not changed meanwhile, so it never
/// races a user's edit. Rows are updated directly, without audit entries for each (the rotation itself is audited).
/// Short-lived encrypted data (stored responses for repeated requests, kept 24 hours, and scheduled report files, kept
/// 30 days by default) is not moved: it stays readable with its key until it expires.
/// </summary>
internal sealed class FieldKeyMover(HospitalDbContext db, IFieldProtector protector, IFieldKeyRing ring, DataDirectories directories, ILogger<FieldKeyMover> logger)
{
    private const int ValueBatch = 200;
    private const int DocumentBatch = 20;
    private const string FilesPurpose = "files";

    public async Task<FieldKeyBacklog> CountAsync(CancellationToken cancellationToken)
    {
        // Until the first rotation everything is on the one key, so there is nothing to look for.
        if (ring.Keys.Count == 1)
        {
            return new FieldKeyBacklog(0, 0);
        }

        var prefix = ring.CurrentPrefix + "%";
        long values = 0;
        foreach (var column in ProtectedColumns())
        {
            values += await db.Database.SqlQueryRaw<long>(column.CountSql, new NpgsqlParameter("prefix", prefix)).SingleAsync(cancellationToken);
        }

        values += (await SecretSettingsAsync(cancellationToken)).Count(s => !s.Protected.StartsWith(ring.CurrentPrefix, StringComparison.Ordinal));
        return new FieldKeyBacklog(values, await DocumentsToMove().LongCountAsync(cancellationToken));
    }

    /// <summary>One pass over everything still on an earlier key. A value that cannot be moved is logged and skipped.</summary>
    public async Task<FieldKeyMoveResult> MoveAsync(CancellationToken cancellationToken)
    {
        var values = 0;
        var failed = 0;
        foreach (var column in ProtectedColumns())
        {
            var (moved, failures) = await MoveColumnAsync(column, cancellationToken);
            values += moved;
            failed += failures;
        }

        var (settings, settingFailures) = await MoveSecretSettingsAsync(cancellationToken);
        var (documents, documentFailures, waited) = await MoveDocumentsAsync(cancellationToken);
        return new FieldKeyMoveResult(values + settings, documents, failed + settingFailures + documentFailures, waited);
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
            if (ring.KeyIdOf(stored) != ring.CurrentKeyId)
            {
                // Written beside the document, then put in its place in one step: a reader sees one or the other.
                await File.WriteAllBytesAsync(temp, protector.ProtectBytes(protector.UnprotectBytes(stored, FilesPurpose), FilesPurpose), cancellationToken);
                File.Move(temp, path, overwrite: true);
            }

            file.EncryptionKeyId = ring.CurrentKeyId;
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

    private async Task<List<SecretSetting>> SecretSettingsAsync(CancellationToken cancellationToken) =>
        (await db.Set<SystemSetting>().IgnoreQueryFilters().AsNoTracking().Where(s => s.IsSecret).Select(s => new { s.Id, s.Value }).ToListAsync(cancellationToken))
            .Select(s => new SecretSetting(s.Id, s.Value, JsonSerializer.Deserialize<string>(s.Value) ?? string.Empty))
            .Where(s => s.Protected.Length > 0)
            .ToList();

    /// <summary>The marked columns of the model, with the SQL for each (table and column names from the model, values as parameters).</summary>
    private List<ProtectedColumn> ProtectedColumns() =>
        db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && e.FindPrimaryKey() is { Properties: [{ ClrType: var key }] } && key == typeof(Guid))
            .SelectMany(e => e.GetProperties().Where(p => p.FindAnnotation(ProtectedValueBuilderExtensions.Annotation)?.Value is true).Select(p => new ProtectedColumn(e, p)))
            .ToList();

    private sealed record SecretSetting(Guid Id, string Json, string Protected);

    private sealed class ProtectedRow
    {
        public Guid Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Table and column names come from the EF model; values are parameters.")]
    private sealed class ProtectedColumn
    {
        public ProtectedColumn(IEntityType entity, IProperty property)
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            var name = TableName(entity);
            var id = Quote(entity.FindPrimaryKey()!.Properties[0].GetColumnName(table)!);
            var column = Quote(property.GetColumnName(table)!);
            Name = $"{entity.GetSchema()}.{entity.GetTableName()}.{property.GetColumnName(table)}";
            var pending = $"{column} IS NOT NULL AND {column} NOT LIKE @prefix";
            CountSql = $"SELECT count(*) AS \"Value\" FROM {name} WHERE {pending}";
            SelectSql = $"SELECT {id} AS \"Id\", {column} AS \"Value\" FROM {name} WHERE {pending} AND {id} > @after ORDER BY {id} LIMIT @take";
            UpdateSql = $"UPDATE {name} SET {column} = @value WHERE {id} = @id AND {column} = @old";
        }

        public string Name { get; }

        public string CountSql { get; }

        public string SelectSql { get; }

        public string UpdateSql { get; }

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
