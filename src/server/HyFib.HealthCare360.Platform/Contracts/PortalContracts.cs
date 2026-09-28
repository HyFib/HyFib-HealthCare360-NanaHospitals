namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>The patient record a portal sign-in (mobile OTP) maps to.</summary>
public sealed record PortalAccount(Guid? PatientId, string DisplayName);

/// <summary>
/// Implemented by the Patients module: finds the patient registered with a mobile number so a portal account
/// can be linked on first sign-in. Returns <c>null</c> when no patient uses that number yet.
/// </summary>
public interface IPortalAccountResolver
{
    Task<PortalAccount?> ResolveAsync(string phoneE164, CancellationToken cancellationToken);
}

internal sealed class NullPortalAccountResolver : IPortalAccountResolver
{
    public Task<PortalAccount?> ResolveAsync(string phoneE164, CancellationToken cancellationToken) => Task.FromResult<PortalAccount?>(null);
}
