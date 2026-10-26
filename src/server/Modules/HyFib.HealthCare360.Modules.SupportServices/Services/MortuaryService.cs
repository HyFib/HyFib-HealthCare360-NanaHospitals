using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Mortuary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Mortuary;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// The mortuary: its cold-storage slots, the register of bodies received and released, and the checks on each. A patient
/// who died in the hospital comes in only once their death is on record (certified, or the stay discharged as expired),
/// and is medico-legal if their last visit was; a body leaves only to someone named, on the authority its case needs.
/// </summary>
internal sealed class MortuaryService(HospitalDbContext db, INumberSeries numbers, ICurrentUser currentUser, TimeProvider time)
{
    public Task<BodyRecord?> FindAsync(Guid id, CancellationToken ct) => db.Set<BodyRecord>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<MortuarySlotDto>> SlotsAsync(CancellationToken ct)
    {
        var slots = await db.Set<MortuarySlot>().AsNoTracking().OrderBy(s => s.Chamber).ThenBy(s => s.Code).ToListAsync(ct);
        var bodies = await db.Set<BodyRecord>().AsNoTracking().Where(b => b.Status == BodyStatus.InStorage && b.SlotId != null)
            .ToDictionaryAsync(b => b.SlotId!.Value, ct);
        var now = time.GetUtcNow();
        return slots.Select(s =>
        {
            var body = bodies.GetValueOrDefault(s.Id);
            return new MortuarySlotDto(s.Id, s.Code, s.Chamber, s.IsActive, body?.Id, body?.Number, body?.Name, body?.ReceivedAt, body?.Unclaimed(now) ?? false, s.Version);
        }).ToList();
    }

    public async Task<Result<IReadOnlyList<MortuarySlotDto>>> SaveSlotsAsync(SaveSlotsRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var slots = await db.Set<MortuarySlot>().ToListAsync(ct);
        var occupied = await db.Set<BodyRecord>().Where(b => b.Status == BodyStatus.InStorage && b.SlotId != null).Select(b => b.SlotId!.Value)
            .ToListAsync(ct);
        foreach (var input in request.Slots)
        {
            var slot = input.Id is { } id ? slots.FirstOrDefault(s => s.Id == id) : null;
            if (input.Id is not null && slot is null)
            {
                return Error.NotFound("Mortuary slot", input.Id);
            }

            if (slot is null)
            {
                slot = new MortuarySlot { FacilityId = facility };
                db.Add(slot);
                slots.Add(slot);
            }
            else
            {
                db.SetExpectedVersion(slot, input.Version!.Value);
            }

            if (!input.IsActive && occupied.Contains(slot.Id))
            {
                return Error.Rule("mortuary.slot_occupied", $"Slot {slot.Code} holds a body; release or move it before taking the slot out of use.");
            }

            slot.Code = input.Code.Trim().ToUpperInvariant();
            slot.Chamber = string.IsNullOrWhiteSpace(input.Chamber) ? null : input.Chamber.Trim();
            slot.IsActive = input.IsActive;
        }

        if (slots.GroupBy(s => s.Code).Any(g => g.Count() > 1))
        {
            return Error.Field("slots", "Each slot needs its own label.");
        }

        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<MortuarySlotDto>>.Ok(await SlotsAsync(ct));
    }

