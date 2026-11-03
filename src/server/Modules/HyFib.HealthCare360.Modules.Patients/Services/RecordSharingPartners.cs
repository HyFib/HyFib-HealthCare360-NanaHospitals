namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>An account another care provider reads records with through the FHIR API, as a record-sharing consent names it.</summary>
public sealed record SharingPartner(Guid Id, string Name);

/// <summary>
/// The accounts a record-sharing consent can name (implemented by the Interop module, which owns the FHIR API and its
/// permission): the integration accounts the hospital gave FHIR access through a role of its own.
/// </summary>
public interface ISharingPartnerDirectory
{
    /// <summary>The partner accounts now, by name.</summary>
    Task<IReadOnlyList<SharingPartner>> ListAsync(CancellationToken ct);
}
