using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Downtime;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Downtime UHIDs (NABH IMS, continuity when HC360 is down): blocks of UHIDs issued ahead of time for the registration
/// desks' downtime kits, the registrations entered from paper under them, and the numbers that will never be used.
/// </summary>
public sealed class DowntimeUhidService(
    HospitalDbContext db,
    INumberSeries numbers,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>How far back a paper registration may be dated: the longest downtime HC360 expects to be entered after.</summary>
    public static readonly TimeSpan LongestBackEntry = TimeSpan.FromDays(90);

    private static readonly Error FacilityRequired = Error.Rule("facility.required", "Select a facility first.");

    public async Task<Result<IReadOnlyList<DowntimeUhidBlockSummary>>> ListAsync(CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return FacilityRequired;
        }

        var blocks = await db.Set<DowntimeUhidBlock>().AsNoTracking()
            .Where(b => b.FacilityId == facilityId)
            .OrderByDescending(b => b.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        var ids = blocks.Select(b => b.Id).ToList();
        var counts = await db.Set<DowntimeUhid>().AsNoTracking()
            .Where(u => ids.Contains(u.BlockId))
            .GroupBy(u => u.BlockId)
            .Select(g => new
            {
                BlockId = g.Key,
                Entered = g.Count(u => u.PatientId != null),
                Voided = g.Count(u => u.PatientId == null && u.VoidedAt != null),
            })
            .ToDictionaryAsync(c => c.BlockId, ct);
        return blocks.Select(b => Summary(b, counts.GetValueOrDefault(b.Id)?.Entered ?? 0, counts.GetValueOrDefault(b.Id)?.Voided ?? 0)).ToList();
    }

    public async Task<Result<DowntimeUhidBlockDetail>> GetAsync(Guid id, CancellationToken ct)
    {
        var block = await db.Set<DowntimeUhidBlock>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.FacilityId == currentUser.FacilityId, ct);
        if (block is null)
        {
            return Error.NotFound(nameof(DowntimeUhidBlock), id);
        }

        var rows = await db.Set<DowntimeUhid>().AsNoTracking()
            .Where(u => u.BlockId == id)
            .OrderBy(u => u.Position)
            .ToListAsync(ct);
        var patientIds = rows.Where(u => u.PatientId != null).Select(u => u.PatientId!.Value).ToList();
        var names = await db.Set<Patient>().AsNoTracking()
            .Where(p => patientIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
        var facilityName = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == block.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);

        var entered = rows.Count(u => u.Status == DowntimeUhidStatus.Entered);
        var voided = rows.Count(u => u.Status == DowntimeUhidStatus.Voided);
        return new DowntimeUhidBlockDetail(
            Summary(block, entered, voided),
            facilityName ?? string.Empty,
            [.. rows.Select(u => new DowntimeUhidDto(
                u.Position, u.Uhid, u.Status, u.PatientId, u.PatientId is { } p ? names.GetValueOrDefault(p) : null,
                u.PaperRegisteredAt, u.EnteredAt, u.EnteredByName, u.VoidedAt, u.VoidReason, u.VoidedByName, u.Version))]);
    }

    public async Task<Result<DowntimeUhidBlockDetail>> IssueAsync(IssueDowntimeUhidsRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return FacilityRequired;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var uhids = await numbers.ReserveAsync(PatientsNumberSeries.Uhid, null, request.Count, ct);
        var block = new DowntimeUhidBlock
        {
            FacilityId = facilityId,
            Label = request.Label.Trim(),
            Count = uhids.Count,
            FirstUhid = uhids[0],
            LastUhid = uhids[^1],
            IssuedByName = currentUser.DisplayName ?? currentUser.UserName,
        };
        db.Add(block);
        for (var i = 0; i < uhids.Count; i++)
        {
            db.Add(new DowntimeUhid { BlockId = block.Id, Position = i + 1, Uhid = uhids[i] });
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "patients.downtime_uhids_issued",
            $"Issued {uhids.Count} downtime UHIDs {block.FirstUhid} to {block.LastUhid} for \"{block.Label}\"")
        {
            EntityType = nameof(DowntimeUhidBlock),
            EntityId = block.Id.ToString(),
            Details = new { block.Label, count = uhids.Count, first = block.FirstUhid, last = block.LastUhid },
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(block.Id, ct);
    }

    public async Task<Result> VoidAsync(Guid blockId, VoidDowntimeUhidRequest request, CancellationToken ct)
    {
        var block = await db.Set<DowntimeUhidBlock>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == blockId && b.FacilityId == currentUser.FacilityId, ct);
        var uhid = request.Uhid.Trim().ToUpperInvariant();
        var number = block is null ? null : await db.Set<DowntimeUhid>().FirstOrDefaultAsync(u => u.BlockId == blockId && u.Uhid == uhid, ct);
        if (block is null || number is null)
        {
            return Error.NotFound(nameof(DowntimeUhid), uhid);
        }

        if (number.PatientId is not null)
        {
            return Error.Rule("downtime.uhid_entered", $"{uhid} was entered for a patient, so it is in use.");
        }

        if (number.VoidedAt is not null)
        {
            return Result.Success();
        }

        db.SetExpectedVersion(number, request.Version);
        number.VoidedAt = time.GetUtcNow();
        number.VoidReason = request.Reason.Trim();
        number.VoidedByName = currentUser.DisplayName ?? currentUser.UserName;
        audit.Add(new AuditEntry(AuditCategories.Data, "patients.downtime_uhid_voided", $"Voided downtime UHID {uhid}: {number.VoidReason}")
        {
            EntityType = nameof(DowntimeUhidBlock),
            EntityId = blockId.ToString(),
            Details = new { uhid, reason = number.VoidReason },
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// The downtime UHID a registration from paper is entered under, checked and marked entered for
    /// <paramref name="patient"/> (saved with the patient). Refused when HC360 did not issue it as a downtime UHID, it was
    /// voided or already entered, or the form is dated before the block was issued.
    /// </summary>
    internal async Task<Result<DowntimeUhid>> ClaimAsync(PaperRegistration paper, Patient patient, CancellationToken ct)
    {
        var uhid = paper.Uhid.Trim().ToUpperInvariant();
        var number = await db.Set<DowntimeUhid>().FirstOrDefaultAsync(u => u.Uhid == uhid, ct);
        if (number is null)
        {
            return Error.Field("paper.uhid", $"{uhid} is not a downtime UHID HC360 issued. Check the number on the paper form.");
        }

        if (number.PatientId is not null)
        {
            return Error.Conflict("downtime.uhid_entered", $"{uhid} has already been entered for another patient. Check the paper form, or register the patient with a new UHID.");
        }

        if (number.VoidedAt is not null)
        {
            return Error.Field("paper.uhid", $"{uhid} was voided ({number.VoidReason}) and cannot be used.");
        }

        var issuedAt = await db.Set<DowntimeUhidBlock>().Where(b => b.Id == number.BlockId).Select(b => b.CreatedAt).FirstAsync(ct);
        if (paper.RegisteredAt < issuedAt)
        {
            return Error.Field("paper.registeredAt", "The form is dated before its UHID was issued. Check the date and time on the form.");
        }

        number.PatientId = patient.Id;
        number.PaperRegisteredAt = paper.RegisteredAt;
        number.EnteredAt = time.GetUtcNow();
        number.EnteredByName = currentUser.DisplayName ?? currentUser.UserName;
        audit.Add(new AuditEntry(AuditCategories.Data, "patients.registered_from_paper",
            $"Entered the paper registration of {uhid}, registered {paper.RegisteredAt:yyyy-MM-dd HH:mm} UTC")
        {
            EntityType = nameof(Patient),
            EntityId = patient.Id.ToString(),
            PatientId = patient.Id,
            Details = new { uhid, registeredAt = paper.RegisteredAt },
        });
        return number;
    }

    private static DowntimeUhidBlockSummary Summary(DowntimeUhidBlock b, int entered, int voided) =>
        new(b.Id, b.Label, b.Count, b.FirstUhid, b.LastUhid, b.CreatedAt, b.IssuedByName, entered, voided, b.Count - entered - voided);
}
