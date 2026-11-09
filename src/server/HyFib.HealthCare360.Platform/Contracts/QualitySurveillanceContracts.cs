using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>What another module found that the Quality incident register is to hold.</summary>
public enum SafetyEventKind
{
    /// <summary>A dose not given as prescribed (on the MAR: a dose recorded as missed, an error of omission).</summary>
    MedicationError = 0,
    PatientFall,

    /// <summary>A pressure injury that developed in hospital (not one present on admission).</summary>
    PressureInjury,
    TransfusionReaction,
    AdverseDrugReaction,
}

/// <summary>
/// A patient-safety event another module recorded and reports to the incident register as it records it: the blood
/// bank's transfusion reaction, pharmacovigilance's ADR report, a missed dose on the MAR, a fall or a pressure injury on
/// the nursing chart. The source record names it, so the same record raises one incident however often it is saved.
/// </summary>
/// <param name="SourceRecordType">The kind of record, e.g. "TransfusionReaction"; with <paramref name="SourceRecordId"/> it identifies the record.</param>
/// <param name="SourceReference">How staff know the record (the unit number, the ADR report number, the drug and dose).</param>
public sealed record ReportedSafetyEvent(
    Guid FacilityId,
    Guid PatientId,
    SafetyEventKind Kind,
    IncidentHarm Harm,
    DateTimeOffset OccurredAt,
    string Location,
    string Description,
    string? ImmediateAction,
    string SourceModule,
    string SourceRecordType,
    Guid SourceRecordId,
    string SourceReference);

/// <summary>An operation as the theatre records it, for the quality indicators and infection surveillance.</summary>
/// <param name="Elective">Booked as elective; an urgent or emergency operation is unplanned.</param>
public sealed record SurgeryFact(
    Guid BookingId,
    string BookingNumber,
    Guid PatientId,
    Guid? AdmissionId,
    string ProcedureName,
    bool Elective,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

/// <summary>A unit of blood or a component started on a patient.</summary>
public sealed record TransfusionFact(Guid TransfusionId, string UnitNumber, Guid PatientId, DateTimeOffset StartedAt);

/// <summary>A reaction to a transfusion as the blood bank recorded it.</summary>
public sealed record TransfusionReactionFact(Guid ReactionId, string UnitNumber, Guid PatientId, DateTimeOffset OnsetAt, string Type, string Severity);

/// <summary>
/// The theatre's operations and the blood bank's transfusions, for the Quality module, which cannot reference
/// SpecialtyCare (same layer). Raw facts only: what counts as an unplanned return or a reaction rate is decided where the
/// indicator is defined.
/// </summary>
public interface ISurgeryAndTransfusionFacts
{
    /// <summary>False while no module provides the facts: the indicators built on them say so instead of showing zero.</summary>
    bool Available { get; }

    /// <summary>Operations of the facility completed in [start, end).</summary>
    Task<IReadOnlyList<SurgeryFact>> SurgeriesAsync(Guid facilityId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);

    /// <summary>A patient's operations completed since <paramref name="since"/>, any facility.</summary>
    Task<IReadOnlyList<SurgeryFact>> PatientSurgeriesAsync(Guid patientId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Transfusions of the facility started in [start, end).</summary>
    Task<IReadOnlyList<TransfusionFact>> TransfusionsAsync(Guid facilityId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);

    /// <summary>Reactions of the facility with their onset in [start, end).</summary>
    Task<IReadOnlyList<TransfusionReactionFact>> TransfusionReactionsAsync(Guid facilityId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);
}

/// <summary>Fallback until SpecialtyCare registers its facts: nothing is known, and <see cref="Available"/> says so.</summary>
internal sealed class NullSurgeryAndTransfusionFacts : ISurgeryAndTransfusionFacts
{
    public bool Available => false;

    public Task<IReadOnlyList<SurgeryFact>> SurgeriesAsync(Guid facilityId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SurgeryFact>>([]);

    public Task<IReadOnlyList<SurgeryFact>> PatientSurgeriesAsync(Guid patientId, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SurgeryFact>>([]);

    public Task<IReadOnlyList<TransfusionFact>> TransfusionsAsync(Guid facilityId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TransfusionFact>>([]);

    public Task<IReadOnlyList<TransfusionReactionFact>> TransfusionReactionsAsync(Guid facilityId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TransfusionReactionFact>>([]);
}
