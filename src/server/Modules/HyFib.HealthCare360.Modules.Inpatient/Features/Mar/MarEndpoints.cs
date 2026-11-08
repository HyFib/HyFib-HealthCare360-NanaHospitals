using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Mar;

/// <summary>
/// /api/v1/nursing — medication chart (orders) and Medication Administration Record, plus the ward's due-dose worklist, the
/// doctor's countersignature of verbal and telephone orders and of nurses' stops, and the list of what waits for one.
/// </summary>
internal static class MarEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/nursing").WithTags("Medication administration");

        group.MapGet("/admissions/{admissionId:guid}/medication-orders", ListOrders)
            .RequireAnyPermission(InpatientPermissions.NursingMar, InpatientPermissions.NursingChart);
        group.MapPost("/admissions/{admissionId:guid}/medication-orders", AddOrder)
            .RequirePermission(InpatientPermissions.NursingMar).WithValidation<AddMedicationOrderRequest>();
        group.MapPost("/medication-orders/{id:guid}/stop", StopOrder)
            .RequirePermission(InpatientPermissions.NursingMar).WithValidation<StopMedicationOrderRequest>();
        group.MapPost("/medication-orders/{id:guid}/schedule", ExtendSchedule).RequirePermission(InpatientPermissions.NursingMar);
        group.MapPost("/medication-orders/{id:guid}/countersign", Countersign)
            .RequirePermission(ClinicalPermissions.PrescriptionsSign).WithValidation<CountersignMedicationOrderRequest>();
        group.MapPost("/medication-orders/{id:guid}/stop/countersign", ConfirmStop)
            .RequirePermission(ClinicalPermissions.PrescriptionsSign).WithValidation<ConfirmStopRequest>();
        group.MapPost("/medication-orders/{id:guid}/stop/decline", DeclineStop)
            .RequirePermission(ClinicalPermissions.PrescriptionsSign).WithValidation<DeclineStopRequest>();
        group.MapGet("/medication-orders/awaiting-countersignature",
                async (Guid? wardId, bool? mine, bool? overdueOnly, MarService mar, CancellationToken ct) =>
                    TypedResults.Ok(await mar.AwaitingCountersignatureAsync(wardId, mine == true, overdueOnly == true, ct)))
            .RequireAnyPermission(InpatientPermissions.NursingMar, InpatientPermissions.NursingInCharge);
        group.MapGet("/drugs", SearchDrugs).RequirePermission(InpatientPermissions.NursingMar);

        group.MapGet("/admissions/{admissionId:guid}/mar", Chart).RequireAnyPermission(InpatientPermissions.NursingMar, InpatientPermissions.NursingChart);
        group.MapPost("/admissions/{admissionId:guid}/mar", AddAdHoc)
            .RequirePermission(InpatientPermissions.NursingMar).WithValidation<AddAdHocDoseRequest>();
        group.MapPost("/mar/{id:guid}/record", Record).RequirePermission(InpatientPermissions.NursingMar).WithValidation<RecordAdministrationRequest>();
        group.MapGet("/wards/{wardId:guid}/mar/due", async (Guid wardId, int? withinMinutes, MarService mar, CancellationToken ct) =>
                TypedResults.Ok(await mar.WardDueAsync(wardId, Math.Clamp(withinMinutes ?? 60, 0, 12 * 60), ct)))
            .RequirePermission(InpatientPermissions.NursingMar);
    }

    private static async Task<IResult> ListOrders(Guid admissionId, bool? activeOnly, AdmissionAccess admissions, HospitalDbContext db, MarService mar, CancellationToken ct)
    {
        var access = await admissions.OpenForReadAsync(admissionId, "Viewed medication chart", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var orders = db.Set<MedicationOrder>().AsNoTracking().Where(o => o.AdmissionId == admissionId);
        if (activeOnly == true)
        {
            orders = orders.Where(o => o.Status == MedicationOrderStatus.Active);
        }

        var rows = await orders.OrderByDescending(o => o.StartAt).Take(500).ToListAsync(ct);
        return TypedResults.Ok(await mar.ToDtosAsync(rows, ct));
    }

    private static async Task<IResult> AddOrder(Guid admissionId, AddMedicationOrderRequest request, AdmissionAccess admissions, MarService mar, CancellationToken ct)
    {
        var access = await admissions.OpenForChartingAsync(admissionId, ct);
        return access.IsFailure
            ? access.Error!.ToProblem()
            : (await mar.AddOrderAsync(access.Value, request, ct)).ToCreated(o => $"/api/v1/nursing/admissions/{admissionId}/medication-orders");
    }

    private static async Task<IResult> StopOrder(Guid id, StopMedicationOrderRequest request, HospitalDbContext db, AdmissionAccess admissions, MarService mar, CancellationToken ct)
    {
        var access = await OrderAccessAsync(id, db, admissions, ct);
        return access ?? (await mar.StopOrderAsync(id, request.Reason, ct)).ToOk();
    }

    private static async Task<IResult> ExtendSchedule(Guid id, HospitalDbContext db, AdmissionAccess admissions, MarService mar, CancellationToken ct)
    {
        var access = await OrderAccessAsync(id, db, admissions, ct);
        return access ?? (await mar.ExtendScheduleAsync(id, ct)).ToOk();
    }

    // The doctor countersigns after the chart has closed too (a verbal order given the night before a discharge): the
    // record still needs it. Reading the stay is checked and audited as any opening of it is.
    private static async Task<IResult> Countersign(Guid id, CountersignMedicationOrderRequest request, HospitalDbContext db, AdmissionAccess admissions, MarService mar,
        CancellationToken ct)
    {
        var access = await OrderAccessAsync(id, db, admissions, ct, "Countersigned a medication order");
        return access ?? (await mar.CountersignAsync(id, request.Version, ct)).ToOk();
    }

    private static async Task<IResult> ConfirmStop(Guid id, ConfirmStopRequest request, HospitalDbContext db, AdmissionAccess admissions, MarService mar, CancellationToken ct)
    {
        var access = await OrderAccessAsync(id, db, admissions, ct, "Countersigned the stop of a medication order");
        return access ?? (await mar.ConfirmStopAsync(id, request.Version, ct)).ToOk();
    }

    private static async Task<IResult> DeclineStop(Guid id, DeclineStopRequest request, HospitalDbContext db, AdmissionAccess admissions, MarService mar, CancellationToken ct)
    {
        var access = await OrderAccessAsync(id, db, admissions, ct, "Declined the stop of a medication order");
        return access ?? (await mar.DeclineStopAsync(id, request.Reason, request.Version, ct)).ToOk();
    }

    /// <summary>
    /// Drugs of the master for the ward's order form, by code, barcode, name, generic or brand (at most 20): picked from
    /// here, an order takes the drug's controlled and high-alert flags from the master.
    /// </summary>
    private static async Task<IResult> SearchDrugs(string? search, HospitalDbContext db, CancellationToken ct)
    {
        var drugs = await db.Set<Item>().AsNoTracking()
            .Where(i => i.IsActive && i.Type == ItemType.Drug)
            .Search(search)
            .OrderBy(i => i.Name)
            .ThenBy(i => i.Code)
            .Take(20)
            .Select(i => new { i.Id, i.Code, i.Name, i.GenericName, i.Strength, i.DosageForm, i.Schedule, i.IsControlled, i.IsHighAlert })
            .ToListAsync(ct);
        return TypedResults.Ok(drugs.Select(d => new WardDrugDto(d.Id, d.Code, d.Name, d.GenericName, d.Strength, d.DosageForm,
            Item.NeedsRegister(d.Schedule, d.IsControlled), d.IsHighAlert)).ToList());
    }

    /// <summary>The day's MAR (IST calendar day) plus any older doses still waiting to be documented.</summary>
    private static async Task<IResult> Chart(Guid admissionId, DateOnly? date, AdmissionAccess admissions, HospitalDbContext db, MarService mar, TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForReadAsync(admissionId, "Viewed medication administration record", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var day = date ?? time.Today();
        var from = IndiaClock.StartOfDayUtc(day);
        var to = IndiaClock.StartOfDayUtc(day.AddDays(1));
        var entries = await db.Set<MedicationAdministration>().AsNoTracking()
            .Where(m => m.AdmissionId == admissionId
                        && ((m.ScheduledAt >= from && m.ScheduledAt < to) || (m.Status == MarStatus.Due && m.ScheduledAt < from)))
            .OrderBy(m => m.ScheduledAt)
            .Take(500)
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        var grace = await mar.GraceAsync(access.Value.FacilityId, ct);
        var orderIds = entries.Where(m => m.MedicationOrderId is not null).Select(m => m.MedicationOrderId!.Value).Distinct().ToList();
        var orders = await db.Set<MedicationOrder>().AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        return TypedResults.Ok(entries.Select(m => MarService.ToDto(m, now, grace, m.MedicationOrderId is { } id ? orders.GetValueOrDefault(id) : null)).ToList());
    }

    private static async Task<IResult> AddAdHoc(Guid admissionId, AddAdHocDoseRequest request, AdmissionAccess admissions, MarService mar, CancellationToken ct)
    {
        var access = await admissions.OpenForChartingAsync(admissionId, ct);
        return access.IsFailure
            ? access.Error!.ToProblem()
            : (await mar.AddAdHocAsync(access.Value, request, ct)).ToCreated(m => $"/api/v1/nursing/admissions/{admissionId}/mar");
    }

    private static async Task<IResult> Record(Guid id, RecordAdministrationRequest request, HospitalDbContext db, AdmissionAccess admissions, MarService mar, CancellationToken ct)
    {
        var admissionId = await db.Set<MedicationAdministration>().Where(m => m.Id == id).Select(m => (Guid?)m.AdmissionId).FirstOrDefaultAsync(ct);
        if (admissionId is null)
        {
            return Error.NotFound("Medication dose", id).ToProblem();
        }

        var access = await admissions.OpenForChartingAsync(admissionId.Value, ct);
        return access.IsFailure ? access.Error!.ToProblem() : (await mar.RecordAsync(id, request, ct)).ToOk();
    }

    /// <summary>
    /// Checks restricted-record rules and that the chart is open for the order's admission (or, given <paramref name="read"/>,
    /// only that the stay may be opened, audited as that); null when allowed.
    /// </summary>
    private static async Task<IResult?> OrderAccessAsync(Guid orderId, HospitalDbContext db, AdmissionAccess admissions, CancellationToken ct, string? read = null)
    {
        var admissionId = await db.Set<MedicationOrder>().Where(o => o.Id == orderId).Select(o => (Guid?)o.AdmissionId).FirstOrDefaultAsync(ct);
        if (admissionId is null)
        {
            return Error.NotFound("Medication order", orderId).ToProblem();
        }

        var access = read is null ? await admissions.OpenForChartingAsync(admissionId.Value, ct) : await admissions.OpenForReadAsync(admissionId.Value, read, ct);
        return access.IsFailure ? access.Error!.ToProblem() : null;
    }
}
