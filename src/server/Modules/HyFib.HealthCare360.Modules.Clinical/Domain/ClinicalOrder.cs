namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum ClinicalOrderStatus
{
    Active = 0,
    Completed,
    Cancelled,
}

public enum OrderItemStatus
{
    Ordered = 0,
    InProgress,
    Completed,
    Cancelled,
}

public enum OrderSource
{
    Encounter = 0,
    HealthCheckup,
    Direct,
}

/// <summary>
/// How many units one order line may request. Only blood components (units) and physiotherapy/dialysis courses
/// (sessions) are ordered in quantity; every other service is one per line, so a repeat test is a new line with its
/// own sample, report and charge. The caps catch keying slips ("22" for "2") before they reach the blood bank.
/// </summary>
public static class OrderQuantity
{
    public const int Largest = 30;

    public static int MaxFor(ServiceCategory category) => category switch
    {
        ServiceCategory.BloodBank => 10,
        ServiceCategory.Physiotherapy or ServiceCategory.Dialysis => Largest,
        _ => 1,
    };

    public static string UnitFor(ServiceCategory category) => category == ServiceCategory.BloodBank ? "units" : "sessions";

    public static Result Check(ServiceItem service, int quantity)
    {
        var max = MaxFor(service.Category);
        if (quantity >= 1 && quantity <= max)
        {
            return Result.Success();
        }

        return Error.Rule("order_item.quantity", max == 1
            ? $"{service.Name} is ordered one at a time; add it again as a separate order to repeat it."
            : $"{service.Name}: order between 1 and {max} {UnitFor(service.Category)} on one line.");
    }
}

/// <summary>
/// A clinician's order for services (lab, imaging, cardiology, procedures…). Placing it raises one
/// <see cref="ServiceOrderPlaced"/> per line so Diagnostics/SpecialtyCare create their work items and Billing can
/// capture charges, all in the same transaction. The order's status is derived from its lines. Orders placed for a
/// health checkup carry the booking id on every event, because those tests are billed once, under the package.
/// </summary>
public sealed class ClinicalOrder : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? OrderingPractitionerId { get; set; }
    public Guid? OrderedByUserId { get; set; }
    public OrderPriority Priority { get; set; }
    public ClinicalOrderStatus Status { get; private set; }
    public OrderSource Source { get; set; }
    public Guid? PackageBookingId { get; set; }
    public DateTimeOffset OrderedAt { get; set; }
    public string? ClinicalNotes { get; set; }
    public List<ClinicalOrderItem> Items { get; set; } = [];

    public Result<ClinicalOrderItem> AddItem(ServiceItem service, string? notes, int quantity = 1)
    {
        var allowed = OrderQuantity.Check(service, quantity);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var item = new ClinicalOrderItem
        {
            OrderId = Id,
            ServiceItemId = service.Id,
            ServiceCode = service.Code,
            ServiceName = service.Name,
            Category = service.Category,
            Quantity = quantity,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };
        Items.Add(item);
        return item;
    }

    /// <summary>Publishes every line to the performing departments. Call once, after the number is allocated.</summary>
    public Result Place(DateTimeOffset now)
    {
        if (Items.Count == 0)
        {
            return Error.Rule("order.empty", "Add at least one service to the order.");
        }

        if (string.IsNullOrWhiteSpace(OrderNumber))
        {
            return Error.Rule("order.number_missing", "The order has no number yet.");
        }

        // Billing prices a line unless it names a package booking: a checkup test without it would be charged on top of
        // the package, and an ordinary test with one would never be charged at all.
        if ((Source == OrderSource.HealthCheckup) != (PackageBookingId is not null))
        {
            return Error.Rule("order.package_link", "Health-checkup orders must reference their booking, and no other order may.");
        }

        OrderedAt = now;
        foreach (var item in Items)
        {
            Raise(new ServiceOrderPlaced(item.Id, Id, OrderNumber, PatientId, VisitId, AdmissionId, FacilityId, item.ServiceItemId,
                item.ServiceCode, item.ServiceName, item.Category, Priority, OrderingPractitionerId, item.Notes ?? ClinicalNotes, now,
                PackageBookingId, item.Quantity));
        }

        RefreshStatus();
        return Result.Success();
    }

    public Result CancelItem(Guid itemId, string reason, DateTimeOffset now)
    {
        var item = Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return Error.NotFound("Order item", itemId);
        }

        var result = item.Cancel(reason, now);
        if (result.IsFailure)
        {
            return result;
        }

        Raise(new ServiceOrderCancelled(item.Id, PatientId, reason.Trim()));
        RefreshStatus();
        return Result.Success();
    }

    /// <summary>Cancels every line not yet completed; completed lines (results exist) are left untouched.</summary>
    public Result Cancel(string reason, DateTimeOffset now)
    {
        var open = Items.Where(i => i.IsOpen).ToList();
        if (open.Count == 0)
        {
            return Error.Rule("order.closed", "Nothing on this order can be cancelled.");
        }

        foreach (var item in open)
        {
            item.Cancel(reason, now);
            Raise(new ServiceOrderCancelled(item.Id, PatientId, reason.Trim()));
        }

        RefreshStatus();
        return Result.Success();
    }

    public Result UpdateItemStatus(Guid itemId, OrderItemStatus status, DateTimeOffset now, string? resultReference = null)
    {
        var item = Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return Error.NotFound("Order item", itemId);
        }

        Result result = status switch
        {
            OrderItemStatus.InProgress => item.Start(now),
            OrderItemStatus.Completed => item.Complete(now, resultReference),
            _ => Error.Rule("order_item.invalid_status", "Use cancel to cancel an order line."),
        };
        if (result.IsSuccess)
        {
            RefreshStatus();
        }

        return result;
    }

    private void RefreshStatus() => Status = DeriveStatus(Items.Select(i => i.Status).ToList());

    public static ClinicalOrderStatus DeriveStatus(IReadOnlyCollection<OrderItemStatus> lines) =>
        lines.Count > 0 && lines.All(s => s == OrderItemStatus.Cancelled) ? ClinicalOrderStatus.Cancelled
        : lines.Count > 0 && lines.All(s => s is OrderItemStatus.Completed or OrderItemStatus.Cancelled) ? ClinicalOrderStatus.Completed
        : ClinicalOrderStatus.Active;
}

