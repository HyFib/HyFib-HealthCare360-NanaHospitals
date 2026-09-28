using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>One line of an <see cref="OrderDraft"/>: a catalogue service, optional notes and units/sessions requested.</summary>
public sealed record OrderLine(ServiceItem Service, string? Notes, int Quantity = 1);

/// <summary>
/// What to order; lines are already-validated catalogue services. <see cref="PackageBookingId"/> is set (only) for
/// health-checkup orders and travels on every <see cref="ServiceOrderPlaced"/>, so Billing charges the package once.
/// </summary>
public sealed record OrderDraft(
    Guid FacilityId,
    Guid PatientId,
    Guid? EncounterId,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid? PractitionerId,
    OrderPriority Priority,
    string? ClinicalNotes,
    OrderSource Source,
    Guid? PackageBookingId,
    IReadOnlyList<OrderLine> Lines);

/// <summary>
/// Clinical orders (EMR-05): validation against the service catalogue, duplicate-order protection, gap-free order
/// numbers ("{FAC}OR{YY}{SEQ:7}") and publication of <see cref="ServiceOrderPlaced"/>/<see cref="ServiceOrderCancelled"/>
/// to the performing departments in the same transaction.
/// </summary>
public sealed class ClinicalOrderService(
    HospitalDbContext db,
    INumberSeries numbers,
    ClinicalAccess access,
    ICurrentUser currentUser,
    IRealtimePublisher realtime,
    TimeProvider time)
{
    public const string Series = "clinical-order";
    public const string Format = "{FAC}OR{YY}{SEQ:7}";

    /// <summary>Services a clinician may order; beds, nursing, registration and packages are billed by other flows.</summary>
    public static readonly IReadOnlySet<ServiceCategory> OrderableCategories = new HashSet<ServiceCategory>
    {
        ServiceCategory.Laboratory, ServiceCategory.Radiology, ServiceCategory.Cardiology, ServiceCategory.Procedure, ServiceCategory.Surgery,
        ServiceCategory.Physiotherapy, ServiceCategory.Dialysis, ServiceCategory.Dietary, ServiceCategory.BloodBank, ServiceCategory.Consultation,
    };

    public async Task<Result<OrderDto>> PlaceAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var allowed = await access.EnsureCanRecordAsync(request.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var visitId = request.VisitId;
        var admissionId = request.AdmissionId;
        if (request.EncounterId is { } encounterId)
        {
            var encounter = await db.Set<Encounter>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == encounterId, ct);
            if (encounter is null || encounter.PatientId != request.PatientId)
            {
                return Error.Field("encounterId", "This encounter does not belong to the patient.");
            }

            visitId ??= encounter.VisitId;
            admissionId ??= encounter.AdmissionId;
        }

        if (visitId is { } vid && !await db.Set<Visit>().AnyAsync(v => v.Id == vid && v.PatientId == request.PatientId, ct))
        {
            return Error.Field("visitId", "This visit does not belong to the patient.");
        }

        var lines = await ResolveLinesAsync(request, ct);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        if (!request.ConfirmDuplicates)
        {
            var duplicates = await FindPendingDuplicatesAsync(request.PatientId, visitId, admissionId, lines.Value.Select(l => l.Service.Id).ToList(), ct);
            if (duplicates.Count > 0)
            {
                return Error.Conflict("order.duplicate", "Some of these services are already ordered and still pending. Confirm to order them again.", duplicates);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var order = await CreateAsync(new OrderDraft(facilityId, request.PatientId, request.EncounterId, visitId, admissionId,
            currentUser.PractitionerId, request.Priority, request.ClinicalNotes?.Trim(), request.EncounterId is null ? OrderSource.Direct : OrderSource.Encounter,
            null, lines.Value), ct);
        if (order.IsFailure)
        {
            return order.Error!;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.ToFacilityAsync(facilityId, "clinical.order.placed", new { order.Value.Id, order.Value.PatientId }, ct);
        return await ToDtoAsync(order.Value, ct);
    }

    /// <summary>Allocates the number, adds the lines and raises the events. The caller saves inside its own transaction.</summary>
    public async Task<Result<ClinicalOrder>> CreateAsync(OrderDraft draft, CancellationToken ct)
    {
        var order = new ClinicalOrder
        {
            FacilityId = draft.FacilityId,
            OrderNumber = await numbers.NextAsync(Series, draft.FacilityId, Format, NumberResetPolicy.Yearly, ct),
            PatientId = draft.PatientId,
            EncounterId = draft.EncounterId,
            VisitId = draft.VisitId,
            AdmissionId = draft.AdmissionId,
            OrderingPractitionerId = draft.PractitionerId,
            OrderedByUserId = currentUser.UserId,
            Priority = draft.Priority,
            Source = draft.Source,
            PackageBookingId = draft.PackageBookingId,
            ClinicalNotes = draft.ClinicalNotes,
        };

        foreach (var line in draft.Lines)
        {
            var added = order.AddItem(line.Service, line.Notes, line.Quantity);
            if (added.IsFailure)
            {
                return added.Error!;
            }
        }

        var placed = order.Place(time.GetUtcNow());
        if (placed.IsFailure)
        {
            return placed.Error!;
        }

        db.Add(order);
        return order;
    }

    public Task<Result<OrderDto>> CancelAsync(Guid orderId, string reason, CancellationToken ct) =>
        ChangeAsync(o => o.Id == orderId, order => order.Cancel(reason, time.GetUtcNow()), ct);

    public Task<Result<OrderDto>> CancelItemAsync(Guid itemId, string reason, CancellationToken ct) =>
        ChangeAsync(o => o.Items.Any(i => i.Id == itemId), order => order.CancelItem(itemId, reason, time.GetUtcNow()), ct);

    public Task<Result<OrderDto>> UpdateItemStatusAsync(Guid itemId, OrderItemStatus status, string? reference, CancellationToken ct) =>
        ChangeAsync(o => o.Items.Any(i => i.Id == itemId), order => order.UpdateItemStatus(itemId, status, time.GetUtcNow(), reference), ct);

    public async Task<OrderDto> ToDtoAsync(ClinicalOrder o, CancellationToken ct)
    {
        var names = await PrintQueries.PractitionerNamesAsync(db, [o.OrderingPractitionerId], ct);
        return ToDto(o, o.OrderingPractitionerId is { } p ? names.GetValueOrDefault(p) : null);
    }

    public static OrderDto ToDto(ClinicalOrder o, string? practitionerName) => new(
        o.Id, o.OrderNumber, o.PatientId, o.EncounterId, o.VisitId, o.AdmissionId, o.OrderingPractitionerId, practitionerName, o.Priority, o.Status,
        o.Source, o.OrderedAt, o.ClinicalNotes,
        o.Items.Select(i => new OrderItemDto(i.Id, i.ServiceItemId, i.ServiceCode, i.ServiceName, i.Category, i.Quantity, i.Status, i.Notes,
            i.StatusChangedAt, i.CompletedAt, i.ResultReference, i.CancelReason)).ToList());

    private async Task<Result<IReadOnlyList<OrderLine>>> ResolveLinesAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        var picked = request.Items ?? [];
        var fromSet = new List<Guid>();
        if (request.OrderSetId is { } orderSetId)
        {
            var set = await db.Set<OrderSet>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == orderSetId && s.IsActive, ct);
            if (set is null)
            {
                return Error.Field("orderSetId", "Choose an active order set.");
            }

            fromSet.AddRange(set.ServiceItemIds);
        }

        var ids = picked.Select(p => p.ServiceItemId).Concat(fromSet).Distinct().ToList();
        var services = await db.Set<ServiceItem>().AsNoTracking().Where(s => ids.Contains(s.Id) && s.IsActive).ToDictionaryAsync(s => s.Id, ct);
        if (services.Count != ids.Count)
        {
            return Error.Field("items", "One or more services are unknown or no longer offered.");
        }

        if (services.Values.FirstOrDefault(s => !OrderableCategories.Contains(s.Category)) is { } notOrderable)
        {
            return Error.Field("items", $"{notOrderable.Name} ({notOrderable.Category}) cannot be ordered from the EMR.");
        }

        // Keeping only one of two picked lines would silently drop the other's notes or units; ask for a single line.
        if (picked.GroupBy(p => p.ServiceItemId).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            var service = services[repeated.Key];
            return Error.Field("items", OrderQuantity.MaxFor(service.Category) > 1
                ? $"{service.Name} is listed more than once; order it on one line and set the quantity."
                : $"{service.Name} is listed more than once; order it on one line.");
        }

        var lines = new List<OrderLine>();
        foreach (var line in picked)
        {
            var service = services[line.ServiceItemId];
            var quantity = line.Quantity ?? 1;
            var allowed = OrderQuantity.Check(service, quantity);
            if (allowed.IsFailure)
            {
                return Error.Field("items", allowed.Error!.Message);
            }

            lines.Add(new OrderLine(service, line.Notes?.Trim(), quantity));
        }

        // An order set contributes each service once; a service the clinician also picked keeps the clinician's line.
        lines.AddRange(fromSet.Distinct().Where(id => lines.All(l => l.Service.Id != id)).Select(id => new OrderLine(services[id], null)));

        // Explicit Ok: user-defined conversions do not apply when the value type is an interface.
        return Result<IReadOnlyList<OrderLine>>.Ok(lines);
    }

    private async Task<List<DuplicateOrderLine>> FindPendingDuplicatesAsync(Guid patientId, Guid? visitId, Guid? admissionId, List<Guid> serviceIds, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddHours(-24);
        var orders = db.Set<ClinicalOrder>().AsNoTracking().Where(o => o.PatientId == patientId);
        orders = visitId is not null ? orders.Where(o => o.VisitId == visitId)
            : admissionId is not null ? orders.Where(o => o.AdmissionId == admissionId)
            : orders.Where(o => o.OrderedAt >= since);

        return await (
                from o in orders
                from i in o.Items
                where serviceIds.Contains(i.ServiceItemId) && (i.Status == OrderItemStatus.Ordered || i.Status == OrderItemStatus.InProgress)
                select new DuplicateOrderLine(i.ServiceItemId, i.ServiceName, o.OrderNumber, o.OrderedAt))
            .ToListAsync(ct);
    }

    private async Task<Result<OrderDto>> ChangeAsync(System.Linq.Expressions.Expression<Func<ClinicalOrder, bool>> match, Func<ClinicalOrder, Result> change, CancellationToken ct)
    {
        var order = await db.Set<ClinicalOrder>().Include(o => o.Items).FirstOrDefaultAsync(match, ct);
        if (order is null)
        {
            return Error.NotFound("Order");
        }

        var allowed = await access.EnsureCanOpenAsync(order.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var result = change(order);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        await realtime.ToFacilityAsync(order.FacilityId, "clinical.order.changed", new { order.Id, order.PatientId }, ct);
        return await ToDtoAsync(order, ct);
    }
}
