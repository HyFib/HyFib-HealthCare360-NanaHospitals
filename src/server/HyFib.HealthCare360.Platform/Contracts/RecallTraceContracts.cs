namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>How a recalled batch reached a patient outside the pharmacy: what kind of use another module recorded.</summary>
public enum RecalledUseKind
{
    /// <summary>An implant recorded against an operation, the patient carrying it.</summary>
    Implant = 0,

    /// <summary>A consumable used on the patient in an operation.</summary>
    Consumable,
}

/// <summary>
/// One use of a recalled batch on a patient that a module other than the pharmacy recorded (an implant or a consumable
/// recorded against an operation), for the recall trace.
/// </summary>
/// <param name="Kind">An implant or a consumable.</param>
/// <param name="At">When it was used: the operation's start, else when it was recorded.</param>
/// <param name="Document">The record it is on (the surgery's booking number).</param>
/// <param name="Place">Where it was used (the operation theatre).</param>
/// <param name="PatientId">The patient it was used on.</param>
/// <param name="Quantity">How many were used.</param>
/// <param name="SerialNumber">The device's serial number as recorded, for an implant that has one.</param>
/// <param name="Procedure">The operation, as booked.</param>
/// <param name="Clinician">The surgeon, who reviews the patient.</param>
public sealed record RecalledUse(
    RecalledUseKind Kind,
    DateTimeOffset At,
    string Document,
    string Place,
    Guid PatientId,
    decimal Quantity,
    string? SerialNumber,
    string? Procedure,
    string? Clinician);

/// <summary>
/// What a module recorded of a batch used on patients, which the pharmacy's recall trace lists beside its own dispenses,
/// sales and ward issues. Each module that records stock used on a patient registers one, so the pharmacy names every
/// patient a recalled lot reached without referencing the module.
/// </summary>
public interface IRecallTraceSource
{
    /// <summary>Every use of the item's batch on a patient in this module, in the signed-in facility; the batch is upper case.</summary>
    Task<IReadOnlyList<RecalledUse>> UsesAsync(Guid itemId, string batchNumber, CancellationToken cancellationToken);
}
