using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Where the QR code of a printed prescription points (ERX-04). <see cref="Path"/> is the anonymous verification API
/// (server-relative, so it resolves only on the hospital LAN); <see cref="Url"/> is the patient portal's public
/// verification page under the hospital's public address (<c>{portal.baseUrl}/portal/verify/prescription/{token}</c>),
/// which calls that API and opens from any phone.
/// </summary>
public static class PrescriptionVerificationLinks
{
    public const string ApiPathPrefix = "/api/v1/public/prescriptions/verify/";

    public static string? Path(string? token) => token is null ? null : ApiPathPrefix + token;

    /// <summary>The anonymous download of the doctor's DSC signature as a .p7s (server-relative, like <see cref="Path"/>).</summary>
    public static string SignaturePath(string token) => ApiPathPrefix + Uri.EscapeDataString(token) + "/signature.p7s";

    /// <summary>Null until the prescription is signed (no token) or while <c>portal.baseUrl</c> is not configured.</summary>
    public static string? Url(string? baseUrl, string? token) =>
        baseUrl is null || token is null ? null : PortalAddress.Page(baseUrl, $"verify/prescription/{Uri.EscapeDataString(token)}");
}
