using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Audit trail for analytics (SEC-04, PRV-04). Every export is recorded (who, which report, inputs, format, rows). For
/// reports that list patients, every page viewed and every export also records one PHI-access entry per patient listed,
/// so a patient's access history shows that they appeared in a report. Entries are written in one batch.
/// </summary>
internal sealed class ReportAuditor(IAuditLogger audit, HospitalDbContext db)
{
    public async Task PageViewedAsync(ReportExecution execution, IReadOnlyCollection<Guid> patients, CancellationToken ct)
    {
        if (!execution.Report.ContainsPatientIdentifiers)
        {
            return;
        }

        audit.Add(new AuditEntry(AuditCategories.PhiAccess, "analytics.report.viewed", $"Viewed the report '{execution.Report.Title}' ({patients.Count} patients listed)")
        {
            EntityType = "Report",
            EntityId = execution.Report.Id,
            Severity = AuditSeverity.Warning,
            Details = Details(execution, null, patients.Count, "view"),
        });
        AddPatientEntries(execution, patients);
        await db.SaveChangesAsync(ct);
    }

    public async Task ExportedAsync(
        ReportExecution execution, ExportFormat format, int rows, ReportActor actor, string channel, IReadOnlyCollection<Guid> patients, CancellationToken ct)
    {
        var patientLevel = execution.Report.ContainsPatientIdentifiers;
        audit.Add(new AuditEntry(
            patientLevel ? AuditCategories.PhiAccess : AuditCategories.Data,
            "analytics.report.exported",
            $"Exported the report '{execution.Report.Title}' as {format.ToString().ToUpperInvariant()} ({rows} rows, {channel})")
        {
            EntityType = "Report",
            EntityId = execution.Report.Id,
            Severity = patientLevel ? AuditSeverity.Warning : AuditSeverity.Info,
            UserId = actor.UserId,
            UserName = actor.UserName,
            Details = Details(execution, format, rows, channel),
        });
        AddPatientEntries(execution, patients, actor);
        await db.SaveChangesAsync(ct);
    }

    private void AddPatientEntries(ReportExecution execution, IReadOnlyCollection<Guid> patients, ReportActor? actor = null)
    {
        foreach (var patientId in patients.Distinct())
        {
            audit.Add(new AuditEntry(AuditCategories.PhiAccess, "patient.accessed", $"Listed in the report '{execution.Report.Title}'")
            {
                PatientId = patientId,
                EntityType = "Report",
                EntityId = execution.Report.Id,
                UserId = actor?.UserId,
                UserName = actor?.UserName,
            });
        }
    }

    private static object Details(ReportExecution execution, ExportFormat? format, int rows, string channel) => new
    {
        reportId = execution.Report.Id,
        facility = execution.Facility.Name,
        parameters = execution.Arguments.ToCanonical(),
        from = execution.Report.HasPeriod ? execution.Arguments.From : (DateOnly?)null,
        to = execution.Report.HasPeriod ? execution.Arguments.To : (DateOnly?)null,
        format = format?.ToString(),
        rows,
        channel,
        dataSource = execution.DataSource,
    };
}
