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

/// <summary>
/// A payer's name, and the patient's policy, membership or scheme card with it, for the patient banner (UX-04). Each
/// part is null when the caller may not see it, or there is none.
/// </summary>
/// <param name="PolicyNumber">The policy number, masked as the insurance screens show it.</param>
/// <param name="ValidUntil">The last day the policy is valid.</param>
public sealed record PayerCoverageSummary(string? PayerName, string? PolicyNumber, DateOnly? ValidUntil);

/// <summary>A payer of the payer master: its name, the kind of payer its bills are billed as, and whether it is in use.</summary>
public sealed record PayerSummary(Guid Id, string Name, PayerType PayerType, bool IsActive);

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
}

/// <summary>Fallbacks used until the Billing module registers its implementations.</summary>
internal sealed class NullChargePoster : IChargePoster
{
    public Task<Result<Guid>> PostAsync(ChargeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<Guid>>(Error.Rule("billing.unavailable", "Billing module is not installed."));

    public Task<Result> CancelAsync(string sourceModule, string sourceReference, string reason, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal sealed class NullPayerDirectory : IPayerDirectory
{
    public Task<PayerCoverageSummary> GetCoverageAsync(Guid patientId, Guid payerId, CancellationToken cancellationToken) =>
        Task.FromResult(new PayerCoverageSummary(null, null, null));

    public Task<PayerSummary?> FindAsync(Guid payerId, CancellationToken cancellationToken) => Task.FromResult<PayerSummary?>(null);
}

internal sealed class NullBillingStatusProvider : IBillingStatusProvider
{
    public Task<IReadOnlyDictionary<string, ChargeBillingState>> GetStatesAsync(string sourceModule, IReadOnlyCollection<string> sourceReferences, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, ChargeBillingState>>(sourceReferences.ToDictionary(r => r, _ => ChargeBillingState.NotFound));

    public Task<decimal> GetOutstandingAsync(Guid patientId, CancellationToken cancellationToken) => Task.FromResult(0m);

    public Task<DischargeClearance> GetDischargeClearanceAsync(Guid admissionId, CancellationToken cancellationToken) =>
        Task.FromResult(new DischargeClearance(true, 0m, null));
}
