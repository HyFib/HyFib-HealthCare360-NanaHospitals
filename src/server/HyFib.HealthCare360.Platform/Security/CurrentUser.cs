using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>JWT claim names issued by HC360 (inbound claim mapping is disabled, so these arrive verbatim).</summary>
public static class HcClaims
{
    public const string UserId = "sub";
    public const string UserName = "uname";
    public const string DisplayName = "dname";
    public const string Facility = "fac";
    public const string Session = "sid";
    public const string Device = "did";
    public const string Practitioner = "prac";
    public const string Patient = "pat";
    public const string Role = "role";
    public const string UserType = "typ";

    /// <summary>Present on tokens that may only complete a mandatory step (for example MFA enrolment).</summary>
    public const string Restriction = "rst";

    public const string StaffType = "staff";
    public const string PatientType = "patient";
}

/// <summary>The caller of the current request (or an unauthenticated/system context).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string? UserName { get; }
    string? DisplayName { get; }
    Guid? FacilityId { get; }
    Guid? SessionId { get; }
    Guid? DeviceId { get; }
    Guid? PractitionerId { get; }
    Guid? PatientId { get; }
    bool IsPatient { get; }
    IReadOnlyCollection<string> Roles { get; }
    string? IpAddress { get; }
    string? CorrelationId { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => ReadGuid(HcClaims.UserId);

    public string? UserName => Principal?.FindFirstValue(HcClaims.UserName);

    public string? DisplayName => Principal?.FindFirstValue(HcClaims.DisplayName);

    public Guid? FacilityId => ReadGuid(HcClaims.Facility);

    public Guid? SessionId => ReadGuid(HcClaims.Session);

    public Guid? DeviceId => ReadGuid(HcClaims.Device);

    public Guid? PractitionerId => ReadGuid(HcClaims.Practitioner);

    public Guid? PatientId => ReadGuid(HcClaims.Patient);

    public bool IsPatient => string.Equals(Principal?.FindFirstValue(HcClaims.UserType), HcClaims.PatientType, StringComparison.Ordinal);

    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll(HcClaims.Role).Select(c => c.Value).ToArray() ?? [];

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;

    private Guid? ReadGuid(string claim) =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(claim), out var value) ? value : null;
}
