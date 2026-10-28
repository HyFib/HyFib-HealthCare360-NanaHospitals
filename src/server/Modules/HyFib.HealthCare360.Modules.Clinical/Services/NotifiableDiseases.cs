using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Idsp;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>A released lab result, as the laboratory hands it over to be checked for a notifiable disease.</summary>
public sealed record NotifiableLabFinding(
    Guid FacilityId,
    Guid PatientId,
    Guid ResultId,
    string TestCode,
    string? Loinc,
    string TestName,
    string Result,
    string? Organism,
    DateTimeOffset ReleasedAt);

/// <summary>
/// Turns diagnoses and released positive results into notifiable cases (<see cref="NotifiableCase"/>), in the caller's
/// save, and alerts the surveillance officer through the outbox, after the commit: at once for an outbreak-prone IDSP
/// disease, and for each tuberculosis case, which is to be notified on Ni-kshay.
/// </summary>
public sealed class NotifiableDiseases(HospitalDbContext db, IOutbox outbox)
{
    internal const string AlertType = "clinical.idsp-alert";

    /// <summary>A diagnosis being recorded (not a differential) presumes a case of the disease its code is notified as.</summary>
    public async Task FromDiagnosisAsync(Guid facilityId, Diagnosis diagnosis, CancellationToken ct)
    {
        if (diagnosis.Type == DiagnosisType.Differential || IdspDiseases.ForIcd(diagnosis.IcdCode) is not { } disease)
        {
            return;
        }

        var found = await EpisodeAsync(facilityId, diagnosis.PatientId, disease, diagnosis.RecordedAt, ct);
        var @case = found ?? Add(facilityId, diagnosis.PatientId, disease, NotifiableCaseSource.Diagnosis);
        @case.Presume(diagnosis.Id, diagnosis.IcdCode, diagnosis.RecordedAt);
        @case.OnsetDate ??= diagnosis.OnsetDate;
        Alert(@case, disease, "suspected", diagnosis.RecordedAt);
    }

    /// <summary>A released positive result of a test that confirms a notifiable disease confirms (or opens) the case.</summary>
    public async Task FromLabResultAsync(NotifiableLabFinding finding, CancellationToken ct)
    {
        if (IdspDiseases.ForLabResult(finding.TestCode, finding.Loinc, finding.Organism) is not { } disease)
        {
            return;
        }

        var result = finding.Organism is { Length: > 0 } organism ? $"{finding.Result}: {organism}" : finding.Result;
        var found = await EpisodeAsync(finding.FacilityId, finding.PatientId, disease, finding.ReleasedAt, ct);
        var @case = found ?? Add(finding.FacilityId, finding.PatientId, disease, NotifiableCaseSource.LabResult);
        @case.Confirm(finding.ResultId, finding.TestName, result, finding.ReleasedAt);
        Alert(@case, disease, "confirmed", finding.ReleasedAt);
    }

    /// <summary>A confirming result was amended to negative: the case it confirmed leaves the L form.</summary>
    public async Task WithdrawLabResultAsync(Guid resultId, DateTimeOffset at, CancellationToken ct)
    {
        var cases = await Cases().Where(c => c.LabResultId == resultId).ToListAsync(ct);
        foreach (var @case in cases)
        {
            @case.WithdrawConfirmation("The confirming result was amended to negative.", at);
        }
    }

    internal static IdspAlert AlertFor(NotifiableCase @case, IdspDisease disease, string what) => disease.Nikshay
        ? new(@case.FacilityId, $"Notify on Ni-kshay: {disease.Name}",
            $"A case of {disease.Name} was recorded. Notify it on Ni-kshay, then record its Ni-kshay ID in HC360.",
            $"/mrd/idsp?case={@case.Id}")
        : new(@case.FacilityId, $"Notifiable disease: {disease.Name}",
            $"A {what} case of {disease.Name} was recorded. It is outbreak-prone: inform the district surveillance unit within 24 hours.",
            $"/mrd/idsp?case={@case.Id}");

    private void Alert(NotifiableCase @case, IdspDisease disease, string what, DateTimeOffset at)
    {
        if (!(disease.Immediate || disease.Nikshay) || @case.AlertedAt is not null)
        {
            return;
        }

        @case.MarkAlerted(at);
        outbox.Enqueue(AlertType, AlertFor(@case, disease, what), @case.FacilityId, expiresIn: TimeSpan.FromDays(2));
    }

    private NotifiableCase Add(Guid facilityId, Guid patientId, IdspDisease disease, NotifiableCaseSource source)
    {
        var @case = new NotifiableCase { FacilityId = facilityId, PatientId = patientId, DiseaseCode = disease.Code, Source = source };
        db.Add(@case);
        return @case;
    }

    // The patient's open case of the disease whose last event is within the episode; one added earlier in this save counts.
    private async Task<NotifiableCase?> EpisodeAsync(Guid facilityId, Guid patientId, IdspDisease disease, DateTimeOffset at, CancellationToken ct)
    {
        var since = at.AddDays(-disease.EpisodeDays);
        bool Matches(NotifiableCase c) =>
            c.FacilityId == facilityId && c.PatientId == patientId && c.DiseaseCode == disease.Code && c.Status == NotifiableCaseStatus.Open;

        var local = db.Set<NotifiableCase>().Local.Where(Matches).ToList();
        var stored = await Cases()
            .Where(c => c.FacilityId == facilityId && c.PatientId == patientId && c.DiseaseCode == disease.Code && c.Status == NotifiableCaseStatus.Open)
            .ToListAsync(ct);
        return local.Concat(stored).Distinct().Where(c => c.LastEventAt >= since).OrderByDescending(c => c.LastEventAt).FirstOrDefault();
    }

    private IQueryable<NotifiableCase> Cases() => db.Set<NotifiableCase>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);
}

/// <summary>The alert to the surveillance officers of a facility, delivered after the case is saved.</summary>
internal sealed record IdspAlert(Guid FacilityId, string Title, string Body, string Link);

internal sealed class IdspAlertOutboxHandler(INotificationService notifications) : IOutboxHandler
{
    public string Type => NotifiableDiseases.AlertType;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var alert = message.Read<IdspAlert>();
        return notifications.NotifyPermissionHoldersAsync(
            ClinicalPermissions.IdspReport, alert.FacilityId, alert.Title, alert.Body, alert.Link, "idsp", "critical", cancellationToken);
    }
}