    public async Task<Result<BodyDto>> ReceiveAsync(ReceiveBodyRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var slot = await db.Set<MortuarySlot>().FirstOrDefaultAsync(s => s.Id == request.SlotId, ct);
        if (slot is not { IsActive: true })
        {
            return Error.Field("slotId", "Choose a slot in use.");
        }

        if (await db.Set<BodyRecord>().AnyAsync(b => b.SlotId == slot.Id && b.Status == BodyStatus.InStorage, ct))
        {
            return Error.Conflict("mortuary.slot_occupied", $"Slot {slot.Code} already holds a body.");
        }

        var body = new BodyRecord
        {
            FacilityId = facility,
            Source = request.Source,
            ReceivedAt = time.GetUtcNow(),
            ReceivedByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
            BroughtBy = Trim(request.BroughtBy),
            IsMlc = request.IsMlc,
            MlcNumber = Trim(request.MlcNumber),
            PoliceStation = Trim(request.PoliceStation),
            IdentificationMarks = Trim(request.IdentificationMarks),
            Belongings = Trim(request.Belongings),
        };

        if (request.PatientId is { } patientId)
        {
            var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
            if (patient is null)
            {
                return Error.NotFound(nameof(Patient), patientId);
            }

            if (request.Source == BodySource.InHospitalDeath && !patient.IsDeceased)
            {
                return Error.Rule("mortuary.death_not_recorded",
                    "The patient's death is not on record. Certify the cause of death, or discharge the stay as expired, first.");
            }

            if (await db.Set<BodyRecord>().AnyAsync(b => b.PatientId == patientId && b.Status == BodyStatus.InStorage, ct))
            {
                return Error.Conflict("mortuary.already_in", "This patient's body is already in the mortuary.");
            }

            body.PatientId = patientId;
            body.Name = patient.FullName;
            body.Sex = patient.Gender.ToString();
            body.DiedAt = patient.DeceasedAt ?? request.DiedAt;
            body.AgeYears = patient.DateOfBirth is { } dob && body.DiedAt is { } died ? AgeAt(dob, DateOnly.FromDateTime(died.UtcDateTime)) : request.AgeYears;

            // A death after a medico-legal visit or stay is a medico-legal death.
            var lastVisit = await db.Set<Visit>().AsNoTracking().Where(v => v.PatientId == patientId).OrderByDescending(v => v.VisitedAt)
                .Select(v => new { v.IsMlc, v.MlcNumber }).FirstOrDefaultAsync(ct);
            if (lastVisit is { IsMlc: true })
            {
                body.IsMlc = true;
                body.MlcNumber ??= lastVisit.MlcNumber;
            }
        }
        else
        {
            body.Name = request.Name!.Trim();
            body.Identified = request.Identified;
            body.Sex = Trim(request.Sex);
            body.AgeYears = request.AgeYears;
            body.DiedAt = request.DiedAt;
        }

        if (body.IsMlc && body.PoliceStation is null)
        {
            return Error.Field("policeStation", "Name the police station of a medico-legal body.");
        }

        body.Number = await numbers.NextAsync(SupportServicesNumberSeries.MortuaryBody, facility, ct);
        body.Store(slot);
        db.Add(body);
        await db.SaveChangesAsync(ct);
        return await DtoAsync(body, ct);
    }

    public async Task<Result<BodyDto>> ReleaseAsync(Guid id, ReleaseBodyRequest request, CancellationToken ct)
    {
        var body = await db.Set<BodyRecord>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (body is null)
        {
            return Error.NotFound("Body", id);
        }

        db.SetExpectedVersion(body, request.Version);
        var released = body.Release(
            new BodyRelease(request.To, request.ReceivedBy, request.Relationship, request.IdProof, request.PoliceClearance, request.BelongingsHandedOver,
                request.Notes),
            currentUser.DisplayName ?? currentUser.UserName ?? "Staff", time.GetUtcNow());
        if (released.IsFailure)
        {
            return released.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(body, ct);
    }

    /// <summary>The register: bodies in storage, or every body received from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public async Task<IReadOnlyList<BodyDto>> RegisterAsync(BodyStatus? status, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var query = db.Set<BodyRecord>().AsNoTracking();
        if (status == BodyStatus.InStorage)
        {
            query = query.Where(b => b.Status == BodyStatus.InStorage);
        }
        else
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
            query = query.Where(b => b.ReceivedAt >= start && b.ReceivedAt < end);
            if (status is not null)
            {
                query = query.Where(b => b.Status == status);
            }
        }

        var bodies = await query.OrderByDescending(b => b.ReceivedAt).Take(500).ToListAsync(ct);
        return await DtosAsync(bodies, ct);
    }

    public async Task<BodyDto> DtoAsync(BodyRecord body, CancellationToken ct) => (await DtosAsync([body], ct))[0];

    private async Task<List<BodyDto>> DtosAsync(IReadOnlyCollection<BodyRecord> bodies, CancellationToken ct)
    {
        var ids = bodies.Where(b => b.PatientId is not null).Select(b => b.PatientId!.Value).Distinct().ToList();
        var uhids = await db.Set<Patient>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Uhid, ct);
        var now = time.GetUtcNow();
        return bodies.Select(b => new BodyDto(
            b.Id, b.Number, b.PatientId, b.PatientId is { } p ? uhids.GetValueOrDefault(p) : null, b.Source, b.Name, b.Identified, b.Sex, b.AgeYears, b.DiedAt,
            b.ReceivedAt, b.ReceivedByName, b.BroughtBy, b.IsMlc, b.MlcNumber, b.PoliceStation, b.IdentificationMarks, b.Belongings, b.SlotCode, b.Status,
            b.Unclaimed(now), b.ReleasedAt, b.ReleasedTo, b.ReceivedBy, b.Relationship, b.IdProof, b.PoliceClearance, b.BelongingsHandedOver, b.ReleaseNotes,
            b.ReleasedByName, b.Version)).ToList();
    }

    private static int AgeAt(DateOnly dob, DateOnly on) => on.Year - dob.Year - (on < dob.AddYears(on.Year - dob.Year) ? 1 : 0);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
