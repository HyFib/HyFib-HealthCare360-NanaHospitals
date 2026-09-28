namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// A labelled blood component unit in inventory (BB-03..05). The status machine keeps unsafe units out of stock:
/// a unit leaves quarantine only when it is grouped and every TTI marker is non-reactive, and it can never be
/// reserved or issued once expired.
/// </summary>
public sealed class BloodComponent : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid DonationId { get; set; }

    /// <summary>Donation bag number + component suffix ("NH26000123-R"); printed as a barcode.</summary>
    public string UnitNumber { get; set; } = string.Empty;

    public BloodComponentType ComponentType { get; set; }
    public BloodGroup BloodGroup { get; set; }
    public int VolumeMl { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
    public DateTimeOffset PreparedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public BloodUnitStatus Status { get; set; }
    public TtiPanel Tti { get; set; } = new();
    public string? StorageLocation { get; set; }
    public Guid? ReservedForRequestId { get; set; }
    public DateTimeOffset? ReservedAt { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? DiscardedAt { get; set; }
    public string? DiscardReason { get; set; }

    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsInStock => Status is BloodUnitStatus.Quarantine or BloodUnitStatus.Available or BloodUnitStatus.Reserved;

    public static BloodComponent Prepare(Donation donation, BloodComponentType type, int? volumeMl, string? storageLocation, DateTimeOffset now) => new()
    {
        FacilityId = donation.FacilityId,
        DonationId = donation.Id,
        UnitNumber = $"{donation.BagNumber}-{ComponentShelfLife.Suffix(type)}",
        ComponentType = type,
        BloodGroup = donation.BloodGroup,
        VolumeMl = volumeMl ?? ComponentShelfLife.DefaultVolumeMl(type, donation.VolumeMl),
        CollectedAt = donation.CollectedAt,
        PreparedAt = now,
        ExpiresAt = ComponentShelfLife.ExpiryFor(type, donation.CollectedAt),
        Status = BloodUnitStatus.Quarantine,
        Tti = donation.Tti with { },
        StorageLocation = storageLocation,
    };

    /// <summary>Copies the donation's lab results; releases the unit to stock only if grouped and all markers are non-reactive.</summary>
    public void ApplyLabResults(BloodGroup group, TtiPanel tti, DateTimeOffset now)
    {
        if (Status != BloodUnitStatus.Quarantine)
        {
            return;
        }

        BloodGroup = group;
        Tti = tti with { };
        if (tti.AnyReactive)
        {
            MarkDiscarded($"Reactive TTI screen: {string.Join(", ", tti.ReactiveMarkers)}", now);
        }
        else if (tti.AllNonReactive && BloodCompatibility.IsKnown(group))
        {
            Status = IsExpiredAt(now) ? BloodUnitStatus.Expired : BloodUnitStatus.Available;
        }
    }

    public Result Reserve(Guid requestId, DateTimeOffset now)
    {
        if (IsExpiredAt(now))
        {
            return Error.Rule("bloodbank.unit_expired", $"Unit {UnitNumber} has expired.");
        }

        if (Status == BloodUnitStatus.Reserved && ReservedForRequestId == requestId)
        {
            return Result.Success();
        }

        if (Status != BloodUnitStatus.Available)
        {
            return Error.Rule("bloodbank.unit_unavailable", $"Unit {UnitNumber} is {Status.ToString().ToLowerInvariant()}, not available.");
        }

        Status = BloodUnitStatus.Reserved;
        ReservedForRequestId = requestId;
        ReservedAt = now;
        return Result.Success();
    }

    public void ReleaseReservation(DateTimeOffset now)
    {
        if (Status != BloodUnitStatus.Reserved)
        {
            return;
        }

        Status = IsExpiredAt(now) ? BloodUnitStatus.Expired : BloodUnitStatus.Available;
        ReservedForRequestId = null;
        ReservedAt = null;
    }

    /// <summary>Hands the unit over for transfusion. Compatibility is checked by <see cref="IssueCompatibility"/> before this.</summary>
    public Result Issue(Guid requestId, DateTimeOffset now)
    {
        if (IsExpiredAt(now))
        {
            return Error.Rule("bloodbank.unit_expired", $"Unit {UnitNumber} has expired and must not be transfused.");
        }

        if (!Tti.AllNonReactive)
        {
            return Error.Rule("bloodbank.unit_not_screened", $"Unit {UnitNumber} has not cleared TTI screening.");
        }

        var reservedForOther = Status == BloodUnitStatus.Reserved && ReservedForRequestId != requestId;
        if (reservedForOther || Status is not (BloodUnitStatus.Available or BloodUnitStatus.Reserved))
        {
            return Error.Rule("bloodbank.unit_unavailable", $"Unit {UnitNumber} cannot be issued for this request ({Status.ToString().ToLowerInvariant()}).");
        }

        Status = BloodUnitStatus.Issued;
        ReservedForRequestId = requestId;
        IssuedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// A returned unit goes back to stock only if <see cref="ColdChain.CanRestock"/> allows it; otherwise it is
    /// discarded, because its storage temperature can no longer be guaranteed.
    /// </summary>
    public Result Return(DateTimeOffset issuedAt, DateTimeOffset returnedAt, bool temperatureMaintained)
    {
        if (Status != BloodUnitStatus.Issued)
        {
            return Error.Rule("bloodbank.unit_not_issued", $"Unit {UnitNumber} is not issued.");
        }

        var restock = ColdChain.CanRestock(ComponentType, issuedAt, returnedAt, temperatureMaintained);
        if (restock.IsSuccess && !IsExpiredAt(returnedAt))
        {
            Status = BloodUnitStatus.Available;
            ReservedForRequestId = null;
            ReservedAt = null;
            IssuedAt = null;
        }
        else
        {
            MarkDiscarded(restock.Error?.Message ?? "Expired while issued.", returnedAt);
        }

        return Result.Success();
    }

    /// <summary>Removes an in-stock unit (broken bag, haemolysis, clots, failed QC). Issued units are handled by <see cref="Return"/>.</summary>
    public Result Discard(string reason, DateTimeOffset now)
    {
        if (!IsInStock && Status != BloodUnitStatus.Expired)
        {
            return Error.Rule("bloodbank.unit_not_in_stock", $"Unit {UnitNumber} is {Status.ToString().ToLowerInvariant()} and cannot be discarded.");
        }

        MarkDiscarded(reason, now);
        return Result.Success();
    }

    private void MarkDiscarded(string reason, DateTimeOffset now)
    {
        Status = BloodUnitStatus.Discarded;
        ReservedForRequestId = null;
        ReservedAt = null;
        DiscardedAt = now;
        DiscardReason = reason.Trim();
    }

    /// <summary>Used by the daily expiry job; returns true when the unit changed.</summary>
    public bool MarkExpiredIfDue(DateTimeOffset now)
    {
        if (!IsInStock || !IsExpiredAt(now))
        {
            return false;
        }

        Status = BloodUnitStatus.Expired;
        ReservedForRequestId = null;
        ReservedAt = null;
        return true;
    }
}
