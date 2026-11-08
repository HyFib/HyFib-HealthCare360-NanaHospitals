using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Organization;

/// <summary>What ABDM's Healthcare Professionals Registry holds for an HPR ID.</summary>
/// <param name="Name">The professional's name as registered.</param>
/// <param name="Council">The registration council (a state medical council, the National Medical Commission, a nursing council…).</param>
/// <param name="RegistrationNumber">The registration number with that council.</param>
/// <param name="Active">False when the registry marks the registration suspended or removed.</param>
public sealed record HprRegistryRecord(string HprId, string Name, string Council, string RegistrationNumber, bool Active);

/// <summary>
/// ABDM's Healthcare Professionals Registry, asked for one HPR ID. <see cref="Source"/> names where the answer came from,
/// and is shown beside every check, so a check against the simulator is never taken for one against the registry.
/// </summary>
public interface IHprRegistry
{
    string Source { get; }

    /// <summary>The registry's record for the id (written <c>xx-xxxx-xxxx-xxxx</c>), or null when it holds none.</summary>
    Task<HprRegistryRecord?> FindAsync(string hprId, CancellationToken ct);
}

/// <summary>
/// The registry until the hospital is connected to ABDM's HPR service: it knows only the records added to it (by a test,
/// or by an administrator's demo set-up) and says so: every check made against it is labelled "simulator".
/// </summary>
public sealed class SimulatedHprRegistry : IHprRegistry
{
    private readonly ConcurrentDictionary<string, HprRegistryRecord> _records = new(StringComparer.Ordinal);

    public string Source => "simulator";

    public void Add(HprRegistryRecord record) => _records[Hpr.Format(record.HprId)] = record with { HprId = Hpr.Format(record.HprId) };

    public Task<HprRegistryRecord?> FindAsync(string hprId, CancellationToken ct) =>
        Task.FromResult(_records.GetValueOrDefault(Hpr.Format(hprId)));
}

public enum HprCheckResult
{
    /// <summary>The registry holds the id, active, with the same name, council and registration number.</summary>
    Verified,

    /// <summary>The registry holds the id, but something differs (listed), or the registration is not active.</summary>
    Mismatch,

    /// <summary>The registry holds no such id.</summary>
    NotFound,
}

/// <summary>
/// One check of a practitioner's HPR ID against the registry: what the master said when it was checked, what the registry
/// said, and the result. Kept as history; the practitioner master shows the latest, and whether the master has changed
/// since (then the check no longer vouches for it).
/// </summary>
public sealed class PractitionerHprCheck : Entity
{
    public Guid PractitionerId { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    public Guid CheckedBy { get; set; }

    public HprCheckResult Result { get; set; }

    /// <summary>The fields that differ: <c>name</c>, <c>council</c>, <c>registrationNumber</c>, <c>status</c>.</summary>
    public List<string> Mismatches { get; set; } = [];

    /// <summary>The master's HPR ID, council, registration number and name as checked (<see cref="PractitionerHprChecks.Snapshot"/>).</summary>
    public string CheckedValues { get; set; } = string.Empty;

    public string? RegistryName { get; set; }

    public string? RegistryCouncil { get; set; }

    public string? RegistryNumber { get; set; }

    public string Source { get; set; } = string.Empty;
}

internal sealed class PractitionerHprCheckConfiguration : IEntityTypeConfiguration<PractitionerHprCheck>
{
    public void Configure(EntityTypeBuilder<PractitionerHprCheck> b)
    {
        b.ToTable("practitioner_hpr_checks", "org");
        b.Property(x => x.Result).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CheckedValues).HasMaxLength(600);
        b.Property(x => x.RegistryName).HasMaxLength(200);
        b.Property(x => x.RegistryCouncil).HasMaxLength(128);
        b.Property(x => x.RegistryNumber).HasMaxLength(64);
        b.Property(x => x.Source).HasMaxLength(32);
        b.HasOne<Practitioner>().WithMany().HasForeignKey(x => x.PractitionerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PractitionerId, x.CheckedAt });
    }
}

/// <summary>The latest HPR check as the practitioner master shows it.</summary>
/// <param name="Current">False when the master's HPR ID, council, registration number or name changed since the check.</param>
public sealed record PractitionerHprCheckDto(
    HprCheckResult Result, IReadOnlyList<string> Mismatches, DateTimeOffset CheckedAt, string CheckedByName, string Source,
    string? RegistryName, string? RegistryCouncil, string? RegistryNumber, bool Current);

/// <summary>Check the practitioner as last read (<see cref="Version"/>), so the check is of what the administrator saw.</summary>
public sealed record CheckHprRequest(uint Version);

public sealed class CheckHprRequestValidator : AbstractValidator<CheckHprRequest>
{
    public CheckHprRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

/// <summary>
/// HPR verification (ABDM HPR): the practitioner's HPR ID looked up in the registry, and the registry's name, council and
/// registration number compared with the master. Names compare without titles (Dr., Prof.), case, accents, punctuation
/// or the order of their words; councils compare without case, punctuation or the words "council", "medical", "of" and
/// "the" (abbreviations are not guessed: "TN" is not "Tamil Nadu"); registration numbers compare without case, spaces,
/// hyphens, slashes or leading zeros.
/// </summary>
public sealed class PractitionerHprChecks(HospitalDbContext db, IHprRegistry registry, ICurrentUser user, IAuditLogger audit, TimeProvider time)
{
    private static readonly HashSet<string> Titles = new(StringComparer.Ordinal) { "dr", "prof", "mr", "mrs", "ms", "miss", "shri", "smt", "sri" };

