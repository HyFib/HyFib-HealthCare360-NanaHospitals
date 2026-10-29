using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// A billable event raised by a clinical or support module (charge capture). Posting is idempotent on
/// (<see cref="SourceModule"/>, <see cref="SourceReference"/>), so WiFi retries never double-charge.
/// </summary>
public sealed record ChargeRequest
{
    public required Guid PatientId { get; init; }
    public Guid? VisitId { get; init; }
    public Guid? AdmissionId { get; init; }
    public required Guid ServiceItemId { get; init; }
    public decimal Quantity { get; init; } = 1;

    /// <summary>Overrides the tariff price (for example pharmacy MRP or implant cost).</summary>
    public decimal? UnitPriceOverride { get; init; }

    public string? Description { get; init; }

    /// <summary>Module key, for example "diagnostics.lab", "pharmacy.ip", "inpatient.bed".</summary>
    public required string SourceModule { get; init; }

    /// <summary>Stable id of the originating line (order item id, dispense line id…).</summary>
    public required string SourceReference { get; init; }

    /// <summary>Ordering/performing practitioner — drives doctor fee accounting.</summary>
    public Guid? PractitionerId { get; init; }

    public Guid? DepartmentId { get; init; }
    public DateTimeOffset? ServiceDate { get; init; }

    /// <summary>For a day of a bed (Inpatient's bed-day charge), the kind of room, which decides its GST; null otherwise.</summary>
    public RoomCharge? Room { get; init; }
}

/// <summary>
/// What a day of a bed is, for GST on room rent (<c>RoomRentGst</c> in Billing): health care is exempt, but a room other
/// than an ICU, CCU, ICCU or NICU charged above ₹5,000 a day is taxed.
/// </summary>
public enum RoomCharge
{
    /// <summary>A room or bed outside intensive care.</summary>
    Room = 0,

    /// <summary>A bed in intensive or critical care (Inpatient's ICU, NICU, PICU and HDU wards): exempt whatever it costs.</summary>
    IntensiveCare,
}

public enum ChargeBillingState
{
    NotFound = 0,
    Unbilled,
    Billed,
    Paid,
    Cancelled,
}

public sealed record DischargeClearance(bool Cleared, decimal Outstanding, string? Reason);

/// <summary>
/// A class of bed a facility charges by the day, for its rate list: the bed charge, the nursing charge of each day and
/// the charge of each consultant's ward-round visit in it, whether its wards are intensive care (no GST on room rent,
/// <see cref="RoomCharge"/>), and the names of its wards.
/// </summary>
public sealed record RoomClass(
    Guid BedServiceItemId, bool IsIntensiveCare, Guid? NursingServiceItemId, Guid? VisitServiceItemId, IReadOnlyList<string> Wards);

/// <summary>Implemented by the Inpatient module, which keeps the wards and charges their days and visits.</summary>
public interface IRoomClassDirectory
{
    /// <summary>The bed classes of the facility's wards in use, in the wards' display order.</summary>
    Task<IReadOnlyList<RoomClass>> ForFacilityAsync(Guid facilityId, CancellationToken cancellationToken);
}

/// <summary>Implemented by the Billing module; other modules post charges through it.</summary>
public interface IChargePoster
{
    Task<Result<Guid>> PostAsync(ChargeRequest request, CancellationToken cancellationToken);

    Task<Result> CancelAsync(string sourceModule, string sourceReference, string reason, CancellationToken cancellationToken);
}

/// <summary>Implemented by the Billing module; read-only billing facts for lower modules.</summary>
public interface IBillingStatusProvider
{
    Task<IReadOnlyDictionary<string, ChargeBillingState>> GetStatesAsync(string sourceModule, IReadOnlyCollection<string> sourceReferences, CancellationToken cancellationToken);

    Task<decimal> GetOutstandingAsync(Guid patientId, CancellationToken cancellationToken);

