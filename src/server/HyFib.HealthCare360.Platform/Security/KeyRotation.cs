using System.Globalization;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>When a key was last rotated, and by whom (from the audit log).</summary>
public sealed record KeyRotationEvent(DateTimeOffset At, string? By);

/// <summary>
/// The data encryption key: the current key's id and age, how many keys the ring holds, and what is still encrypted with
/// an earlier key (moved in the background).
/// </summary>
public sealed record DataKeyStatus(string KeyId, DateTimeOffset? Since, int Keys, long ValuesToMove, long DocumentsToMove, KeyRotationEvent? LastRotation)
{
    /// <summary>The keys rotations added that are no longer current, oldest first: the ones that can be retired.</summary>
    public IReadOnlyList<EarlierDataKey> Earlier { get; init; } = [];
}

/// <summary>
/// A key a rotation added, no longer current, and what it still encrypts: values (identifiers and secret settings),
/// documents and scheduled report files, and stored answers to repeated requests, which expire within a day. It can be
/// retired once it encrypts nothing and the current key has been current for a day (<see cref="RetireFrom"/>).
/// </summary>
/// <param name="RetireFrom">When the current key will have been current for a day, while it has not; null once it has.</param>
public sealed record EarlierDataKey(string KeyId, DateTimeOffset? Since, long Values, long Documents, long Expiring, DateTimeOffset? RetireFrom, bool CanRetire);

/// <summary>The key that signs sign-in (access) tokens.</summary>
public sealed record SigningKeyStatus(string KeyId, DateTimeOffset? Since, KeyRotationEvent? LastRotation);

public sealed record EncryptionKeysStatus(DataKeyStatus DataKey, SigningKeyStatus SigningKey);

