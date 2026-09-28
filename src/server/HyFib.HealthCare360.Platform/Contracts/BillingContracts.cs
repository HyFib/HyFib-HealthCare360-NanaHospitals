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

/// <summary>Fallbacks used until the Billing module registers its implementations.</summary>
internal sealed class NullChargePoster : IChargePoster
{
    public Task<Result<Guid>> PostAsync(ChargeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<Guid>>(Error.Rule("billing.unavailable", "Billing module is not installed."));

    public Task<Result> CancelAsync(string sourceModule, string sourceReference, string reason, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal sealed class NullBillingStatusProvider : IBillingStatusProvider
{
    public Task<IReadOnlyDictionary<string, ChargeBillingState>> GetStatesAsync(string sourceModule, IReadOnlyCollection<string> sourceReferences, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, ChargeBillingState>>(sourceReferences.ToDictionary(r => r, _ => ChargeBillingState.NotFound));

    public Task<decimal> GetOutstandingAsync(Guid patientId, CancellationToken cancellationToken) => Task.FromResult(0m);

    public Task<DischargeClearance> GetDischargeClearanceAsync(Guid admissionId, CancellationToken cancellationToken) =>
        Task.FromResult(new DischargeClearance(true, 0m, null));
}
