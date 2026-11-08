namespace HyFib.HealthCare360.Platform.Contracts;

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
