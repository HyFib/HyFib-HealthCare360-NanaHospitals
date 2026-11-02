using System.Globalization;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Organization;

/// <summary>
/// What a service charges a patient who has no tariff plan of their own, the self-pay patient: its price and GST rate,
/// whether the counter may type another price, whether it may be discounted, and whether it may be charged at all
/// (maker-checker).
/// </summary>
public sealed record ServicePriceTerms(decimal BasePrice, decimal TaxRate, bool IsPriceEditable, bool IsDiscountAllowed, bool IsActive)
{
    /// <summary>A service not yet charged at any price.</summary>
    public static readonly ServicePriceTerms None = new(0m, 0m, false, false, false);

    public static ServicePriceTerms Of(ServiceItem service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new(service.BasePrice, service.TaxRate, service.IsPriceEditable, service.IsDiscountAllowed, service.IsActive);
    }

    /// <summary>
    /// These terms with what in <paramref name="asked"/> only narrows them: a price typed at the counter or a discount no
    /// longer allowed, the service taken out of use. A new price or GST rate, lower too (a wrong rate overcharges the
    /// patient or leaves the hospital owing the tax), or anything that lets the counter charge differently or at all, waits
    /// for a second person.
    /// </summary>
    public ServicePriceTerms Narrowed(ServicePriceTerms asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        return this with
        {
            IsPriceEditable = IsPriceEditable && asked.IsPriceEditable,
            IsDiscountAllowed = IsDiscountAllowed && asked.IsDiscountAllowed,
            IsActive = IsActive && asked.IsActive,
        };
    }

    /// <summary>
    /// Whether terms already waiting for approval (<paramref name="waiting"/>) ask for everything <paramref name="asked"/>
    /// needs beyond <paramref name="now"/>: the same price and GST rate, and each widening. A save that only narrows a
    /// service while a change waits (taking it out of use) is then made at once, and is not refused as a second request;
    /// the change that waits no longer fits the service, so approving it is refused (<see cref="ServicePrices.ChangedCode"/>).
    /// Any other save while a change waits is refused, the price in use put back included, so that change is never
    /// approved over a save its maker thought replaced it.
    /// </summary>
    public static bool Covers(ServicePriceTerms waiting, ServicePriceTerms now, ServicePriceTerms asked)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(asked);
        return waiting.BasePrice == asked.BasePrice
               && waiting.TaxRate == asked.TaxRate
               && (!asked.IsPriceEditable || now.IsPriceEditable || waiting.IsPriceEditable)
               && (!asked.IsDiscountAllowed || now.IsDiscountAllowed || waiting.IsDiscountAllowed)
               && (!asked.IsActive || now.IsActive || waiting.IsActive);
    }

    public void ApplyTo(ServiceItem service)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.BasePrice = BasePrice;
        service.TaxRate = TaxRate;
        service.IsPriceEditable = IsPriceEditable;
        service.IsDiscountAllowed = IsDiscountAllowed;
        service.IsActive = IsActive;
    }
}

/// <summary>A service's price terms asked for, and the terms they replace (a price changed meanwhile is not overwritten).</summary>
public sealed record ServicePricePayload(Guid ServiceItemId, ServicePriceTerms Asked, ServicePriceTerms Replaced);

