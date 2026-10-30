using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;

/// <summary>/api/v1/pharmacy/queue — the e-prescription queue (PH-01, ERX-03): list, open, dispense, close, refills.</summary>
internal static class QueueEndpoints
{
    private static readonly string[] Readers = [PharmacyPermissions.QueueRead, PharmacyPermissions.Dispense, PharmacyPermissions.IpIssue];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pharmacy/queue").WithTags("Pharmacy");

        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapPost("/{id:guid}/dispense", async (Guid id, DispenseRequest r, DispensingService dispensing, CancellationToken ct) =>
                (await dispensing.DispenseAsync(id, r, ct)).ToCreated(s => $"/api/v1/pharmacy/sales/{s.Id}"))
            .RequirePermission(PharmacyPermissions.Dispense).WithValidation<DispenseRequest>();
        group.MapPost("/{id:guid}/close", Close)
            .RequireAnyPermission(PharmacyPermissions.Dispense, PharmacyPermissions.IpIssue).WithValidation<PharmacyReasonRequest>();
        group.MapPost("/{id:guid}/refills", async (Guid id, AuthorizeRefillsRequest r, RefillService refills, CancellationToken ct) =>
                (await refills.AuthorizeAsync(id, r.Refills, r.ValidUntil, r.Note, ct)).ToNoContent())
            .RequirePermission(PharmacyPermissions.Refills).WithValidation<AuthorizeRefillsRequest>();
    }

    /// <summary>Open prescriptions first-in-first-out; search by UHID, mobile, name or prescription number.</summary>
    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        QueueEntryStatus? status,
        bool? inpatient,
        bool? includeClosed,
        HospitalDbContext db,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var entries = db.Set<PharmacyQueueEntry>().AsNoTracking();
        if (status is not null)
        {
            entries = entries.Where(e => e.Status == status);
        }
        else if (includeClosed != true)
        {
            entries = entries.Where(e => e.Status == QueueEntryStatus.Pending || e.Status == QueueEntryStatus.PartiallyDispensed);
        }

        if (inpatient is not null)
        {
            entries = entries.Where(e => e.IsInpatient == inpatient);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            var patients = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            entries = entries.Where(e => e.PrescriptionNumber == upper || patients.Contains(e.PatientId));
        }

        var result = await (
                from e in entries
                join p in db.Set<Patient>() on e.PatientId equals p.Id
                orderby e.ReceivedAt
                select new
                {
                    e.Id,
                    e.PrescriptionNumber,
                    e.PatientId,
                    p.FullName,
                    p.Uhid,
                    p.Gender,
                    p.DateOfBirth,
                    Prescriber = db.Set<Practitioner>().Where(x => x.Id == e.PractitionerId).Select(x => x.DisplayName).FirstOrDefault(),
                    e.IsInpatient,
                    e.Status,
                    LineCount = e.Lines.Count,
                    OpenLines = e.Lines.Count(l => l.Status == QueueLineStatus.Pending || l.Status == QueueLineStatus.PartiallyDispensed),
                    e.HasControlledDrugs,
                    IsRefill = e.RefillOfEntryId != null,
                    e.SignedAt,
                    e.ReceivedAt,
                })
            .ToPagedResultAsync(page, ct);

        var now = time.GetUtcNow();
        var today = time.Today();
        return TypedResults.Ok(result.Map(e => new QueueListItem(
            e.Id, e.PrescriptionNumber, e.PatientId, e.FullName, e.Uhid, e.Gender, e.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            e.Prescriber, e.IsInpatient, e.Status, e.LineCount, e.OpenLines, e.HasControlledDrugs, e.IsRefill, e.SignedAt, e.ReceivedAt,
            (int)Math.Max((now - e.ReceivedAt).TotalMinutes, 0))));
    }

    /// <summary>Opens a prescription for dispensing, with the chosen store's stock per line (PHI read is audited).</summary>
    private static async Task<IResult> Get(
        Guid id,
        Guid? storeId,
        HospitalDbContext db,
        PatientAccessGuard guard,
        PatientContactAccess contacts,
        IAuditLogger audit,
        TimeProvider time,
        CancellationToken ct)
    {
        var entry = await db.Set<PharmacyQueueEntry>().AsNoTracking().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
        {
            return Error.NotFound(nameof(PharmacyQueueEntry), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(entry.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(entry.PatientId, $"Opened prescription {entry.PrescriptionNumber} in pharmacy", ct);
        var today = time.Today();
        var summaries = await PatientQueries.SummariesAsync(db, [entry.PatientId], today, await contacts.FullAsync(ct), ct);
        if (!summaries.TryGetValue(entry.PatientId, out var patient))
        {
            return Error.NotFound(nameof(Patient), entry.PatientId).ToProblem();
        }

        var prescriber = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.Id == entry.PractitionerId)
            .Select(p => new { p.DisplayName, p.RegistrationNumber })
            .FirstOrDefaultAsync(ct);
        var itemIds = entry.Lines.Where(l => l.DrugItemId != null).Select(l => l.DrugItemId!.Value).ToList();
        var stock = storeId is { } store && itemIds.Count > 0
            ? await InventoryQueries.AvailabilityAsync(db, store, itemIds, today, ct)
            : new Dictionary<Guid, StockAvailability>();

        return TypedResults.Ok(new QueueEntryDetail(
            entry.Id, entry.PrescriptionId, entry.PrescriptionNumber, patient, entry.VisitId, entry.AdmissionId, entry.PractitionerId,
            prescriber?.DisplayName, prescriber?.RegistrationNumber, entry.IsInpatient, entry.Status, entry.SignedAt, entry.ReceivedAt, entry.CloseReason,
            entry.RefillsAuthorized, entry.RefillsUsed, entry.RefillValidUntil, entry.RefillOfEntryId,
            entry.Lines.OrderBy(l => l.DrugName).Select(l =>
            {
                var available = l.DrugItemId is { } itemId ? stock.GetValueOrDefault(itemId) : null;
                return new QueueLineDto(
                    l.Id, l.PrescriptionLineId, l.DrugItemId, l.DrugName, l.GenericName, l.Strength, l.DosageForm, l.Route, l.Dose, l.Frequency,
                    l.DurationDays, l.Instructions, l.IsControlledDrug, l.RequestedQuantity, l.DispensedQuantity, l.RemainingQuantity, l.Status,
                    available?.Available, available?.CurrentMrp, available?.NearestExpiry);
            }).ToList(),
            entry.Version,
            await DispensingService.FreeTreatmentAsync(db, entry.VisitId, ct)));
    }

    private static async Task<IResult> Close(Guid id, PharmacyReasonRequest request, HospitalDbContext db, PharmacyRealtime realtime, TimeProvider time, CancellationToken ct)
    {
        var entry = await db.Set<PharmacyQueueEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
        {
            return Error.NotFound(nameof(PharmacyQueueEntry), id).ToProblem();
        }

        var closed = entry.Close(request.Reason.Trim(), time.GetUtcNow());
        if (closed.IsFailure)
        {
            return closed.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        await realtime.QueueChangedAsync(entry.FacilityId, entry.Id, entry.Status, ct);
        return TypedResults.NoContent();
    }
}
