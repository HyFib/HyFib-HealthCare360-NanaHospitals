using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// A record to link to its patient's ABHA without staff: the outbox job's payload, and how many times it has waited
/// (<see cref="AbdmAutoLinking.WaitAfter"/>).
/// </summary>
internal sealed record AbdmAutoLinkJob(AbdmRecordType Type, Guid RecordId, Guid PatientId, int Waits = 0);

/// <summary>What became of one automatic link.</summary>
internal enum AbdmAutoLinkOutcome
{
    /// <summary>A link request was made (whatever the gateway then answers).</summary>
    Requested,

    /// <summary>Nothing to link: the record or patient does not qualify, or the record is linked or awaiting an answer already.</summary>
    NothingToDo,

    /// <summary>Not yet: try again after <see cref="AbdmAutoLinkResult.RetryAfter"/>.</summary>
    Wait,
}

/// <param name="FirstWait">For a wait, how long the first is; each after it twice as long, up to <paramref name="LongestWait"/>.</param>
internal sealed record AbdmAutoLinkResult(AbdmAutoLinkOutcome Outcome, string Reason, TimeSpan FirstWait = default, TimeSpan LongestWait = default)
{
    public static AbdmAutoLinkResult Requested(AbdmLinkView view) => new(AbdmAutoLinkOutcome.Requested, $"Request {view.Id:D}: {view.Status}.");

    public static AbdmAutoLinkResult Nothing(string reason) => new(AbdmAutoLinkOutcome.NothingToDo, reason);

    public static AbdmAutoLinkResult Wait(TimeSpan first, TimeSpan longest, string reason) => new(AbdmAutoLinkOutcome.Wait, reason, first, longest);
}

/// <summary>
/// Automatic HIP-initiated linking (ABDM M2): the record a clinical event completes is linked to the patient's ABHA
/// address as it happens, the way staff link it (<see cref="AbdmLinkingService"/>), so the patient sees it in their PHR
/// app without anyone at the hospital doing anything:
/// <list type="bullet">
/// <item>an out-patient consultation note signed (<see cref="EncounterSigned"/>, not an in-patient note: the stay's
/// discharge summary carries it), as an OP consultation;</item>
/// <item>a discharge summary signed (<see cref="DischargeSummarySigned"/>);</item>
/// <item>a lab or radiology report released (<see cref="DiagnosticReportReleased"/>; a lab report raises one per test
/// released, and is queued once);</item>
/// <item>an out-patient prescription signed (<see cref="PrescriptionSigned"/>), linked once it may be issued: where the
/// hospital requires the doctor's DSC, once signed with it.</item>
/// </list>
/// The event's handler (<see cref="AbdmAutoLinkTriggers"/>) only queues an outbox job, in the event's own transaction,
/// and only while ABDM health records and the gateway are on, automatic linking is (<c>autoLink</c> in
/// <c>abdm.gateway</c>, on by default), and the patient has an ABHA address and a date of birth; nothing it does can
/// fail the signature. The job (<see cref="AbdmAutoLinkHandler"/>) links the record, or waits, or finds nothing to do.
/// No user acts, so no one's access to the chart is checked: a VIP patient's records, and those of a record in a
/// sensitive category (HIV, mental health, MTP), are not linked automatically, as they are not listed to readers who
/// may not open them (<see cref="PatientSearchQuery"/>). Staff link them.
/// </summary>
internal static class AbdmAutoLinking
{
    public const string MessageType = "abdm.link_record";

    /// <summary>
    /// The outbox provider of the ABDM gateway, for linking and for transfers (<see cref="IOutboxHandler.Provider"/>): their
    /// failures to reach it count together, and when it cools down, both wait.
    /// </summary>
    public const string OutboxProvider = "abdm";

    /// <summary>How long a queued record keeps being tried, its waits for a DSC or a link token included.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(2);

    /// <summary>The first wait while a link token for the patient's ABHA address is being asked for (it comes in seconds).</summary>
    public static readonly TimeSpan TokenWait = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait for a link token: a request the gateway never answers is failed by the sweeper soon after.</summary>
    public static readonly TimeSpan LongestTokenWait = TimeSpan.FromMinutes(5);

    /// <summary>The first wait while a signed prescription waits for the doctor's DSC before it may be issued.</summary>
    public static readonly TimeSpan SignatureWait = TimeSpan.FromMinutes(10);

    /// <summary>The longest wait for a DSC, which may come at the end of the doctor's day, or never.</summary>
    public static readonly TimeSpan LongestSignatureWait = TimeSpan.FromHours(2);

    /// <summary>
    /// How long a job waits after it has waited <paramref name="waits"/> times: <paramref name="first"/>, then twice as long
    /// each time, up to <paramref name="longest"/>. Each wait is a new outbox message, kept as the outbox keeps every
    /// message, so a prescription whose DSC never comes leaves some thirty of them in its two days rather than hundreds.
    /// </summary>
    public static TimeSpan WaitAfter(int waits, TimeSpan first, TimeSpan longest) =>
        waits >= 16 || first.Ticks << Math.Max(waits, 0) >= longest.Ticks ? longest : TimeSpan.FromTicks(first.Ticks << Math.Max(waits, 0));

