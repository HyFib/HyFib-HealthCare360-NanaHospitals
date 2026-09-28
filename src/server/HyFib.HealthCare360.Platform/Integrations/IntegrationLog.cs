using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Integrations;

public enum IntegrationDirection
{
    Outbound = 0,
    Inbound,
}

/// <summary>Request/response trace for every integration call, with phone numbers and secrets masked.</summary>
[NotAudited]
public sealed class IntegrationLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; }
    public string Integration { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public IntegrationDirection Direction { get; set; }
    public bool Success { get; set; }
    public int? DurationMs { get; set; }
    public string? Reference { get; set; }
    public string? Request { get; set; }
    public string? Response { get; set; }
    public string? Error { get; set; }
    public Guid? FacilityId { get; set; }
    public string? CorrelationId { get; set; }
}

internal sealed class IntegrationLogConfiguration : IEntityTypeConfiguration<IntegrationLog>
{
    public void Configure(EntityTypeBuilder<IntegrationLog> b)
    {
        b.ToTable("integration_logs", "integrations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Integration).HasMaxLength(32);
        b.Property(x => x.Provider).HasMaxLength(64);
        b.Property(x => x.Operation).HasMaxLength(64);
        b.Property(x => x.Reference).HasMaxLength(128);
        b.Property(x => x.Request).HasMaxLength(8000);
        b.Property(x => x.Response).HasMaxLength(8000);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.Property(x => x.CorrelationId).HasMaxLength(128);
        b.HasIndex(x => new { x.Integration, x.CreatedAt });
    }
}

public sealed record IntegrationLogEntry(
    string Integration,
    string Provider,
    string Operation,
    IntegrationDirection Direction,
    bool Success,
    TimeSpan? Duration = null,
    string? Reference = null,
    string? Request = null,
    string? Response = null,
    string? Error = null);

public interface IIntegrationLogger
{
    Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken = default);
}

internal sealed partial class IntegrationLogger(
    IDbContextFactory<HospitalDbContext> contextFactory,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<IntegrationLogger> logger) : IIntegrationLogger
{
    private const int MaxLength = 8000;

    public async Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            db.Set<IntegrationLog>().Add(new IntegrationLog
            {
                CreatedAt = time.GetUtcNow(),
                Integration = entry.Integration,
                Provider = entry.Provider,
                Operation = entry.Operation,
                Direction = entry.Direction,
                Success = entry.Success,
                DurationMs = entry.Duration is { } d ? (int)d.TotalMilliseconds : null,
                Reference = entry.Reference,
                Request = Sanitize(entry.Request),
                Response = Sanitize(entry.Response),
                Error = Truncate(entry.Error, 2000),
                FacilityId = currentUser.FacilityId,
                CorrelationId = currentUser.CorrelationId,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Logging must never break the integration call itself.
            logger.LogWarning(ex, "Could not write integration log for {Integration}/{Operation}", entry.Integration, entry.Operation);
        }
    }

    /// <summary>Masks phone numbers, e-mail addresses and obvious secrets before persisting payloads.</summary>
    internal static string? Sanitize(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return payload;
        }

        var masked = SecretRegex().Replace(payload, m => m.Groups["name"].Value + m.Groups["sep"].Value + "\"***\"");
        masked = PhoneRegex().Replace(masked, m => m.Value[..Math.Min(4, m.Value.Length)] + new string('*', Math.Max(m.Value.Length - 6, 0)) + m.Value[^2..]);
        masked = EmailRegex().Replace(masked, m => m.Value[0] + "***@" + m.Groups["domain"].Value);
        return Truncate(masked, MaxLength);
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    [GeneratedRegex("(?<name>\"(?:api_?key|apikey|token|secret|password|authorization|auth_token|key_secret)\")(?<sep>\\s*:\\s*)\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"\+?\d{10,13}")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@(?<domain>[A-Za-z0-9.-]+\.[A-Za-z]{2,})")]
    private static partial Regex EmailRegex();
}
