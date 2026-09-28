using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Orders;

/// <summary>
/// One ordered service. <see cref="Quantity"/> (blood units, physiotherapy/dialysis sessions) defaults to 1 and may
/// exceed 1 only for those services; each other test is ordered once per line.
/// </summary>
public sealed record OrderLineRequest(Guid ServiceItemId, string? Notes, int? Quantity = null);

/// <summary>
/// Places an order for services. Lines come from <see cref="Items"/> and/or an order set. Services already ordered and
/// still pending for the same visit/admission are rejected with 409 unless <see cref="ConfirmDuplicates"/> is set.
/// </summary>
public sealed record PlaceOrderRequest(
    Guid PatientId,
    Guid? EncounterId,
    Guid? VisitId,
    Guid? AdmissionId,
    OrderPriority Priority,
    string? ClinicalNotes,
    IReadOnlyList<OrderLineRequest>? Items,
    Guid? OrderSetId,
    bool ConfirmDuplicates);

public sealed record CancelOrderRequest(string Reason);

public sealed record UpdateOrderItemStatusRequest(OrderItemStatus Status, string? ResultReference);

public sealed record OrderItemDto(
    Guid Id,
    Guid ServiceItemId,
    string ServiceCode,
    string ServiceName,
    ServiceCategory Category,
    int Quantity,
    OrderItemStatus Status,
    string? Notes,
    DateTimeOffset? StatusChangedAt,
    DateTimeOffset? CompletedAt,
    string? ResultReference,
    string? CancelReason);

public sealed record OrderDto(
    Guid Id,
    string OrderNumber,
    Guid PatientId,
    Guid? EncounterId,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid? OrderingPractitionerId,
    string? OrderingPractitionerName,
    OrderPriority Priority,
    ClinicalOrderStatus Status,
    OrderSource Source,
    DateTimeOffset OrderedAt,
    string? ClinicalNotes,
    IReadOnlyList<OrderItemDto> Items);

public sealed record DuplicateOrderLine(Guid ServiceItemId, string ServiceName, string OrderNumber, DateTimeOffset OrderedAt);

public sealed class PlaceOrderRequestValidator : AbstractValidator<PlaceOrderRequest>
{
    public PlaceOrderRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x)
            .Must(x => x.Items is { Count: > 0 } || x.OrderSetId is not null)
            .WithName("items").WithMessage("Choose at least one service or an order set.");
        RuleFor(x => x.Items).Must(items => items is null || items.Count <= 50).WithMessage("An order can have at most 50 lines.");
        RuleForEach(x => x.Items).ChildRules(line =>
        {
            line.RuleFor(l => l.ServiceItemId).NotEmpty();
            line.RuleFor(l => l.Notes).SafeText(500);
            // Shape only; whether this service may be ordered in quantity is checked against its category when placing.
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, OrderQuantity.Largest).When(l => l.Quantity is not null);
        });
        RuleFor(x => x.ClinicalNotes).SafeText(1000);
    }
}

public sealed class CancelOrderRequestValidator : AbstractValidator<CancelOrderRequest>
{
    public CancelOrderRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}

public sealed class UpdateOrderItemStatusRequestValidator : AbstractValidator<UpdateOrderItemStatusRequest>
{
    public UpdateOrderItemStatusRequestValidator()
    {
        RuleFor(x => x.Status).Must(s => s is OrderItemStatus.InProgress or OrderItemStatus.Completed)
            .WithMessage("Only InProgress or Completed can be set here; use cancel to cancel a line.");
        RuleFor(x => x.ResultReference).MaximumLength(64);
    }
}