    /// <summary>
    /// The record the event completed, as it is linked: its kind and id, its patient, and the facility it was made in
    /// where the event says; null for an event that links nothing (an in-patient note, an in-patient prescription, a
    /// cardiology report).
    /// </summary>
    public static (AbdmRecordType Type, Guid Id, Guid PatientId, Guid? FacilityId)? Completed(IDomainEvent completed) => completed switch
    {
        EncounterSigned e when e.AdmissionId is null && e.Type != EncounterType.InPatient => (AbdmRecordType.OpConsultation, e.EncounterId, e.PatientId, e.FacilityId),
        DischargeSummarySigned e => (AbdmRecordType.DischargeSummary, e.DischargeSummaryId, e.PatientId, e.FacilityId),
        DiagnosticReportReleased { Kind: DiagnosticKind.Laboratory } e => (AbdmRecordType.LabReport, e.ReportId, e.PatientId, null),
        DiagnosticReportReleased { Kind: DiagnosticKind.Radiology } e => (AbdmRecordType.RadiologyReport, e.ReportId, e.PatientId, null),
        PrescriptionSigned { IsInpatient: false } e => (AbdmRecordType.Prescription, e.PrescriptionId, e.PatientId, e.FacilityId),
        _ => null,
    };

    /// <summary>Why a patient's records are not linked automatically, or null when they are.</summary>
    public static string? NotLinkedAutomatically(string? abhaAddress, DateOnly? dateOfBirth, Guid? mergedInto, bool isVip, SensitiveCategory categories) =>
        string.IsNullOrWhiteSpace(abhaAddress) ? "The patient has no ABHA address."
        : dateOfBirth is null ? "The patient has no date of birth, which ABDM needs to link records."
        : mergedInto is not null ? "The patient's records were merged into another UHID."
        : isVip || categories != SensitiveCategory.None ? "The record is restricted or in a sensitive category: staff link it."
        : null;

    /// <summary>Whether automatic linking is on: the gateway on and set up, and its <c>autoLink</c> switch on.</summary>
    public static async Task<string?> OffAsync(AbdmGatewayConfiguration configuration, DisabledAbdmGatewayClient refusal, CancellationToken ct)
    {
        if (await refusal.WhyNotAsync(ct) is { } off)
        {
            return off.Message;
        }

        var (settings, _, _) = await configuration.ReadAsync(ct);
        return settings.AutoLink ? null : $"Automatic linking is switched off (\"autoLink\" in the {AbdmGatewaySettingKeys.Gateway} setting).";
    }
}

/// <summary>
/// Queues the record a clinical event completed (<see cref="AbdmAutoLinking"/>). It runs inside the event's
/// transaction, before the signature commits, so it only reads and enqueues: it calls nothing outside, and what it
/// queues commits with the signature or not at all.
/// </summary>
internal sealed class AbdmAutoLinkTriggers(
    HospitalDbContext db,
    IOutbox outbox,
    IFeatureFlags features,
    AbdmGatewayConfiguration configuration,
    DisabledAbdmGatewayClient refusal)
    : IDomainEventHandler<EncounterSigned>,
      IDomainEventHandler<DischargeSummarySigned>,
      IDomainEventHandler<DiagnosticReportReleased>,
      IDomainEventHandler<PrescriptionSigned>
{
    public Task HandleAsync(EncounterSigned domainEvent, CancellationToken cancellationToken) => QueueAsync(domainEvent, cancellationToken);

    public Task HandleAsync(DischargeSummarySigned domainEvent, CancellationToken cancellationToken) => QueueAsync(domainEvent, cancellationToken);

    public Task HandleAsync(DiagnosticReportReleased domainEvent, CancellationToken cancellationToken) => QueueAsync(domainEvent, cancellationToken);

    public Task HandleAsync(PrescriptionSigned domainEvent, CancellationToken cancellationToken) => QueueAsync(domainEvent, cancellationToken);

    private async Task QueueAsync(IDomainEvent completed, CancellationToken ct)
    {
        if (AbdmAutoLinking.Completed(completed) is not { } record)
        {
            return;
        }

        // A report's release names no facility, and the report's is not always the releasing user's: ABDM may be switched
        // off for one facility while it is on for the hospital.
        var facilityId = record.FacilityId ?? await ReportFacilityAsync(record.Type, record.Id, ct);
        if (!await features.IsEnabledAsync(FeatureKeys.Abdm, facilityId, ct)
            || await AbdmAutoLinking.OffAsync(configuration, refusal, ct) is not null)
        {
            return;
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == record.PatientId)
            .Select(p => new { p.AbhaAddress, p.DateOfBirth, p.MergedIntoPatientId, p.IsVip, p.SensitiveCategories })
            .FirstOrDefaultAsync(ct);
        if (patient is null
            || AbdmAutoLinking.NotLinkedAutomatically(patient.AbhaAddress, patient.DateOfBirth, patient.MergedIntoPatientId, patient.IsVip, patient.SensitiveCategories) is not null
            || Queued(record.Type, record.Id))
        {
            return;
        }

        outbox.Enqueue(AbdmAutoLinking.MessageType, new AbdmAutoLinkJob(record.Type, record.Id, record.PatientId), facilityId,
            expiresIn: AbdmAutoLinking.Lifetime);
    }

    /// <summary>
    /// The facility of a released lab or radiology report: the report raised the event, so it is tracked; it was saved
    /// before it was released, so failing that the database has it, whatever facility the releasing user works in.
    /// </summary>
    private async Task<Guid?> ReportFacilityAsync(AbdmRecordType type, Guid id, CancellationToken ct)
    {
        var tracked = type switch
        {
            AbdmRecordType.LabReport => db.ChangeTracker.Entries<LabReport>().FirstOrDefault(e => e.Entity.Id == id)?.Entity.FacilityId,
            AbdmRecordType.RadiologyReport => db.ChangeTracker.Entries<RadiologyReport>().FirstOrDefault(e => e.Entity.Id == id)?.Entity.FacilityId,
            _ => null,
        };
        if (tracked is { } facility && facility != Guid.Empty)
        {
            return facility;
        }

        return type switch
        {
            AbdmRecordType.LabReport => await db.Set<LabReport>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(r => r.Id == id).Select(r => (Guid?)r.FacilityId).FirstOrDefaultAsync(ct),
            AbdmRecordType.RadiologyReport => await db.Set<RadiologyReport>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(r => r.Id == id).Select(r => (Guid?)r.FacilityId).FirstOrDefaultAsync(ct),
            _ => null,
        };
    }

    /// <summary>Queued already in this transaction: a lab report releases its tests together, an event each.</summary>
    private bool Queued(AbdmRecordType type, Guid id) =>
        db.ChangeTracker.Entries<OutboxMessage>()
            .Where(e => e.State == EntityState.Added && e.Entity.Type == AbdmAutoLinking.MessageType)
            .Select(e => e.Entity.Read<AbdmAutoLinkJob>())
            .Any(job => job.Type == type && job.RecordId == id);
}

