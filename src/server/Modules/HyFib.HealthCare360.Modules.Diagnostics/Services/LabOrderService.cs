using System.Security.Cryptography;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Lab accessions (LIS-02). They are created from clinicians' orders (ServiceOrderPlaced; all lab items of one
/// clinical order share one accession) or at the lab counter for walk-in and outside-referral patients. Each ordered
/// test is placed on a tube, charged through Billing, and can be cancelled until its results are verified.
/// </summary>
internal sealed class LabOrderService(
    HospitalDbContext db,
    INumberSeries numbers,
    DiagnosticsLookups lookups,
    ChargeService charges,
    LabWorkflow workflow,
    DiagnosticsRealtime realtime,
    AnalyserOrders analyserOrders,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<LabOrderService> logger)
{
    // ------------------------------------------------------------------ clinical orders (inside the Clinical unit of work)

    /// <summary>
    /// Handles one lab item of a clinician's order. This runs inside the Clinical module's SaveChanges, so it neither
    /// saves nor opens a transaction, and it checks the change tracker as well as the database. Several items of one
    /// order arrive in the same unit of work, and a redelivered event must not create duplicates.
    /// </summary>
    public async Task CreateFromClinicalOrderAsync(ServiceOrderPlaced placed, CancellationToken ct)
    {
        if (db.Set<LabOrderItem>().Local.Any(i => i.SourceOrderItemId == placed.OrderItemId)
            || await db.Set<LabOrderItem>().AnyAsync(i => i.SourceOrderItemId == placed.OrderItemId, ct))
        {
            return;
        }

        var test = await ResolveTestAsync(placed, ct);
        var order = await FindOpenOrderAsync(placed.OrderId, ct);
        if (order is null)
        {
            var patientClass = await lookups.PatientClassAsync(placed.VisitId, placed.AdmissionId, ct);
            order = await NewOrderAsync(
                placed.FacilityId, placed.PatientId, placed.VisitId, placed.AdmissionId, patientClass, LabOrderSource.ClinicalOrder,
                placed.OrderingPractitionerId, null, placed.ClinicalNotes, placed.OrderedAt, ct);
            order.SourceOrderId = placed.OrderId;
            order.SourceOrderNumber = TextLimits.ClipOrNull(placed.OrderNumber, 32);
        }

        var item = await AddItemAsync(order, test, placed.Priority, placed.OrderItemId, placed.ServiceItemId, ct);

        // Health-checkup components are ordinary lab work; Billing prices the package, so they are never charged per test.
        item.PackageBookingId = placed.PackageBookingId;
        item.Quantity = Math.Max(1, placed.Quantity);
        await charges.PostLabAsync(order, item, ct);
        await RefreshReportAsync(order, ct);
        realtime.PublishAfterCommit(order.FacilityId, "lab.order.created", new { order.Id, order.AccessionNumber });
    }

    /// <summary>A clinician cancelled an ordered item. Returns false when the item is not a lab item.</summary>
    public async Task<bool> HandleClinicalCancelAsync(ServiceOrderCancelled cancelled, CancellationToken ct)
    {
        var item = db.Set<LabOrderItem>().Local.FirstOrDefault(i => i.SourceOrderItemId == cancelled.OrderItemId)
                   ?? await db.Set<LabOrderItem>().FirstOrDefaultAsync(i => i.SourceOrderItemId == cancelled.OrderItemId, ct);
        if (item is null)
        {
            return false;
        }

        var reason = string.IsNullOrWhiteSpace(cancelled.Reason) ? "Cancelled by the ordering clinician" : cancelled.Reason.Trim();
        var result = await CancelItemCoreAsync(item, TextLimits.Clip(reason, 300), ct);
        if (result.IsFailure)
        {
            // The clinician's cancellation stands in the Clinical module; the lab must review a test that already has verified results.
            logger.LogWarning("Clinical cancellation of lab item {ItemId} could not be applied: {Error}", item.Id, result.Error!.Message);
            realtime.AlertStaffAfterCommit(new DeferredStaffAlert(
                DiagnosticsPermissions.LabResultsVerify,
                item.FacilityId,
                $"Cancelled order already resulted: {item.TestName}",
                $"The clinician cancelled {item.TestName}, but {result.Error.Message} Review the report and the bill.",
                $"/lab/orders/{item.LabOrderId}",
                "warning"));
            return true;
        }

        realtime.PublishAfterCommit(item.FacilityId, "lab.order.cancelled", new { item.LabOrderId, ItemId = item.Id });
        return true;
    }

    // ------------------------------------------------------------------ lab counter (walk-in / outside referral)

    public async Task<Result<LabOrderCreated>> CreateManualAsync(CreateLabOrderRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var patient = await lookups.PatientAsync(request.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another UHID. Use the surviving record.");
        }

        if (!patient.IsActive || patient.IsDeceased)
        {
            return Error.Rule("patient.inactive", "Lab orders cannot be created for inactive or deceased patients.");
        }

        var ids = request.TestIds.Distinct().ToList();
        var tests = await db.Set<LabTest>().Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        if (tests.Count != ids.Count)
        {
            return Error.Field("testIds", "Some of the selected tests no longer exist.");
        }

        var notOrderable = tests.Where(t => !t.IsActive || !t.IsOrderable).Select(t => t.Name).ToList();
        if (notOrderable.Count > 0)
        {
            return Error.Field("testIds", $"These tests cannot be ordered: {string.Join(", ", notOrderable)}.");
        }

        if (request.OrderingPractitionerId is { } practitionerId && !await db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId && p.IsActive, ct))
        {
            return Error.Field("orderingPractitionerId", "Choose an active doctor.");
        }

        var patientClass = PatientClass.External;
        if (request.VisitId is { } visitId)
        {
            var visit = await db.Set<Visit>().Where(v => v.Id == visitId).Select(v => new { v.PatientId, v.Type, v.Status }).FirstOrDefaultAsync(ct);
            if (visit is null || visit.PatientId != patient.Id)
            {
                return Error.Field("visitId", "The visit does not belong to this patient.");
            }

            if (visit.Status == VisitStatus.Cancelled)
            {
                return Error.Rule("visit.cancelled", "The visit was cancelled.");
            }

            patientClass = PatientClasses.From(visit.Type, hasAdmission: false);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var order = await NewOrderAsync(
            facilityId, patient.Id, request.VisitId, null, patientClass, request.Source, request.OrderingPractitionerId, request.ReferringDoctorName,
            request.ClinicalNotes, time.GetUtcNow(), ct);

        var warnings = new List<string>();
        foreach (var test in ids.Select(id => tests.First(t => t.Id == id)))
        {
            var item = await AddItemAsync(order, test, request.Priority, null, null, ct);
            await charges.PostLabAsync(order, item, ct);
            if (item.ChargeError is not null)
            {
                warnings.Add($"{item.TestName}: {item.ChargeError}");
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.PublishAsync(facilityId, "lab.order.created", new { order.Id, order.AccessionNumber }, ct);

        var tubes = db.Set<LabSample>().Local.Where(s => s.LabOrderId == order.Id).OrderBy(s => s.Section).ToList();
        return new LabOrderCreated(
            order.Id,
            order.AccessionNumber,
            tubes.Select(s => new CreatedSample(s.Id, s.Barcode, s.Container, s.Section, order.Items.Where(i => i.SampleId == s.Id).Select(i => i.TestName).ToList())).ToList(),
            warnings);
    }

    public async Task<Result> CancelItemAsync(Guid itemId, string reason, CancellationToken ct)
    {
        var item = await db.Set<LabOrderItem>().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
        {
            return Error.NotFound(nameof(LabOrderItem), itemId);
        }

        var result = await CancelItemCoreAsync(item, reason.Trim(), ct);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(item.FacilityId, "lab.order.cancelled", new { item.LabOrderId, ItemId = item.Id }, ct);
        return Result.Success();
    }

    /// <summary>Posts charges that failed earlier (billing down or not yet configured). Idempotent in Billing.</summary>
    public async Task<Result<ChargeRetryOutcome>> RetryChargesAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Set<LabOrder>().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound(nameof(LabOrder), orderId);
        }

        var pending = order.Items.Where(i => i.IsActive && i.ChargeId is null && !i.IsPackageComponent).ToList();
        foreach (var item in pending)
        {
            await charges.PostLabAsync(order, item, ct);
        }

        await db.SaveChangesAsync(ct);
        return new ChargeRetryOutcome(
            pending.Count(i => i.ChargeId is not null),
            pending.Where(i => i.ChargeError is not null).Select(i => $"{i.TestName}: {i.ChargeError}").ToList());
    }

    public async Task<LabOrderDetail?> DetailAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Set<LabOrder>().AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return null;
        }

        var samples = await db.Set<LabSample>().AsNoTracking().Where(s => s.LabOrderId == orderId).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var report = await db.Set<LabReport>().AsNoTracking().Where(r => r.LabOrderId == orderId)
            .Select(r => new { r.Id, r.Status }).FirstOrDefaultAsync(ct);
        var patient = await lookups.PatientAsync(order.PatientId, ct);
        var doctors = await lookups.PractitionerNamesAsync([order.OrderingPractitionerId], ct);
        var users = await lookups.UserNamesAsync(samples.SelectMany(s => new[] { s.CollectedBy, s.ReceivedBy }), ct);
        var today = DiagnosticsTime.Today(time);

        return new LabOrderDetail(
            order.Id, order.AccessionNumber, order.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty,
            patient?.Gender ?? Gender.Unknown, patient?.AgeOn(today), order.VisitId, order.AdmissionId, order.PatientClass, order.Source,
            order.SourceOrderNumber, order.Priority, order.Status, order.OrderingPractitionerId,
            order.OrderingPractitionerId is { } pid ? doctors.GetValueOrDefault(pid) : null, order.ReferringDoctorName, order.ClinicalNotes,
            order.OrderedAt, order.CompletedAt, report?.Id, report?.Status,
            order.Items.OrderBy(i => i.Section).ThenBy(i => i.CreatedAt).Select(i => new LabOrderItemDto(
                i.Id, i.LabTestId, i.TestCode, i.TestName, i.Section, i.Status, i.Priority, i.SampleId, i.ServiceItemId, i.ChargeId, i.ChargeError,
                i.PackageBookingId, i.Quantity, i.ReferralLabId, i.SentOutAt, i.ExternalReportReceivedAt, i.VerifiedAt, i.ReleasedAt,
                i.CancelReason, i.ConsentBasis, i.ConsentRecordedAt)).ToList(),
            samples.Select(s => new LabSampleDto(
                s.Id, s.Barcode, s.SpecimenType, s.Container, s.Section, s.Priority, s.Status, s.CollectedAt,
                s.CollectedBy is { } c ? users.GetValueOrDefault(c) : null, s.CollectionSite, s.ReceivedAt,
                s.ReceivedBy is { } r ? users.GetValueOrDefault(r) : null, s.RejectionReason, s.RejectionRemarks, s.RejectedAt,
                s.RecollectionOfSampleId, s.RecollectedAsSampleId,
                order.Items.Where(i => i.SampleId == s.Id && i.IsActive).Select(i => i.TestName).ToList())).ToList(),
            order.Version);
    }

    // ------------------------------------------------------------------ shared building blocks

    private async Task<Result> CancelItemCoreAsync(LabOrderItem item, string reason, CancellationToken ct)
    {
        await db.Set<LabResult>().Where(r => r.LabOrderItemId == item.Id).LoadAsync(ct);
        var results = db.Set<LabResult>().Local.Where(r => r.LabOrderItemId == item.Id).ToList();
        if (results.Any(r => r.Status == LabResultStatus.Verified))
        {
            return Error.Rule("lab.item_partly_verified", $"some results of {item.TestName} are already verified.");
        }

        var now = time.GetUtcNow();
        var cancelled = item.Cancel(reason, now);
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        // Unverified values are discarded from worklists; the audit trail keeps them (soft delete).
        foreach (var result in results)
        {
            db.Remove(result);
        }

        if (item.SampleId is { } sampleId)
        {
            await db.Set<LabOrderItem>().Where(i => i.SampleId == sampleId).LoadAsync(ct);
            if (db.Set<LabOrderItem>().Local.Where(i => i.SampleId == sampleId).All(i => !i.IsActive))
            {
                var tube = db.Set<LabSample>().Local.FirstOrDefault(s => s.Id == sampleId)
                           ?? await db.Set<LabSample>().FirstOrDefaultAsync(s => s.Id == sampleId, ct);
                tube?.Cancel();
            }
        }

        await workflow.RefreshAsync([item.LabOrderId], now, ct);
        await charges.CancelLabAsync(item, reason, ct);

        // Analysers that were sent the test cancel it (in the caller's unit of work, the clinician's one included).
        await analyserOrders.TestCancelledAsync(item, ct);
        return Result.Success();
    }

    /// <summary>
    /// Finds the test set up for the ordered service. If none exists, a provisional free-text test is created and
    /// masters staff are alerted after commit. A clinician's order is never rejected or lost because the lab
    /// catalogue is incomplete.
    /// </summary>
    private async Task<LabTest> ResolveTestAsync(ServiceOrderPlaced placed, CancellationToken ct)
    {
        var test = await db.Set<LabTest>()
                       .Where(t => t.ServiceItemId == placed.ServiceItemId || t.Code == placed.ServiceCode)
                       .OrderByDescending(t => t.IsActive)
                       .ThenByDescending(t => t.IsOrderable)
                       .ThenByDescending(t => t.ServiceItemId == placed.ServiceItemId)
                       .FirstOrDefaultAsync(ct)
                   ?? db.Set<LabTest>().Local.FirstOrDefault(t => t.ServiceItemId == placed.ServiceItemId || t.Code == placed.ServiceCode);
        if (test is not null)
        {
            if (!test.IsActive || !test.IsOrderable)
            {
                logger.LogWarning("Service {ServiceCode} was ordered although lab test {TestCode} is inactive or not orderable", placed.ServiceCode, test.Code);
            }

            return test;
        }

        var codeTaken = await db.Set<LabTest>().IgnoreQueryFilters().AnyAsync(t => t.Code == placed.ServiceCode, ct);
        var provisional = new LabTest
        {
            Code = codeTaken
                ? $"{TextLimits.Clip(placed.ServiceCode, 22)}-P{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}"
                : TextLimits.Clip(placed.ServiceCode, 32),
            Name = TextLimits.Clip(placed.ServiceName, 200),
            Section = LabSection.Biochemistry,
            SpecimenType = SpecimenType.Serum,
            Container = ContainerType.Plain,
            TatHours = 24,
            ServiceItemId = placed.ServiceItemId,
            IsOrderable = true,
            ResultType = LabResultType.Text,
            AllowAutoValidation = false,
            IsProvisional = true,
        };
        db.Add(provisional);

        logger.LogWarning("No lab test is set up for service {ServiceCode}; created provisional test {TestCode}", placed.ServiceCode, provisional.Code);
        realtime.AlertStaffAfterCommit(new DeferredStaffAlert(
            DiagnosticsPermissions.LabMastersManage,
            placed.FacilityId,
            $"Set up lab test: {placed.ServiceName}",
            $"{placed.ServiceName} ({placed.ServiceCode}) was ordered but has no lab test set-up. A provisional free-text test was created so the order is not lost. Complete its specimen, container and reference ranges.",
            $"/lab/masters/tests/{provisional.Id}",
            "warning"));
        return provisional;
    }

    private async Task<LabOrder?> FindOpenOrderAsync(Guid sourceOrderId, CancellationToken ct) =>
        db.Set<LabOrder>().Local.FirstOrDefault(o => o.SourceOrderId == sourceOrderId && o.AcceptsNewItems)
        ?? await db.Set<LabOrder>().Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.SourceOrderId == sourceOrderId && (o.Status == LabOrderStatus.Ordered || o.Status == LabOrderStatus.InProgress), ct);

    private async Task<LabOrder> NewOrderAsync(
        Guid facilityId, Guid patientId, Guid? visitId, Guid? admissionId, PatientClass patientClass, LabOrderSource source,
        Guid? orderingPractitionerId, string? referringDoctorName, string? clinicalNotes, DateTimeOffset orderedAt, CancellationToken ct)
    {
        var order = new LabOrder
        {
            FacilityId = facilityId,
            AccessionNumber = await numbers.NextAsync(DiagnosticsNumberSeries.LabAccession, facilityId, ct),
            PatientId = patientId,
            VisitId = visitId,
            AdmissionId = admissionId,
            PatientClass = patientClass,
            Source = source,
            OrderingPractitionerId = orderingPractitionerId,
            ReferringDoctorName = TextLimits.ClipOrNull(referringDoctorName, 200),
            ClinicalNotes = TextLimits.ClipOrNull(clinicalNotes, 1000),
            OrderedAt = orderedAt,
            Status = LabOrderStatus.Ordered,
        };
        db.Add(order);
        db.Add(new LabReport
        {
            FacilityId = facilityId,
            LabOrderId = order.Id,
            PatientId = patientId,
            ReportNumber = order.AccessionNumber,
            Status = LabReportStatus.Draft,
        });
        return order;
    }

    private async Task<LabOrderItem> AddItemAsync(
        LabOrder order, LabTest test, OrderPriority priority, Guid? sourceOrderItemId, Guid? serviceItemId, CancellationToken ct)
    {
        var referralLabId = test.DefaultReferralLabId;
        var key = SamplePlanner.KeyFor(test, referralLabId);
        await db.Set<LabSample>().Where(s => s.LabOrderId == order.Id && s.Status == SampleStatus.Pending).LoadAsync(ct);
        var tube = SamplePlanner.FindTube(db.Set<LabSample>().Local.Where(s => s.LabOrderId == order.Id), key)
                   ?? await NewTubeAsync(order, key, priority, ct);
        if (priority > tube.Priority)
        {
            tube.Priority = priority;
        }

        var item = new LabOrderItem
        {
            LabTestId = test.Id,
            TestCode = test.Code,
            TestName = test.Name,
            Section = test.Section,
            SampleId = tube.Id,
            SourceOrderItemId = sourceOrderItemId,
            ServiceItemId = serviceItemId ?? test.ServiceItemId,
            Priority = priority,
            Status = LabOrderItemStatus.Ordered,
            ReferralLabId = referralLabId,
        };
        order.AddItem(item);
        db.Add(item);
        return item;
    }

    private async Task<LabSample> NewTubeAsync(LabOrder order, SampleKey key, OrderPriority priority, CancellationToken ct)
    {
        var tube = new LabSample
        {
            FacilityId = order.FacilityId,
            LabOrderId = order.Id,
            PatientId = order.PatientId,
            Barcode = await numbers.NextAsync(DiagnosticsNumberSeries.LabSample, null, ct),
            SpecimenType = key.Specimen,
            Container = key.Container,
            Section = key.Section,
            ReferralLabId = key.ReferralLabId,
            DedicatedTestId = key.DedicatedTestId,
            Priority = priority,
            Status = SampleStatus.Pending,
        };
        db.Add(tube);
        return tube;
    }

    private async Task RefreshReportAsync(LabOrder order, CancellationToken ct)
    {
        order.RefreshStatus(time.GetUtcNow());
        var report = db.Set<LabReport>().Local.FirstOrDefault(r => r.LabOrderId == order.Id)
                     ?? await db.Set<LabReport>().FirstOrDefaultAsync(r => r.LabOrderId == order.Id, ct);
        report?.Refresh(order.Items);
    }
}
