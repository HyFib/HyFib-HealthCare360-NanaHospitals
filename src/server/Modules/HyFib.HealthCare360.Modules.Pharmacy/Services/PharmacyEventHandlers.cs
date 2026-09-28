using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// ERX-03: a signed e-prescription lands in the pharmacy queue in the prescriber's own transaction (so a
/// prescription is never signed without being queued). Idempotent: a replayed event does not queue it twice.
/// </summary>
internal sealed class PrescriptionSignedHandler(HospitalDbContext db, IOutbox outbox, TimeProvider time) : IDomainEventHandler<PrescriptionSigned>
{
    public async Task HandleAsync(PrescriptionSigned prescription, CancellationToken cancellationToken)
    {
        if (prescription.Lines.Count == 0
            || db.Set<PharmacyQueueEntry>().Local.Any(q => q.PrescriptionId == prescription.PrescriptionId && q.RefillOfEntryId == null)
            || await db.Set<PharmacyQueueEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .AnyAsync(q => q.PrescriptionId == prescription.PrescriptionId && q.RefillOfEntryId == null, cancellationToken))
        {
            return;
        }

        var entry = PharmacyQueueEntry.FromPrescription(prescription, time.GetUtcNow());
        db.Add(entry);
        PharmacyRealtime.QueueChangedAfterCommit(outbox, entry);
    }
}

/// <summary>The prescriber withdrew the prescription: open entries stop, refills end and pending refill requests lapse.</summary>
internal sealed class PrescriptionCancelledHandler(HospitalDbContext db, IOutbox outbox, TimeProvider time) : IDomainEventHandler<PrescriptionCancelled>
{
    public async Task HandleAsync(PrescriptionCancelled cancelled, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var reason = string.IsNullOrWhiteSpace(cancelled.Reason) ? "Prescription cancelled by the prescriber" : cancelled.Reason.Trim();
        var entries = await db.Set<PharmacyQueueEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(q => q.Lines)
            .Where(q => q.PrescriptionId == cancelled.PrescriptionId)
            .ToListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            entry.RevokeRefills();
            if (entry.IsOpen && entry.CancelByPrescriber(reason, now).IsSuccess)
            {
                PharmacyRealtime.QueueChangedAfterCommit(outbox, entry);
            }
        }

        var entryIds = entries.Select(e => e.Id).ToList();
        var requests = await db.Set<RefillRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => entryIds.Contains(r.QueueEntryId) && r.Status == RefillStatus.Requested)
            .ToListAsync(cancellationToken);
        foreach (var request in requests)
        {
            request.Lapse(reason, now);
        }
    }
}

/// <summary>On discharge, pending ward indents are cancelled and open in-patient queue entries closed.</summary>
internal sealed class PatientDischargedHandler(HospitalDbContext db, IOutbox outbox, TimeProvider time) : IDomainEventHandler<PatientDischarged>
{
    public Task HandleAsync(PatientDischarged discharged, CancellationToken cancellationToken) =>
        InpatientPharmacyClosure.CloseAsync(db, outbox, discharged.AdmissionId, "Patient discharged", time.GetUtcNow(), cancellationToken);
}

/// <summary>A cancelled admission leaves nothing for the ward pharmacy to supply: the same clean-up as a discharge.</summary>
internal sealed class AdmissionCancelledHandler(HospitalDbContext db, IOutbox outbox, TimeProvider time) : IDomainEventHandler<AdmissionCancelled>
{
    public Task HandleAsync(AdmissionCancelled cancelled, CancellationToken cancellationToken) =>
        InpatientPharmacyClosure.CloseAsync(db, outbox, cancelled.AdmissionId, "Admission cancelled", time.GetUtcNow(), cancellationToken);
}

internal static class InpatientPharmacyClosure
{
    /// <summary>Cancels the admission's pending ward indents and closes its open in-patient queue entries.</summary>
    public static async Task CloseAsync(HospitalDbContext db, IOutbox outbox, Guid admissionId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var indents = await db.Set<WardIndent>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(i => i.Lines)
            .Where(i => i.AdmissionId == admissionId && (i.Status == WardIndentStatus.Pending || i.Status == WardIndentStatus.PartiallyIssued))
            .ToListAsync(cancellationToken);
        foreach (var indent in indents)
        {
            indent.Cancel(reason);
        }

        var entries = await db.Set<PharmacyQueueEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(q => q.Lines)
            .Where(q => q.AdmissionId == admissionId && q.IsInpatient
                        && (q.Status == QueueEntryStatus.Pending || q.Status == QueueEntryStatus.PartiallyDispensed))
            .ToListAsync(cancellationToken);
        foreach (var entry in entries.Where(e => e.Close(reason, now).IsSuccess))
        {
            PharmacyRealtime.QueueChangedAfterCommit(outbox, entry);
        }
    }
}

/// <summary>Duplicate patient records were merged: pharmacy history follows the surviving UHID (same transaction as the merge).</summary>
internal sealed class PatientsMergedHandler(HospitalDbContext db) : IDomainEventHandler<PatientsMerged>
{
    public async Task HandleAsync(PatientsMerged merged, CancellationToken cancellationToken)
    {
        var source = merged.SourcePatientId;
        var target = merged.TargetPatientId;
        string[] facility = [HospitalDbContext.FacilityFilter];

        foreach (var entry in await db.Set<PharmacyQueueEntry>().IgnoreQueryFilters(facility).Where(x => x.PatientId == source).ToListAsync(cancellationToken))
        {
            entry.PatientId = target;
        }

        foreach (var sale in await db.Set<PharmacySale>().IgnoreQueryFilters(facility).Where(x => x.PatientId == source).ToListAsync(cancellationToken))
        {
            sale.PatientId = target;
        }

        foreach (var saleReturn in await db.Set<PharmacySaleReturn>().IgnoreQueryFilters(facility).Where(x => x.PatientId == source).ToListAsync(cancellationToken))
        {
            saleReturn.PatientId = target;
        }

        foreach (var indent in await db.Set<WardIndent>().IgnoreQueryFilters(facility).Where(x => x.PatientId == source).ToListAsync(cancellationToken))
        {
            indent.PatientId = target;
        }

        foreach (var issue in await db.Set<IpIssue>().IgnoreQueryFilters(facility).Where(x => x.PatientId == source).ToListAsync(cancellationToken))
        {
            issue.PatientId = target;
        }

        foreach (var wardReturn in await db.Set<WardReturn>().IgnoreQueryFilters(facility).Where(x => x.PatientId == source).ToListAsync(cancellationToken))
        {
            wardReturn.PatientId = target;
        }

        foreach (var request in await db.Set<RefillRequest>().IgnoreQueryFilters(facility).Where(x => x.PatientId == source).ToListAsync(cancellationToken))
        {
            request.PatientId = target;
        }

        // Register lines keep the name/UHID printed at the time; only the link moves to the surviving record.
        foreach (var line in await db.Set<ControlledDrugRegisterEntry>().IgnoreQueryFilters(facility).Where(x => x.PatientId == source).ToListAsync(cancellationToken))
        {
            line.PatientId = target;
        }
    }
}