/// <summary>
/// Links a queued record (<see cref="AbdmLinkingService.LinkAutomaticallyAsync"/>) in a scope of its own. The
/// dispatcher handles each message in a transaction that commits once its handler is done, and linking saves each step
/// before its gateway call so that the gateway's answer, which may come back within a second, finds it: inside the
/// message's transaction the request would not be committed until the linking was done, and the answer would find
/// nothing. A wait queues the record again
/// for later, within the message's lifetime; anything else ends the job. A refusal is not retried here: the request
/// shows it, and staff link the record again (the gateway may have acted on a call whose answer was lost).
/// </summary>
internal sealed class AbdmAutoLinkHandler(
    IServiceScopeFactory scopes,
    IOutbox outbox,
    IFeatureFlags features,
    TimeProvider time,
    ILogger<AbdmAutoLinkHandler> log) : IOutboxHandler
{
    public string Type => AbdmAutoLinking.MessageType;

    public string Provider => AbdmAutoLinking.OutboxProvider;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var job = message.Read<AbdmAutoLinkJob>();
        if (!await features.IsEnabledAsync(FeatureKeys.Abdm, message.FacilityId, cancellationToken))
        {
            log.LogInformation("ABDM health records were switched off: {Type} {RecordId} is not linked automatically", job.Type, job.RecordId);
            return;
        }

        AbdmAutoLinkResult result;
        await using (var scope = scopes.CreateAsyncScope())
        using (IntegrationTrace.ForFacility(message.FacilityId))
        {
            result = await scope.ServiceProvider.GetRequiredService<AbdmLinkingService>().LinkAutomaticallyAsync(job.Type, job.RecordId, cancellationToken);
        }

        var wait = AbdmAutoLinking.WaitAfter(job.Waits, result.FirstWait, result.LongestWait);
        switch (result.Outcome)
        {
            case AbdmAutoLinkOutcome.Wait when OutboxRetryPolicy.ExpiryOf(message) - time.GetUtcNow() is var left && left > wait:
                outbox.Enqueue(Type, job with { Waits = job.Waits + 1 }, message.FacilityId, delay: wait, expiresIn: left);
                log.LogDebug("Linking {Type} {RecordId} automatically waits {Wait}: {Reason}", job.Type, job.RecordId, wait, result.Reason);
                break;
            case AbdmAutoLinkOutcome.Wait:
                log.LogWarning("Gave up linking {Type} {RecordId} to the patient's ABHA automatically: {Reason}", job.Type, job.RecordId, result.Reason);
                break;
            case AbdmAutoLinkOutcome.Requested:
                log.LogInformation("Linking {Type} {RecordId} to the patient's ABHA automatically: {Reason}", job.Type, job.RecordId, result.Reason);
                break;
            default:
                log.LogDebug("{Type} {RecordId} is not linked automatically: {Reason}", job.Type, job.RecordId, result.Reason);
                break;
        }
    }
}
