namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Portal;

/// <summary>A released lab, radiology or cardiology report visible to the signed-in patient (PP-02).</summary>
public sealed record PortalReportItem(
    DiagnosticKind Kind,
    Guid Id,
    Guid PatientId,
    string PatientName,
    string Title,
    string Reference,
    DateTimeOffset ReleasedAt,
    bool IsAmended,
    bool IsPartial);
