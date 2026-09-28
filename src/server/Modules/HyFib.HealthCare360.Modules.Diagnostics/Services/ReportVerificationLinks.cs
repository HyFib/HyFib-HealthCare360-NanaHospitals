using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Where the QR code of a printed report points (LIS-07). The server-relative <c>VerificationPath</c> is the anonymous
/// verification API and resolves only on the hospital LAN; the absolute URL built here is the patient portal's public
/// verification page under the hospital's public address (<c>{portal.baseUrl}/portal/verify/{lab|radiology|cardiology}/{code}</c>),
/// which calls that API and opens from any phone.
/// </summary>
internal static class ReportVerificationLinks
{
    /// <summary>Route segment of the portal page, matching the API prefix (<c>/api/v1/{segment}/reports/verify/</c>).</summary>
    public static string Segment(DiagnosticKind kind) => kind switch
    {
        DiagnosticKind.Laboratory => "lab",
        DiagnosticKind.Radiology => "radiology",
        DiagnosticKind.Cardiology => "cardiology",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown report kind."),
    };

    /// <summary>Null while the report has no code (not yet released) or <c>portal.baseUrl</c> is not configured.</summary>
    public static string? Url(string? baseUrl, DiagnosticKind kind, string? code) =>
        baseUrl is null || code is null ? null : PortalAddress.Page(baseUrl, $"verify/{Segment(kind)}/{Uri.EscapeDataString(code)}");
}