/// <summary>A service's price terms waiting for a second person: what was asked, by whom and when.</summary>
public sealed record PendingServicePrice(Guid ApprovalId, ServicePriceTerms Asked, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// A service's price changes only when two people agree (maker-checker). Saving a service applies what only narrows its
/// terms at once and asks for the rest: a new price or GST rate, a price the counter may type, a discount allowed, the
/// service put in use. A new service is made out of use and goes live at the price a second person approves. The approver holds
/// <see cref="PlatformPermissions.PricesApprove"/> and is never the person who asked (the approvals' four eyes).
/// </summary>
internal sealed class ServicePrices(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "masters.service_price";

    /// <summary>Refused at approval: the service's terms changed after the request, so it would overwrite that change.</summary>
    public const string ChangedCode = "prices.changed";

    /// <summary>Refused on save: another change to the price is waiting for approval (the approvals' own code).</summary>
    public const string WaitingCode = "approval.already_requested";

    private const string NoReason = "No reason given.";

    /// <summary>Asks a second person to set <paramref name="service"/>'s terms to <paramref name="asked"/>.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(
        ServiceItem service, ServicePriceTerms replaced, ServicePriceTerms asked, string? reason, CancellationToken ct)
    {
        List<ApprovalDetail> details =
        [
            new("Service", $"{service.Code} {service.Name}"),
            new("Price", replaced.BasePrice == asked.BasePrice ? Rupees(asked.BasePrice) : $"{Rupees(replaced.BasePrice)} → {Rupees(asked.BasePrice)}"),
        ];
        if (replaced.TaxRate != asked.TaxRate)
        {
            details.Add(new("GST", $"{Gst(replaced.TaxRate)} → {Gst(asked.TaxRate)}"));
        }

        if (replaced.IsPriceEditable != asked.IsPriceEditable)
        {
            details.Add(new("Price typed at the counter", asked.IsPriceEditable ? "Allowed" : "Not allowed"));
        }

        if (replaced.IsDiscountAllowed != asked.IsDiscountAllowed)
        {
            details.Add(new("Discounts", asked.IsDiscountAllowed ? "Allowed" : "Not allowed"));
        }

        if (!replaced.IsActive && asked.IsActive)
        {
            details.Add(new("In use", "Goes live when approved"));
        }

        var facility = current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(ServiceItem), service.Id, service.Version, facility, null),
            new ServicePricePayload(service.Id, asked, replaced),
            $"Price of {service.Code} {service.Name}: {Describe(replaced, asked)}",
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>The terms waiting for approval for this service, if any.</summary>
    public async Task<PendingServicePrice?> PendingAsync(Guid serviceItemId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(ServiceItem) && r.SubjectId == serviceItemId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
        return request is null
            ? null
            : new PendingServicePrice(request.Id, ApprovalPayload.Read<ServicePricePayload>(request.Payload).Asked, request.RequestedByName, request.RequestedAt);
    }

    /// <summary>
    /// Sets an approved service's terms, in the approval's transaction, if they are still the ones the request replaces:
    /// a change made meanwhile is never overwritten, and the request can be asked again on the service as it is now.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ServicePricePayload>(request.Payload);
        var service = await db.Set<ServiceItem>().FirstOrDefaultAsync(s => s.Id == asked.ServiceItemId, ct);
        if (service is null)
        {
            return Error.Rule("prices.service_gone", "The service no longer exists, so its price was not set.");
        }

        var before = ServicePriceTerms.Of(service);
        if (before != asked.Replaced)
        {
            return Error.Rule(ChangedCode,
                $"{service.Code}'s price or terms changed after this was asked for ({Describe(asked.Replaced, before)}), so nothing was set. Ask again.");
        }

        asked.Asked.ApplyTo(service);
        audit.Add(new AuditEntry(AuditCategories.Admin, "service.price_changed",
            Fit($"{service.Code} {service.Name}: {Describe(before, asked.Asked)} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(ServiceItem),
            EntityId = service.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { before, after = asked.Asked, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return Describe(before, asked.Asked);
    }

    /// <summary>What changes from <paramref name="from"/> to <paramref name="to"/>, in words.</summary>
    internal static string Describe(ServicePriceTerms from, ServicePriceTerms to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        List<string> parts = [];
        if (from.BasePrice != to.BasePrice)
        {
            parts.Add($"{Rupees(from.BasePrice)} → {Rupees(to.BasePrice)}");
        }

        if (from.TaxRate != to.TaxRate)
        {
            parts.Add($"GST {Gst(from.TaxRate)} → {Gst(to.TaxRate)}");
        }

        if (from.IsPriceEditable != to.IsPriceEditable)
        {
            parts.Add(to.IsPriceEditable ? "price typed at the counter allowed" : "price typed at the counter no longer allowed");
        }

        if (from.IsDiscountAllowed != to.IsDiscountAllowed)
        {
            parts.Add(to.IsDiscountAllowed ? "discounts allowed" : "discounts no longer allowed");
        }

        if (from.IsActive != to.IsActive)
        {
            parts.Add(to.IsActive ? "put in use" : "taken out of use");
        }

        return parts.Count == 0 ? $"{Rupees(to.BasePrice)}, unchanged" : string.Join("; ", parts);
    }

    private static string Rupees(decimal amount) => "₹" + Money.FormatInr(amount);

    private static string Gst(decimal rate) => rate == 0m ? "exempt" : rate.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A service's price terms changed: one person asks, another who may approve prices decides (maker-checker).</summary>
internal sealed class ServicePriceApproval(ServicePrices prices) : IApprovalAction
{
    public string Kind => ServicePrices.Kind;

    public string Title => "Price of a service";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => prices.ApplyAsync(request, ct);
}