/// <summary>One ordered service with a snapshot of its code, name and category at the time of ordering.</summary>
public sealed class ClinicalOrderItem : AuditableEntity
{
    public Guid OrderId { get; set; }
    public Guid ServiceItemId { get; set; }
    public string ServiceCode { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public ServiceCategory Category { get; set; }

    /// <summary>Blood units or therapy sessions requested; 1 for every other service (see <see cref="OrderQuantity"/>).</summary>
    public int Quantity { get; init; } = 1;

    public OrderItemStatus Status { get; private set; }
    public string? Notes { get; set; }
    public DateTimeOffset? StatusChangedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Id of the released report (lab/radiology/cardiology) that completed this line.</summary>
    public string? ResultReference { get; private set; }

    public string? CancelReason { get; private set; }

    public bool IsOpen => Status is OrderItemStatus.Ordered or OrderItemStatus.InProgress;

    internal Result Start(DateTimeOffset now)
    {
        if (Status != OrderItemStatus.Ordered)
        {
            return Error.Rule("order_item.not_ordered", "Only a newly ordered line can be started.");
        }

        Status = OrderItemStatus.InProgress;
        StatusChangedAt = now;
        return Result.Success();
    }

    internal Result Complete(DateTimeOffset now, string? resultReference)
    {
        if (Status == OrderItemStatus.Cancelled)
        {
            return Error.Rule("order_item.cancelled", "This order line was cancelled.");
        }

        Status = OrderItemStatus.Completed;
        StatusChangedAt = now;
        CompletedAt ??= now;
        ResultReference = resultReference ?? ResultReference;
        return Result.Success();
    }

    internal Result Cancel(string reason, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("order_item.closed", Status == OrderItemStatus.Completed
                ? "This line is completed and has results; it cannot be cancelled."
                : "This line is already cancelled.");
        }

        Status = OrderItemStatus.Cancelled;
        StatusChangedAt = now;
        CancelReason = reason.Trim();
        return Result.Success();
    }
}