    private static readonly HashSet<string> CouncilFillers = new(StringComparer.Ordinal) { "council", "medical", "of", "the" };

    public static string Snapshot(Practitioner p) =>
        string.Join('|', p.HprId ?? string.Empty, p.RegistrationCouncil ?? string.Empty, p.RegistrationNumber ?? string.Empty, p.DisplayName);

    public async Task<Result<PractitionerHprCheckDto>> CheckAsync(Guid practitionerId, uint version, CancellationToken ct)
    {
        var practitioner = await db.Set<Practitioner>().FirstOrDefaultAsync(p => p.Id == practitionerId, ct);
        if (practitioner is null)
        {
            return Error.NotFound(nameof(Practitioner), practitionerId);
        }

        if (practitioner.Version != version)
        {
            return Error.Conflict("practitioner.changed", "The practitioner was changed since you opened it. Open it again before checking the HPR ID.");
        }

        if (string.IsNullOrWhiteSpace(practitioner.HprId) || !Hpr.IsValidId(practitioner.HprId))
        {
            return Error.Rule("hpr.missing", "Enter the practitioner's 14-digit HPR ID and save it before checking it with the registry.");
        }

        var record = await registry.FindAsync(practitioner.HprId, ct);
        var mismatches = record is null ? [] : Compare(practitioner, record);
        var check = new PractitionerHprCheck
        {
            PractitionerId = practitioner.Id,
            CheckedAt = time.GetUtcNow(),
            CheckedBy = user.UserId ?? Guid.Empty,
            Result = record is null ? HprCheckResult.NotFound : mismatches.Count == 0 ? HprCheckResult.Verified : HprCheckResult.Mismatch,
            Mismatches = mismatches,
            CheckedValues = Snapshot(practitioner),
            RegistryName = record?.Name,
            RegistryCouncil = record?.Council,
            RegistryNumber = record?.RegistrationNumber,
            Source = registry.Source,
        };
        db.Add(check);
        audit.Add(new AuditEntry(AuditCategories.Admin, "practitioner.hpr_checked",
            $"HPR ID of {practitioner.DisplayName} checked with the registry ({registry.Source}): {check.Result}")
        {
            EntityType = nameof(Practitioner),
            EntityId = practitioner.Id.ToString(),
            Details = new { result = check.Result.ToString(), mismatches, source = registry.Source },
        });
        await db.SaveChangesAsync(ct);
        return (await LatestAsync(practitioner, ct))!;
    }

    /// <summary>The latest check of the practitioner, and whether it still vouches for the master as it is.</summary>
    public async Task<PractitionerHprCheckDto?> LatestAsync(Practitioner practitioner, CancellationToken ct)
    {
        var latest = await db.Set<PractitionerHprCheck>().AsNoTracking()
            .Where(c => c.PractitionerId == practitioner.Id)
            .OrderByDescending(c => c.CheckedAt)
            .FirstOrDefaultAsync(ct);
        if (latest is null)
        {
            return null;
        }

        var name = await db.Set<Identity.User>().IgnoreQueryFilters().Where(u => u.Id == latest.CheckedBy).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        return new PractitionerHprCheckDto(latest.Result, latest.Mismatches, latest.CheckedAt, name ?? "Unknown", latest.Source,
            latest.RegistryName, latest.RegistryCouncil, latest.RegistryNumber, latest.CheckedValues == Snapshot(practitioner));
    }

    /// <summary>What differs between the master and the registry's record, in the order the screen lists them.</summary>
    public static List<string> Compare(Practitioner practitioner, HprRegistryRecord record)
    {
        var mismatches = new List<string>();
        if (!NameWords(practitioner.DisplayName).SetEquals(NameWords(record.Name)))
        {
            mismatches.Add("name");
        }

        if (Council(practitioner.RegistrationCouncil) != Council(record.Council))
        {
            mismatches.Add("council");
        }

        if (RegistrationNumber(practitioner.RegistrationNumber) != RegistrationNumber(record.RegistrationNumber))
        {
            mismatches.Add("registrationNumber");
        }

        if (!record.Active)
        {
            mismatches.Add("status");
        }

        return mismatches;
    }

    private static IEnumerable<string> Words(string? value)
    {
        var folded = new StringBuilder();
        foreach (var c in (value ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            folded.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static HashSet<string> NameWords(string? name) => [.. Words(name).Where(w => !Titles.Contains(w))];

    private static string Council(string? council) => string.Join(' ', Words(council).Where(w => !CouncilFillers.Contains(w)));

    /// <summary>Letters and digits only, lower-cased, each run of digits without its leading zeros (TNMC/0012345 is tnmc12345).</summary>
    private static string RegistrationNumber(string? number) =>
        System.Text.RegularExpressions.Regex.Replace(string.Concat(Words(number)), "(?<![0-9])0+(?=[0-9])", string.Empty);
}

internal static class PractitionerHprEndpoints
{
    public static void Map(RouteGroupBuilder masters) =>
        masters.MapPost("/practitioners/{id:guid}/hpr-check", async (Guid id, CheckHprRequest request, PractitionerHprChecks checks, CancellationToken ct) =>
                (await checks.CheckAsync(id, request.Version, ct)).ToOk())
            .RequirePermission(PlatformPermissions.MastersManage)
            .WithValidation<CheckHprRequest>();
}