/// <summary>
/// Rotating the server's keys from System health (SRS SEC-07; ASVS V11.1.1, V11.2.2), one at a time and audited:
/// <list type="bullet">
/// <item>the data encryption key (<see cref="IFieldKeyRing"/>): new values use a new key at once, and
/// <see cref="FieldKeyMoverService"/> moves what was encrypted before in the background. An earlier key is retired, by an
/// administrator who gives their password again, once nothing is encrypted with it any more;</item>
/// <item>the token signing key (<see cref="SigningKeyStore"/>): new access tokens are signed with a new key, and those
/// already issued stay valid until they expire.</item>
/// </list>
/// </summary>
internal sealed class KeyRotationService(
    HospitalDbContext db,
    IFieldKeyRing ring,
    SigningKeyStore signing,
    FieldKeyMover mover,
    FieldKeyMoveSignal signal,
    AuthService auth,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<KeyRotationService> logger)
{
    public const string DataKeyRotated = "security.data_key_rotated";
    public const string DataKeyRetired = "security.data_key_retired";
    public const string SigningKeyRotated = "security.signing_key_rotated";

    /// <summary>
    /// How long after a rotation an earlier key can be retired: by then anything that was being encrypted with it as the
    /// key changed (a long import, say) has been written, so it is found and moved.
    /// </summary>
    public static readonly TimeSpan RetireAfter = TimeSpan.FromDays(1);

    /// <summary>The PostgreSQL advisory lock that keeps key changes one at a time across the nodes sharing the keys (SCAL-01).</summary>
    internal const long ClusterLockKey = 4_247_362;

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>
    /// Takes the cluster-wide key lock until the returned transaction ends, then reads the keys again: another node sharing
    /// the key folder may have changed them a moment ago (SCAL-01). On a single Server Node the lock is never contended.
    /// </summary>
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> LockKeysAsync(CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ClusterLockKey})", ct);
        ring.Refresh();
        signing.Refresh();
        return transaction;
    }

    public async Task<EncryptionKeysStatus> StatusAsync(CancellationToken ct)
    {
        var backlog = await mover.CountAsync(ct);
        var keys = ring.Keys;
        var current = keys.Single(k => k.Current);
        var earlier = await EarlierAsync(ct);
        return new EncryptionKeysStatus(
            new DataKeyStatus(current.Id, current.CreatedAt, keys.Count, backlog.Values, backlog.Documents, await LastAsync(DataKeyRotated, ct)) { Earlier = earlier },
            new SigningKeyStatus(signing.Key.KeyId!, signing.KeyCreatedAt, await LastAsync(SigningKeyRotated, ct)));
    }

    public async Task<EncryptionKeysStatus> RotateDataKeyAsync(CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            await using var keys = await LockKeysAsync(ct);
            var previous = ring.CurrentKeyId;
            var id = ring.Rotate();
            logger.LogWarning("Data encryption key rotated from {Previous} to {Current}; earlier data moves to it in the background", previous, id);
            await audit.WriteAsync(new AuditEntry(AuditCategories.Security, DataKeyRotated,
                $"Rotated the data encryption key from {previous} to {id}; data encrypted before moves to it in the background")
            {
                Severity = AuditSeverity.Warning,
            }, CancellationToken.None);
            await keys.CommitAsync(CancellationToken.None);
        }
        finally
        {
            OneAtATime.Release();
        }

        signal.Wake();
        return await StatusAsync(ct);
    }

    public async Task<Result<EncryptionKeysStatus>> RotateSigningKeyAsync(CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            await using var keys = await LockKeysAsync(ct);

            // Tokens signed with the previous key are still valid, and only one previous key is kept.
            if (signing.NextRotationAt is { } next)
            {
                var minutes = Math.Max(1, (int)Math.Ceiling((next - time.GetUtcNow()).TotalMinutes));
                return Error.Rule("keys.signing_rotated_recently",
                    $"The sign-in token key was rotated a few minutes ago, and tokens signed before that are still in use. Rotate it again in {minutes} minute(s).");
            }

            var previous = signing.Key.KeyId;
            var id = signing.Rotate();
            logger.LogWarning("Token signing key rotated from {Previous} to {Current}", previous, id);
            await audit.WriteAsync(new AuditEntry(AuditCategories.Security, SigningKeyRotated,
                $"Rotated the sign-in token signing key from {previous} to {id}; tokens signed with the previous key stay valid until they expire")
            {
                Severity = AuditSeverity.Warning,
            }, CancellationToken.None);
            await keys.CommitAsync(CancellationToken.None);
        }
        finally
        {
            OneAtATime.Release();
        }

        return await StatusAsync(ct);
    }

    /// <summary>
    /// Retires an earlier data key (ASVS V11.1.1): deletes it from this server once nothing is encrypted with it and the
    /// current key has been current for a day, for a holder of <c>admin.system.manage</c> who gives their password again.
    /// Backups made before keep their copy of it, so they still restore, and a restore of one puts it back.
    /// </summary>
    public async Task<Result<EncryptionKeysStatus>> RetireDataKeyAsync(string keyId, ReauthenticateRequest request, CancellationToken ct)
    {
        var confirmed = await auth.ConfirmPasswordAsync(request, ct);
        if (confirmed.IsFailure)
        {
            return confirmed.Error!;
        }

        await OneAtATime.WaitAsync(ct);
        try
        {
            await using var keys = await LockKeysAsync(ct);
            var id = keyId.Trim().ToUpperInvariant();
            var key = ring.Keys.FirstOrDefault(k => k.Id == id);
            if (key is null)
            {
                return Error.NotFound("Data key", keyId);
            }

            if (key.Original)
            {
                return Error.Rule("keys.original_kept",
                    "This is the original data key, which is never retired: searching by Aadhaar number, and backups made before the first new recovery key, depend on it.");
            }

            if (key.Current)
            {
                return Error.Rule("keys.current_key", "This key encrypts new data. Rotate the data key first; this one can be retired once nothing is encrypted with it.");
            }

            var earlier = (await EarlierAsync(ct)).Single(k => k.KeyId == id);
            if (!earlier.CanRetire)
            {
                return earlier.RetireFrom is { } from && earlier.Values + earlier.Documents + earlier.Expiring == 0
                    ? Error.Rule("keys.rotated_recently",
                        $"The data key was rotated less than a day ago, so something may still be being written with key {id}. Retire it in {Math.Max(1, (int)Math.Ceiling((from - time.GetUtcNow()).TotalHours))} hour(s).")
                    : Error.Rule("keys.in_use", $"Key {id} still encrypts {InUse(earlier)}. Retire it once nothing is left on it.");
            }

            try
            {
                ring.Retire(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Data encryption key {KeyId} could not be deleted", id);
                return Error.Rule("keys.not_deleted", $"Key {id} could not be deleted from the server, so nothing changed. Try again in a few minutes.");
            }

            logger.LogWarning("Data encryption key {KeyId} retired: nothing was encrypted with it any more", id);
            await audit.WriteAsync(new AuditEntry(AuditCategories.Security, DataKeyRetired,
                $"Retired data encryption key {id}, which nothing was encrypted with any more; backups made before keep a copy of it")
            {
                Severity = AuditSeverity.Warning,
            }, CancellationToken.None);
            await keys.CommitAsync(CancellationToken.None);
        }
        finally
        {
            OneAtATime.Release();
        }

        return await StatusAsync(ct);
    }

    // The keys rotations added that are no longer current, oldest first, with what each still encrypts.
    private async Task<IReadOnlyList<EarlierDataKey>> EarlierAsync(CancellationToken ct)
    {
        var keys = ring.Keys.Where(k => !k.Current && !k.Original).ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        var usage = await mover.UsageAsync(ct);
        var from = ring.CurrentSince + RetireAfter;
        var wait = from > time.GetUtcNow() ? from : null;
        return keys.Select(k =>
            {
                var use = usage.GetValueOrDefault(k.Id) ?? FieldKeyUse.None;
                return new EarlierDataKey(k.Id, k.CreatedAt, use.Values, use.Documents, use.Expiring, wait, use.IsEmpty && wait is null);
            })
            .ToList();
    }

    // "12 values, 3 documents or report files and 1 stored answer to a repeated request".
    private static string InUse(EarlierDataKey key)
    {
        var parts = new List<string>();
        if (key.Values > 0)
        {
            parts.Add(Count(key.Values, "value", "values"));
        }

        if (key.Documents > 0)
        {
            parts.Add(Count(key.Documents, "document or report file", "documents or report files"));
        }

        if (key.Expiring > 0)
        {
            parts.Add(Count(key.Expiring, "stored answer to a repeated request, which expires within a day", "stored answers to repeated requests, which expire within a day"));
        }

        return parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];

        static string Count(long count, string one, string many) => count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
    }

    private async Task<KeyRotationEvent?> LastAsync(string action, CancellationToken ct) =>
        await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == action)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new KeyRotationEvent(e.OccurredAt, e.UserName))
            .FirstOrDefaultAsync(ct);
}

