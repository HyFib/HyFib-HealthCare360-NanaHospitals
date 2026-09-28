using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>Charge source keys (IChargePoster is idempotent on source + reference, the reference being the item/study id).</summary>
public static class ChargeSources
{
    public const string Lab = "diagnostics.lab";
    public const string Radiology = "diagnostics.radiology";
    public const string Cardiology = "diagnostics.cardiology";
}

/// <summary>
/// Charge capture for diagnostics (MB-01) through the Billing module's <see cref="IChargePoster"/>. A billing failure never
/// blocks clinical work: the error is kept on the item/study and posting can be retried, and because posting is
/// idempotent a retry cannot double-charge. Health-checkup package components are never charged here: Billing charges
/// the package once. Called inside the caller's unit of work, including from ServiceOrderPlaced handlers.
/// </summary>
internal sealed class ChargeService(IChargePoster poster, DiagnosticsLookups lookups, ILogger<ChargeService> logger)
{
    private const int MaxErrorLength = 300;

    public async Task PostLabAsync(LabOrder order, LabOrderItem item, CancellationToken ct)
    {
        if (item.IsPackageComponent)
        {
            return;
        }

        if (item.ServiceItemId is not { } serviceItemId)
        {
            item.ChargeError = "No billable service is linked to this test.";
            return;
        }

        var result = await poster.PostAsync(new ChargeRequest
        {
            PatientId = order.PatientId,
            VisitId = order.VisitId,
            AdmissionId = order.AdmissionId,
            ServiceItemId = serviceItemId,
            Quantity = item.Quantity,
            Description = item.TestName,
            SourceModule = ChargeSources.Lab,
            SourceReference = item.Id.ToString(),
            PractitionerId = order.OrderingPractitionerId,
            DepartmentId = await lookups.DepartmentIdAsync(order.FacilityId, "LAB", ct),
            ServiceDate = order.OrderedAt,
        }, ct);

        (item.ChargeId, item.ChargeError) = Outcome(result, ChargeSources.Lab, item.Id);
    }

    public async Task PostImagingAsync(ImagingStudy study, CancellationToken ct)
    {
        if (study.IsPackageComponent)
        {
            return;
        }

        if (study.ServiceItemId is not { } serviceItemId)
        {
            study.ChargeError = "No billable service is linked to this study.";
            return;
        }

        var result = await poster.PostAsync(new ChargeRequest
        {
            PatientId = study.PatientId,
            VisitId = study.VisitId,
            AdmissionId = study.AdmissionId,
            ServiceItemId = serviceItemId,
            Quantity = study.Quantity,
            Description = study.StudyName,
            SourceModule = ChargeSources.Radiology,
            SourceReference = study.Id.ToString(),
            PractitionerId = study.OrderingPractitionerId,
            DepartmentId = await lookups.DepartmentIdAsync(study.FacilityId, "RAD", ct),
            ServiceDate = study.OrderedAt,
        }, ct);

        (study.ChargeId, study.ChargeError) = Outcome(result, ChargeSources.Radiology, study.Id);
    }

    public async Task PostCardiologyAsync(CardiologyStudy study, CancellationToken ct)
    {
        if (study.IsPackageComponent)
        {
            return;
        }

        if (study.ServiceItemId is not { } serviceItemId)
        {
            study.ChargeError = "No billable service is linked to this study.";
            return;
        }

        var result = await poster.PostAsync(new ChargeRequest
        {
            PatientId = study.PatientId,
            VisitId = study.VisitId,
            AdmissionId = study.AdmissionId,
            ServiceItemId = serviceItemId,
            Quantity = study.Quantity,
            Description = study.StudyName,
            SourceModule = ChargeSources.Cardiology,
            SourceReference = study.Id.ToString(),
            PractitionerId = study.OrderingPractitionerId,
            DepartmentId = await lookups.DepartmentIdAsync(study.FacilityId, "CDX", ct),
            ServiceDate = study.OrderedAt,
        }, ct);

        (study.ChargeId, study.ChargeError) = Outcome(result, ChargeSources.Cardiology, study.Id);
    }

    /// <summary>Reverses the charge of a cancelled test (package tests have none; Billing adjusts the package).</summary>
    public Task CancelLabAsync(LabOrderItem item, string reason, CancellationToken ct) =>
        item.IsPackageComponent ? Task.CompletedTask : CancelAsync(ChargeSources.Lab, item.Id, reason, ct);

    public Task CancelImagingAsync(ImagingStudy study, string reason, CancellationToken ct) =>
        study.IsPackageComponent ? Task.CompletedTask : CancelAsync(ChargeSources.Radiology, study.Id, reason, ct);

    public Task CancelCardiologyAsync(CardiologyStudy study, string reason, CancellationToken ct) =>
        study.IsPackageComponent ? Task.CompletedTask : CancelAsync(ChargeSources.Cardiology, study.Id, reason, ct);

    /// <summary>A failed reversal is logged for billing staff to reconcile; it never blocks the clinical cancellation.</summary>
    private async Task CancelAsync(string source, Guid reference, string reason, CancellationToken ct)
    {
        var result = await poster.CancelAsync(source, reference.ToString(), reason, ct);
        if (result.IsFailure)
        {
            logger.LogWarning("Could not cancel charge {Source}/{Reference}: {Error}", source, reference, result.Error!.Message);
        }
    }

    private (Guid? ChargeId, string? Error) Outcome(Result<Guid> result, string source, Guid reference)
    {
        if (result.IsSuccess)
        {
            return (result.Value, null);
        }

        logger.LogWarning("Charge posting for {Source}/{Reference} failed: {Error}", source, reference, result.Error!.Message);
        return (null, TextLimits.Clip(result.Error.Message, MaxErrorLength));
    }
}
