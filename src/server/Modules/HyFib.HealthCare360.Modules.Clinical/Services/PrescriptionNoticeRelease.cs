using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// "Prescription shared" messages held because the hospital required the doctor's DSC when the prescription was signed
/// (ERX-04, <see cref="Prescription.PatientNoticeHeldAt"/>), sent once the prescription can be issued without it: an
/// administrator made <c>clinical.prescriptions.signature</c> optional or off, for the facility or hospital-wide, before
/// the doctor signed it with their DSC (which sends the message in any case). Nothing announces a change of that setting,
/// which may be made hospital-wide, for one facility, or by removing a facility's own value, and which other servers see
/// within a minute; so the held messages are looked through every few minutes instead, each facility's against its
/// setting as it is then. Each goes once: sending it, or finding that the patient has no mobile number, lets it go, and
/// the prescription's row version keeps two servers (or the DSC arriving at the same moment) from sending it twice.
/// </summary>
public sealed class PrescriptionNoticeRelease(
    HospitalDbContext db,
    PrescriptionService prescriptions,
    PrescriptionSignatureSettings settings,
    TimeProvider time,
    ILogger<PrescriptionNoticeRelease> logger)
{
    /// <summary>
    /// A message held longer than this is not sent when the setting changes (the doctor's DSC still sends it). It tells the
    /// patient that a prescription from their visit is ready: a week on, the hospital pharmacy has dispensed it (its queue
    /// is never held back) or a usual out-patient course is over, and the message would puzzle more than help; the
    /// prescription is on the portal either way. A week also covers a DSC roll-out given up after a few days, the case
    /// this is for, and keeps each look to the last week's prescriptions.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>The most messages one look sends; the rest go on the next.</summary>
    public const int Batch = 200;

    /// <summary>Sends the messages held at the facilities that no longer require the DSC; returns how many were let go.</summary>
    public async Task<int> ReleaseAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow() - Window;
        var released = 0;
        foreach (var facilityId in await Held(since).Select(p => p.FacilityId).Distinct().ToListAsync(ct))
        {
            // A held prescription has no DSC signature (adding one sends the message), so it can be issued exactly when the
            // facility no longer requires one.
            var policy = await settings.PolicyAsync(facilityId, ct);
            if (!PrescriptionDigitalSignatureService.FinalCopyAvailable(policy, DigitalSignatureState.NotSigned))
            {
                continue;
            }

            var ids = await Held(since).Where(p => p.FacilityId == facilityId)
                .OrderBy(p => p.PatientNoticeHeldAt)
                .Select(p => p.Id)
                .Take(Batch - released)
                .ToListAsync(ct);
            foreach (var id in ids)
            {
                released += await ReleaseOneAsync(id, ct) ? 1 : 0;
            }

            if (released >= Batch)
            {
                break;
            }
        }

        return released;
    }

    // Loaded and saved one at a time: one that fails clears the unit of work, and must not take the others with it.
    private async Task<bool> ReleaseOneAsync(Guid id, CancellationToken ct)
    {
        var prescription = await db.Set<Prescription>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (prescription is not { Status: PrescriptionStatus.Signed, PatientNoticeHeldAt: not null, DigitalSignature: null })
        {
            return false;
        }

        try
        {
            await prescriptions.QueuePatientNoticeAsync(prescription, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Changed meanwhile (signed with the DSC, cancelled, let go by another server): the next look sees it as it is.
            logger.LogWarning(ex, "Could not send the held message of prescription {PrescriptionId}", prescription.Id);
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private IQueryable<Prescription> Held(DateTimeOffset since) =>
        db.Set<Prescription>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(p => p.PatientNoticeHeldAt != null && p.PatientNoticeHeldAt > since && p.Status == PrescriptionStatus.Signed);
}

/// <summary>
/// Looks through the held "prescription shared" messages every few minutes (<see cref="PrescriptionNoticeRelease"/>).
/// Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>).
/// </summary>
internal sealed class PrescriptionNoticeReleaseJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<PrescriptionNoticeReleaseJob> logger) : BackgroundService
{
    /// <summary>A setting changed reaches every server within a minute; the patient hears a few minutes after that.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PrescriptionNoticeRelease>().ReleaseAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sending the held prescription messages failed; it runs again on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
