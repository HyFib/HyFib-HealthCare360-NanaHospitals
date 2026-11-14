using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Downtime;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Downtime visit, bill, receipt and pharmacy sale numbers (NABH IMS, continuity when HC360 is down), as downtime UHIDs work
/// (<see cref="DowntimeUhidService"/>): blocks issued ahead to a facility's counters, consumed in order on paper, entered
/// under the number on the paper when HC360 is back, never issued again; the numbers not used are voided, one by one or
/// by returning the block.
/// </summary>
public sealed class DowntimeNumberService(
    HospitalDbContext db,
    INumberSeries numbers,
    IEnumerable<DowntimeNumberSeries> series,
    BackEntryService backEntry,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider time)
{
    private static readonly Error FacilityRequired = Error.Rule("facility.required", "Select a facility first.");

    public DowntimeNumberSeries SeriesOf(DowntimeNumberKind kind) =>
        series.FirstOrDefault(s => s.Kind == kind) ?? throw new InvalidOperationException($"No series is registered for downtime {kind} numbers.");

    public async Task<Result<IReadOnlyList<DowntimeNumberBlockSummary>>> ListAsync(DowntimeNumberKind? kind, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return FacilityRequired;
        }

        var blocks = await db.Set<DowntimeNumberBlock>().AsNoTracking()
            .Where(b => b.FacilityId == facilityId && (kind == null || b.Kind == kind))
            .OrderByDescending(b => b.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        var ids = blocks.Select(b => b.Id).ToList();
        var counts = await db.Set<DowntimeNumber>().AsNoTracking()
            .Where(n => ids.Contains(n.BlockId))
            .GroupBy(n => n.BlockId)
            .Select(g => new { BlockId = g.Key, Entered = g.Count(n => n.EntityId != null), Voided = g.Count(n => n.EntityId == null && n.VoidedAt != null) })
            .ToDictionaryAsync(c => c.BlockId, ct);
        return blocks.Select(b => Summary(b, counts.GetValueOrDefault(b.Id)?.Entered ?? 0, counts.GetValueOrDefault(b.Id)?.Voided ?? 0)).ToList();
    }

    public async Task<Result<DowntimeNumberBlockDetail>> GetAsync(Guid id, CancellationToken ct)
    {
        var block = await db.Set<DowntimeNumberBlock>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.FacilityId == currentUser.FacilityId, ct);
        if (block is null)
        {
            return Error.NotFound(nameof(DowntimeNumberBlock), id);
        }

        var rows = await db.Set<DowntimeNumber>().AsNoTracking().Where(n => n.BlockId == id).OrderBy(n => n.Position).ToListAsync(ct);
        var patientIds = rows.Where(n => n.PatientId != null).Select(n => n.PatientId!.Value).Distinct().ToList();
        var names = await db.Set<Patient>().AsNoTracking().Where(p => patientIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
        var facilityName = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == block.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        return new DowntimeNumberBlockDetail(
            Summary(block, rows.Count(n => n.Status == DowntimeUhidStatus.Entered), rows.Count(n => n.Status == DowntimeUhidStatus.Voided)),
            facilityName ?? string.Empty,
            [.. rows.Select(n => new DowntimeNumberDto(n.Position, n.Number, n.Status, n.EntityType, n.EntityId, n.PatientId,
                n.PatientId is { } p ? names.GetValueOrDefault(p) : null, n.PaperAt, n.EnteredAt, n.EnteredByName, n.LateGrantId is not null,
                n.VoidedAt, n.VoidReason, n.VoidedByName, n.Version))]);
    }

    public async Task<Result<DowntimeNumberBlockDetail>> IssueAsync(IssueDowntimeNumbersRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return FacilityRequired;
        }

        var source = SeriesOf(request.Kind);
        var scope = source.Series.PerFacility ? facilityId : (Guid?)null;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reserved = await numbers.ReserveAsync(source.Series, facilityId, request.Count, ct);

        // The period the reservation counted in, read in the same transaction (the row is locked by the reservation).
        var counted = await db.Set<NumberSeries>().AsNoTracking()
            .Where(s => s.Key == source.Series.Key && s.FacilityId == scope)
            .Select(s => new { s.ResetPolicy, s.CurrentPeriod })
            .FirstAsync(ct);
        var block = new DowntimeNumberBlock
        {
            FacilityId = facilityId,
            Kind = request.Kind,
            Counter = request.Counter.Trim(),
            Count = reserved.Count,
            FirstNumber = reserved[0],
            LastNumber = reserved[^1],
            IssuedByName = currentUser.DisplayName ?? currentUser.UserName,
            ResetPolicy = counted.ResetPolicy,
            Period = counted.CurrentPeriod,
        };
        db.Add(block);
        for (var i = 0; i < reserved.Count; i++)
        {
            db.Add(new DowntimeNumber { BlockId = block.Id, Position = i + 1, Number = reserved[i] });
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "patients.downtime_numbers_issued",
            $"Issued {reserved.Count} downtime {source.Name} numbers {block.FirstNumber} to {block.LastNumber} to \"{block.Counter}\"")
        {
            EntityType = nameof(DowntimeNumberBlock),
            EntityId = block.Id.ToString(),
            Details = new { kind = request.Kind.ToString(), block.Counter, count = reserved.Count, first = block.FirstNumber, last = block.LastNumber },
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(block.Id, ct);
    }

    public async Task<Result> VoidAsync(Guid blockId, VoidDowntimeNumberRequest request, CancellationToken ct)
    {
        var block = await db.Set<DowntimeNumberBlock>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == blockId && b.FacilityId == currentUser.FacilityId, ct);
        var wanted = request.Number.Trim().ToUpperInvariant();
        var number = block is null ? null : await db.Set<DowntimeNumber>().FirstOrDefaultAsync(n => n.BlockId == blockId && n.Number == wanted, ct);
        if (block is null || number is null)
        {
            return Error.NotFound(nameof(DowntimeNumber), wanted);
        }

        if (number.EntityId is not null)
        {
            return Error.Rule("downtime.number_entered", $"{wanted} was entered, so it is in use.");
        }

        if (number.VoidedAt is not null)
        {
            return Result.Success();
        }

        db.SetExpectedVersion(number, request.Version);
        Void(number, request.Reason.Trim());
        audit.Add(new AuditEntry(AuditCategories.Data, "patients.downtime_number_voided", $"Voided downtime number {wanted}: {number.VoidReason}")
        {
            EntityType = nameof(DowntimeNumberBlock),
            EntityId = blockId.ToString(),
            Details = new { number = wanted, reason = number.VoidReason },
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// The block comes back from the counter: every number not entered is voided together ("returned unused"), and the
    /// return, with how many were entered and voided, is audited. A returned block takes no more entries.
    /// </summary>
    public async Task<Result<DowntimeNumberBlockDetail>> ReturnAsync(Guid blockId, ReturnDowntimeBlockRequest request, CancellationToken ct)
    {
        var block = await db.Set<DowntimeNumberBlock>().FirstOrDefaultAsync(b => b.Id == blockId && b.FacilityId == currentUser.FacilityId, ct);
        if (block is null)
        {
            return Error.NotFound(nameof(DowntimeNumberBlock), blockId);
        }

        if (block.ReturnedAt is not null)
        {
            return Error.Rule("downtime.block_returned", $"This block was returned on {Local(block.ReturnedAt.Value)}.");
        }

        db.SetExpectedVersion(block, request.Version);
        var rows = await db.Set<DowntimeNumber>().Where(n => n.BlockId == blockId).ToListAsync(ct);
        var unused = rows.Where(n => n.Status == DowntimeUhidStatus.Open).ToList();
        foreach (var number in unused)
        {
            Void(number, "Returned unused");
        }

        block.ReturnedAt = time.GetUtcNow();
        block.ReturnedByName = currentUser.DisplayName ?? currentUser.UserName;
        block.ReturnNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var entered = rows.Count(n => n.Status == DowntimeUhidStatus.Entered);
        audit.Add(new AuditEntry(AuditCategories.Data, "patients.downtime_block_returned",
            $"Returned downtime block {block.FirstNumber} to {block.LastNumber} from \"{block.Counter}\": {entered} entered, {unused.Count} voided unused")
        {
            EntityType = nameof(DowntimeNumberBlock),
            EntityId = block.Id.ToString(),
            Details = new { entered, voidedUnused = unused.Count, voidedBefore = rows.Count(n => n.Status == DowntimeUhidStatus.Voided) - unused.Count, note = block.ReturnNote },
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(blockId, ct);
    }

    /// <summary>
    /// The downtime number a record entered from paper is made under, checked and marked entered for it (saved with the
    /// record by the caller). Refused when it is not a number HC360 issued for that kind at this facility; it was entered
    /// or voided; the paper is dated before the block was issued, in another period of the series, or out of order with
    /// the numbers already entered from the same block; or the paper is older than the back-entry window and no late
    /// back-entry approval covers it.
    /// </summary>
    public async Task<Result<DowntimeNumber>> ClaimAsync(
        DowntimeNumberKind kind, PaperNumber paper, string entityType, Guid entityId, Guid? patientId, CancellationToken ct)
    {
        var wanted = paper.Number.Trim().ToUpperInvariant();
        var found = await (
                from n in db.Set<DowntimeNumber>()
                join b in db.Set<DowntimeNumberBlock>() on n.BlockId equals b.Id
                where n.Number == wanted && b.Kind == kind && b.FacilityId == currentUser.FacilityId
                select new { Number = n, Block = b })
            .FirstOrDefaultAsync(ct);
        var source = SeriesOf(kind);
        if (found is null)
        {
            return Error.Field("paper.number", $"{wanted} is not a downtime {source.Name} number HC360 issued to this facility. Check the number on the paper.");
        }

        var (number, block) = (found.Number, found.Block);
        if (number.EntityId is not null)
        {
            return Error.Conflict("downtime.number_entered", $"{wanted} has already been entered. Two papers carry the same number, or this one was entered already.");
        }

        if (number.VoidedAt is not null)
        {
            return Error.Field("paper.number", $"{wanted} was voided ({number.VoidReason}) and cannot be used.");
        }

        if (paper.At < block.CreatedAt)
        {
            return Error.Field("paper.at", "The paper is dated before its number was issued. Check the date and time on the paper.");
        }

        if (block.Period is not null && NumberFormat.PeriodFor(block.ResetPolicy, NumberFormat.ToHospitalTime(paper.At)) != block.Period)
        {
            return Error.Field("paper.at",
                $"{wanted} belongs to the series' {PeriodWords(block.ResetPolicy)} {block.Period}, but the paper is dated in another. Check the date on the paper; a number of the wrong period cannot be entered.");
        }

        // Consumed in order: the counter writes the numbers one after the other, so an earlier number cannot carry a later
        // time than a later number already entered, nor the reverse.
        var earlierLater = await db.Set<DowntimeNumber>().AsNoTracking()
            .Where(n => n.BlockId == block.Id && n.EntityId != null && n.Position < number.Position && n.PaperAt > paper.At)
            .OrderBy(n => n.Position).Select(n => new { n.Number, n.PaperAt }).FirstOrDefaultAsync(ct);
        var laterEarlier = await db.Set<DowntimeNumber>().AsNoTracking()
            .Where(n => n.BlockId == block.Id && n.EntityId != null && n.Position > number.Position && n.PaperAt < paper.At)
            .OrderBy(n => n.Position).Select(n => new { n.Number, n.PaperAt }).FirstOrDefaultAsync(ct);
        if ((earlierLater ?? laterEarlier) is { } clash)
        {
            var before = earlierLater is not null;
            return Error.Field("paper.at",
                $"{wanted} is dated {Local(paper.At)}, {(before ? "before" : "after")} {clash.Number} ({Local(clash.PaperAt!.Value)}), which comes {(before ? "earlier" : "later")} in the block. Numbers are used in order: check the date and time on both papers.");
        }

        var allowed = await backEntry.CheckAsync(source.Record, paper.At, consume: true, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        number.EntityType = entityType;
        number.EntityId = entityId;
        number.PatientId = patientId;
        number.PaperAt = paper.At;
        number.EnteredAt = time.GetUtcNow();
        number.EnteredByName = currentUser.DisplayName ?? currentUser.UserName;
        number.LateGrantId = allowed.Value;
        audit.Add(new AuditEntry(AuditCategories.Data, "patients.entered_from_paper",
            $"Entered {source.Name} {wanted} from paper, dated {Local(paper.At)}{(allowed.Value is null ? string.Empty : " (late back-entry, approved)")}")
        {
            EntityType = entityType,
            EntityId = entityId.ToString(),
            PatientId = patientId,
            Severity = allowed.Value is null ? AuditSeverity.Info : AuditSeverity.Warning,
            Details = new { kind = kind.ToString(), number = wanted, paperAt = paper.At, lateGrantId = allowed.Value },
        });
        return number;
    }

    private void Void(DowntimeNumber number, string reason)
    {
        number.VoidedAt = time.GetUtcNow();
        number.VoidReason = reason;
        number.VoidedByName = currentUser.DisplayName ?? currentUser.UserName;
    }

    private DowntimeNumberBlockSummary Summary(DowntimeNumberBlock b, int entered, int voided) =>
        new(b.Id, b.Kind, SeriesOf(b.Kind).Name, b.Counter, b.Count, b.FirstNumber, b.LastNumber, b.CreatedAt, b.IssuedByName, entered, voided,
            b.Count - entered - voided, b.ReturnedAt, b.ReturnedByName, b.ReturnNote, b.Version);

    private static string PeriodWords(NumberResetPolicy policy) => policy switch
    {
        NumberResetPolicy.Yearly => "year",
        NumberResetPolicy.Monthly => "month",
        NumberResetPolicy.Daily => "day",
        _ => "period",
    };

    private static string Local(DateTimeOffset at) => NumberFormat.ToHospitalTime(at).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}