    Task<DischargeClearance> GetDischargeClearanceAsync(Guid admissionId, CancellationToken cancellationToken);
}

/// <summary>
/// A payer's name, and the patient's policy, membership or scheme card with it, for the patient banner (UX-04). Each
/// part is null when the caller may not see it, or there is none.
/// </summary>
/// <param name="PolicyNumber">The policy number, masked as the insurance screens show it.</param>
/// <param name="ValidUntil">The last day the policy is valid.</param>
public sealed record PayerCoverageSummary(string? PayerName, string? PolicyNumber, DateOnly? ValidUntil);

/// <summary>A payer of the payer master: its name, the kind of payer its bills are billed as, and whether it is in use.</summary>
public sealed record PayerSummary(Guid Id, string Name, PayerType PayerType, bool IsActive);

/// <summary>A company of the payer master (category Corporate) with its contact, as a corporate health check-up names it.</summary>
public sealed record CorporatePayer(Guid Id, string Name, string? ContactPerson, string? Phone);

/// <summary>Implemented by the Billing module, which keeps the payer master and the patients' policies.</summary>
public interface IPayerDirectory
{
    /// <summary>
    /// The payer's name, for callers who may look payers up, and the patient's policy with that payer that is active and
    /// in date today (the one valid longest, as billing picks it), for callers who may read policies.
    /// </summary>
    Task<PayerCoverageSummary> GetCoverageAsync(Guid patientId, Guid payerId, CancellationToken cancellationToken);

    /// <summary>
    /// The payer with this id, or null when there is none. Unlike <see cref="GetCoverageAsync"/> it answers whatever the
    /// caller's permissions: a module that bills its services to a payer has to know which payer that is.
    /// </summary>
    Task<PayerSummary?> FindAsync(Guid payerId, CancellationToken cancellationToken);

    /// <summary>
    /// The active corporate payers, by name, whatever the caller's permissions, like <see cref="FindAsync"/>: the
    /// check-up desk books a company's employees without reading the rest of the payer master.
    /// </summary>
    Task<IReadOnlyList<CorporatePayer>> ListCorporateAsync(CancellationToken cancellationToken);
}

/// <summary>Fallbacks used until the Billing module registers its implementations.</summary>
internal sealed class NullChargePoster : IChargePoster
{
    public Task<Result<Guid>> PostAsync(ChargeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<Guid>>(Error.Rule("billing.unavailable", "Billing module is not installed."));

    public Task<Result> CancelAsync(string sourceModule, string sourceReference, string reason, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal sealed class NullRoomClassDirectory : IRoomClassDirectory
{
    public Task<IReadOnlyList<RoomClass>> ForFacilityAsync(Guid facilityId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RoomClass>>([]);
}

internal sealed class NullPayerDirectory : IPayerDirectory
{
    public Task<PayerCoverageSummary> GetCoverageAsync(Guid patientId, Guid payerId, CancellationToken cancellationToken) =>
        Task.FromResult(new PayerCoverageSummary(null, null, null));

    public Task<PayerSummary?> FindAsync(Guid payerId, CancellationToken cancellationToken) => Task.FromResult<PayerSummary?>(null);

    public Task<IReadOnlyList<CorporatePayer>> ListCorporateAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CorporatePayer>>([]);
}

internal sealed class NullBillingStatusProvider : IBillingStatusProvider
{
    public Task<IReadOnlyDictionary<string, ChargeBillingState>> GetStatesAsync(string sourceModule, IReadOnlyCollection<string> sourceReferences, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, ChargeBillingState>>(sourceReferences.ToDictionary(r => r, _ => ChargeBillingState.NotFound));

    public Task<decimal> GetOutstandingAsync(Guid patientId, CancellationToken cancellationToken) => Task.FromResult(0m);

    public Task<DischargeClearance> GetDischargeClearanceAsync(Guid admissionId, CancellationToken cancellationToken) =>
        Task.FromResult(new DischargeClearance(true, 0m, null));
}
