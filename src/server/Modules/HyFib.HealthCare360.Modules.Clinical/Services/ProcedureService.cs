using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Procedures;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Structured procedure records (EHR-01): recorded and corrected on the chart, and recorded from operations the theatre
/// completes. Codes come from the procedure catalogue so a record never carries a code nobody can look up.
/// </summary>
public sealed class ProcedureService(HospitalDbContext db, ICurrentUser user, TimeProvider time)
{
    /// <summary>The patient's procedures from every facility, newest first; those entered in error only when asked for.</summary>
    public async Task<List<ProcedureDto>> ListAsync(Guid patientId, bool includeEnteredInError, CancellationToken ct)
    {
        var rows = Records().AsNoTracking().Where(p => p.PatientId == patientId);
        if (!includeEnteredInError)
        {
            rows = rows.Where(p => p.Status != ProcedureStatus.EnteredInError);
        }

        var list = await rows.OrderByDescending(p => p.PerformedAt).ToListAsync(ct);
        var names = await PrintQueries.PractitionerNamesAsync(db, list.Select(p => p.PerformerPractitionerId), ct);
        return list.Select(p => ToDto(p, names)).ToList();
    }

    public async Task<Result<ProcedureDto>> RecordAsync(Guid patientId, RecordProcedureRequest request, CancellationToken ct)
    {
        Encounter? encounter = null;
        if (request.EncounterId is { } encounterId)
        {
            encounter = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .FirstOrDefaultAsync(e => e.Id == encounterId, ct);
            if (encounter is null || encounter.PatientId != patientId)
            {
                return Error.Field("encounterId", "The encounter is not one of this patient's.");
            }
        }

        var details = await ResolveAsync(request, ct);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        var record = ProcedureRecord.Record(patientId, details.Value!, user.UserId);
        record.EncounterId = encounter?.Id;
        record.AdmissionId = encounter?.AdmissionId;
        record.VisitId = encounter?.VisitId;
        db.Add(record);
        await db.SaveChangesAsync(ct);
        return await DtoAsync(record, ct);
    }

    public async Task<Result<ProcedureDto>> CorrectAsync(ProcedureRecord record, CorrectProcedureRequest request, CancellationToken ct)
    {
        var details = await ResolveAsync(request, ct);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        db.SetExpectedVersion(record, request.Version);
        var corrected = record.Correct(details.Value!, request.Reason, user.UserId, time.GetUtcNow());
        if (corrected.IsFailure)
        {
            return corrected.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(record, ct);
    }

    public async Task<Result<ProcedureDto>> MarkEnteredInErrorAsync(ProcedureRecord record, string reason, CancellationToken ct)
    {
        var result = record.MarkEnteredInError(reason, user.UserId, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(record, ct);
    }

    /// <summary>The record by id, from any facility: the record is longitudinal, and the caller checks the patient's access.</summary>
    public Task<ProcedureRecord?> FindAsync(Guid id, CancellationToken ct) => Records().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>
    /// Records the operation a theatre booking completed, once per booking, in the encounter of its admission (or visit)
    /// when there is one. The booking's code is kept only when the procedure catalogue has it: theatres often book with
    /// the hospital's own tariff code, which is no clinical code.
    /// </summary>
    public async Task RecordSurgeryAsync(SurgeryCompleted surgery, CancellationToken ct)
    {
        if (await Records().IgnoreQueryFilters().AnyAsync(p => p.SurgeryBookingId == surgery.SurgeryBookingId, ct))
        {
            return;
        }

        var catalogued = await CatalogueAsync(surgery.ProcedureCode, ct);
        var encounters = db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.PatientId == surgery.PatientId && e.StartedAt <= surgery.CompletedAt);
        var encounter = surgery.AdmissionId is { } admissionId
            ? await encounters.Where(e => e.AdmissionId == admissionId).OrderByDescending(e => e.StartedAt).FirstOrDefaultAsync(ct)
            : surgery.VisitId is { } visitId
                ? await encounters.Where(e => e.VisitId == visitId).OrderByDescending(e => e.StartedAt).FirstOrDefaultAsync(ct)
                : null;

        var record = ProcedureRecord.FromSurgery(surgery.PatientId, surgery.SurgeryBookingId, new ProcedureDetails(
            surgery.StartedAt ?? surgery.CompletedAt, catalogued?.Code, catalogued?.System, surgery.ProcedureName, surgery.SurgeonId, null, surgery.Side,
            null, null, $"Operation completed in theatre (booking {surgery.BookingNumber})."));
        record.FacilityId = surgery.FacilityId;
        record.EncounterId = encounter?.Id;
        record.AdmissionId = surgery.AdmissionId;
        record.VisitId = surgery.VisitId;
        db.Add(record);
    }

    private IQueryable<ProcedureRecord> Records() =>
        db.Set<ProcedureRecord>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);

    /// <summary>
    /// The details to store: the catalogue's code and title (the title unless the doctor described it otherwise) and,
    /// without a named performer, the recording doctor.
    /// </summary>
    private async Task<Result<ProcedureDetails>> ResolveAsync(IProcedureFields request, CancellationToken ct)
    {
        string? code = null;
        string? system = null;
        var display = request.Display?.Trim();
        if (!string.IsNullOrWhiteSpace(request.Code))
        {
            var catalogued = await CatalogueAsync(request.Code, ct);
            if (catalogued is null)
            {
                return Error.Field("code", "This code is not in the procedure catalogue. Choose one from the list, or describe the procedure without a code.");
            }

            (code, system) = (catalogued.Code, catalogued.System);
            display = string.IsNullOrWhiteSpace(display) ? catalogued.Title : display;
        }

        var performerName = request.PerformerName?.Trim();
        var performer = request.PerformerPractitionerId;
        if (performer is { } practitionerId)
        {
            if (!await db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId, ct))
            {
                return Error.Field("performerPractitionerId", "Choose the practitioner who did the procedure.");
            }
        }
        else if (string.IsNullOrWhiteSpace(performerName))
        {
            performer = user.PractitionerId;
        }

        return new ProcedureDetails(request.PerformedAt, code, system, display!, performer, performerName, request.Side, request.BodySite, request.Outcome,
            request.Notes);
    }

    private async Task<ProcedureCode?> CatalogueAsync(string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var wanted = code.Trim().ToUpperInvariant();
        return await db.Set<ProcedureCode>().AsNoTracking().FirstOrDefaultAsync(c => c.Code == wanted && c.IsActive, ct);
    }

    private async Task<ProcedureDto> DtoAsync(ProcedureRecord record, CancellationToken ct) =>
        ToDto(record, await PrintQueries.PractitionerNamesAsync(db, [record.PerformerPractitionerId], ct));

    private static ProcedureDto ToDto(ProcedureRecord p, IReadOnlyDictionary<Guid, string> practitioners) => new(
        p.Id, p.PatientId, p.EncounterId, p.AdmissionId, p.PerformedAt, p.Code, p.CodeSystem, p.Display, p.PerformerPractitionerId,
        p.PerformerPractitionerId is { } id && practitioners.TryGetValue(id, out var name) ? name : p.PerformerName,
        p.Side, p.BodySite, p.Outcome, p.Notes, p.Source, p.Status, p.StatusReason, p.StatusChangedAt, p.Version);
}
