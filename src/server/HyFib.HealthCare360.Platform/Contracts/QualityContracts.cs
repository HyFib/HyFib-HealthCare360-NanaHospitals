using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>The harm an incident did, graded as NABH incident reporting grades it, from none reaching the patient to death.</summary>
public enum IncidentHarm
{
    NearMiss = 0,
    NoHarm,
    Mild,
    Moderate,
    Severe,
    Death,
}

/// <summary>A never event a department raises itself: sentinel whatever the harm, and its root-cause analysis is due.</summary>
public enum NeverEvent
{
    /// <summary>A swab, needle, instrument or other item left in the patient after an operation.</summary>
    RetainedForeignObject = 0,
}

/// <summary>A patient-safety incident raised by the department where it happened, named to the member of staff who raised it.</summary>
public sealed record PatientSafetyIncident(
    Guid FacilityId,
    Guid PatientId,
    NeverEvent Kind,
    IncidentHarm Harm,
    DateTimeOffset OccurredAt,
    string Location,
    string Description,
    string? ImmediateAction,
    bool InvolvesDevice);

/// <summary>The incident as the Quality register numbered it.</summary>
public sealed record RaisedIncident(Guid IncidentId, string IncidentNumber);

/// <summary>
/// The Quality module's incident register, for the modules that cannot reference it (SpecialtyCare's theatre raises a
/// retained swab or instrument). The incident is added in the caller's unit of work and transaction, so it is reported only if
/// the caller's change is saved; the quality team is alerted as for any sentinel event.
/// </summary>
public interface IIncidentRegister
{
    Task<Result<RaisedIncident>> RaiseAsync(PatientSafetyIncident incident, CancellationToken cancellationToken);
}

/// <summary>Fallback used until the Quality module registers its register: nothing can be raised, and the caller is told so.</summary>
internal sealed class NullIncidentRegister : IIncidentRegister
{
    public Task<Result<RaisedIncident>> RaiseAsync(PatientSafetyIncident incident, CancellationToken cancellationToken) =>
        Task.FromResult<Result<RaisedIncident>>(Error.Rule("incident.register_unavailable",
            "The incident register is not available here, so the incident could not be raised. Report it on paper to the quality team."));
}

/// <summary>A corrective or preventive action in the Quality module's register, as another module links it.</summary>
public sealed record CapaLink(Guid Id, string Number, string Status, bool IsOpen, DateOnly DueDate);

/// <summary>
/// The Quality module's CAPA register (IRMS-04) as another module reads it to link a corrective action to its own record:
/// the lab's unacceptable external quality assessment round, for one. Absent when the Quality module is not installed.
/// </summary>
public interface ICapaRegister
{
    /// <summary>The action, in the signed-in user's facility, or null when there is none.</summary>
    Task<CapaLink?> FindAsync(Guid capaId, CancellationToken cancellationToken);

    /// <summary>The action with this number (<c>MAINCAPA260012</c>), in the signed-in user's facility, or null.</summary>
    Task<CapaLink?> FindByNumberAsync(string number, CancellationToken cancellationToken);

    /// <summary>The actions among these that exist, by id.</summary>
    Task<IReadOnlyDictionary<Guid, CapaLink>> FindManyAsync(IReadOnlyCollection<Guid> capaIds, CancellationToken cancellationToken);
}
