using HyFib.HealthCare360.Platform.Auditing;
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
public sealed record DataKeyStatus(string KeyId, DateTimeOffset? Since, int Keys, long ValuesToMove, long DocumentsToMove, KeyRotationEvent? LastRotation);

/// <summary>The key that signs sign-in (access) tokens.</summary>
public sealed record SigningKeyStatus(string KeyId, DateTimeOffset? Since, KeyRotationEvent? LastRotation);

public sealed record EncryptionKeysStatus(DataKeyStatus DataKey, SigningKeyStatus SigningKey);

/// <summary>
/// Rotating the server's keys from System health (SRS SEC-07; ASVS V11.2.2), one at a time and audited:
/// <list type="bullet">
/// <item>the data encryption key (<see cref="IFieldKeyRing"/>): new values use a new key at once, and
/// <see cref="FieldKeyMoverService"/> moves what was encrypted before in the background;</item>
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
    IAuditLogger audit,
    TimeProvider time,
    ILogger<KeyRotationService> logger)
{
    public const string DataKeyRotated = "security.data_key_rotated";
    public const string SigningKeyRotated = "security.signing_key_rotated";

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<EncryptionKeysStatus> StatusAsync(CancellationToken ct)
    {
        var backlog = await mover.CountAsync(ct);
        var current = ring.Keys.Single(k => k.Current);
        return new EncryptionKeysStatus(
            new DataKeyStatus(current.Id, current.CreatedAt, ring.Keys.Count, backlog.Values, backlog.Documents, await LastAsync(DataKeyRotated, ct)),
            new SigningKeyStatus(signing.Key.KeyId!, signing.KeyCreatedAt, await LastAsync(SigningKeyRotated, ct)));
    }

    public async Task<EncryptionKeysStatus> RotateDataKeyAsync(CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            var previous = ring.CurrentKeyId;
            var id = ring.Rotate();
            logger.LogWarning("Data encryption key rotated from {Previous} to {Current}; earlier data moves to it in the background", previous, id);
            await audit.WriteAsync(new AuditEntry(AuditCategories.Security, DataKeyRotated,
                $"Rotated the data encryption key from {previous} to {id}; data encrypted before moves to it in the background")
            {
                Severity = AuditSeverity.Warning,
            }, CancellationToken.None);
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
        }
        finally
        {
            OneAtATime.Release();
        }

        return await StatusAsync(ct);
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