/// <summary>Wakes <see cref="FieldKeyMoverService"/> straight after a rotation on this node.</summary>
public sealed class FieldKeyMoveSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    internal Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Moves data encrypted with an earlier key to the current one (<see cref="FieldKeyMover"/>): straight after a rotation on
/// this node, and every 15 minutes while anything is left (after a restart, or a pass that had to wait for a backup).
/// Runs on nodes that run jobs.
/// </summary>
internal sealed class FieldKeyMoverService(IServiceScopeFactory scopes, FieldKeyMoveSignal signal, ILogger<FieldKeyMoverService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the server finish starting first.
        await signal.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var mover = scope.ServiceProvider.GetRequiredService<FieldKeyMover>();
                if (!(await mover.CountAsync(stoppingToken)).IsEmpty)
                {
                    var moved = await mover.MoveAsync(stoppingToken);
                    logger.LogInformation(
                        "Moved {Values} encrypted values and {Documents} documents to the current data key ({Failed} could not be moved{Waiting})",
                        moved.Values, moved.Documents, moved.Failed, moved.DocumentsWaited ? "; documents wait for a backup to finish" : string.Empty);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Moving encrypted data to the current data key failed; it will be tried again");
            }

            await signal.WaitAsync(Interval, stoppingToken);
        }
    }
}
