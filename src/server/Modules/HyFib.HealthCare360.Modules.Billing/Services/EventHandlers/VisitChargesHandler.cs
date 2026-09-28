using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;

/// <summary>
/// Automatic OP charges on check-in (MB-01, PMS-04): the consultation of an OP, tele-consultation or emergency visit
/// with a doctor (CONS-NEW / CONS-FU / CONS-TELE / CONS-ER at the doctor's fee, a negotiated payer tariff winning, free
/// follow-ups at zero) and the registration fee (REG-NEW) on a patient's first visit. Runs inside the visit's
/// transaction, so a visit never exists without its charges; retries are harmless because charge posting is idempotent.
/// </summary>
internal sealed class VisitChargesHandler(
    HospitalDbContext db,
    ChargeService charges,
    TariffService tariffs,
    ISettingsService settings,
    ILogger<VisitChargesHandler> logger) : IDomainEventHandler<VisitCreated>
{
    /// <summary>Charge REG-NEW on a patient's first visit (default true).</summary>
    public const string RegistrationFeeSetting = "billing.registrationFee.enabled";

    public async Task HandleAsync(VisitCreated created, CancellationToken cancellationToken)
    {
        var visit = await db.Set<Visit>().FindAsync([created.VisitId], cancellationToken);
        if (visit is null)
        {
            return;
        }

        await ChargeRegistrationAsync(visit, cancellationToken);
        await ChargeConsultationAsync(visit, cancellationToken);
    }

    private async Task ChargeRegistrationAsync(Visit visit, CancellationToken ct)
    {
        // A fee left unbilled when the patient's first visit was cancelled moves to this visit (it is charged once, ever).
        var earlier = await charges.FindBySourceAsync(ChargeService.RegistrationSource, visit.PatientId.ToString("N"), ct);
        if (earlier is not null)
        {
            if (earlier is { IsAvailableForBilling: true, VisitId: null, AdmissionId: null } && earlier.FacilityId == visit.FacilityId)
            {
                earlier.VisitId = visit.Id;
            }

            return;
        }

        if (!(await settings.GetAsync<bool?>(RegistrationFeeSetting, visit.FacilityId, ct) ?? true))
        {
            return;
        }

        if (await db.Set<Visit>().IgnoreQueryFilters().AnyAsync(v => v.PatientId == visit.PatientId && v.Id != visit.Id, ct))
        {
            return;
        }

        var item = await ItemAsync("REG-NEW", ct);
        if (item is null)
        {
            return;
        }

        var result = await charges.StageAsync(new ChargeRequest
        {
            PatientId = visit.PatientId,
            VisitId = visit.Id,
            ServiceItemId = item.Id,
            SourceModule = ChargeService.RegistrationSource,
            SourceReference = visit.PatientId.ToString("N"),
            DepartmentId = item.DepartmentId,
            ServiceDate = visit.VisitedAt,
        }, isFreeFollowUp: false, ct);
        if (result.IsFailure)
        {
            logger.LogWarning("Registration fee for visit {VisitNumber} was not charged: {Error}", visit.VisitNumber, result.Error!.Message);
        }
    }

    private async Task ChargeConsultationAsync(Visit visit, CancellationToken ct)
    {
        if (visit.PractitionerId is not { } practitionerId || visit.Type is not (VisitType.OutPatient or VisitType.Telehealth or VisitType.Emergency))
        {
            return;
        }

        var code = visit.Type switch
        {
            VisitType.Emergency => "CONS-ER",
            VisitType.Telehealth => "CONS-TELE",
            _ => visit.IsFollowUp ? "CONS-FU" : "CONS-NEW",
        };
        var item = await ItemAsync(code, ct);
        if (item is null)
        {
            return;
        }

        var fees = await db.Set<Practitioner>().Where(p => p.Id == practitionerId)
            .Select(p => new { p.ConsultationFeeNew, p.ConsultationFeeFollowUp })
            .FirstOrDefaultAsync(ct);
        var fee = visit.IsFollowUp ? fees?.ConsultationFeeFollowUp ?? 0m : fees?.ConsultationFeeNew ?? 0m;
        var plan = await tariffs.ResolvePlanAsync(visit.PayerType, visit.PayerId, visit.FacilityId, ct);
        var payerPlanHasPrice = plan is { Type: not TariffType.General }
                                && await tariffs.HasPlanPriceAsync(plan.Id, item.Id, BillingTime.LocalDate(visit.VisitedAt), ct);

        var result = await charges.StageAsync(new ChargeRequest
        {
            PatientId = visit.PatientId,
            VisitId = visit.Id,
            ServiceItemId = item.Id,
            UnitPriceOverride = visit.IsFreeFollowUp ? null : PriceResolver.ConsultationOverride(payerPlanHasPrice, fee),
            SourceModule = ChargeService.ConsultationSource,
            SourceReference = visit.Id.ToString("N"),
            PractitionerId = practitionerId,
            DepartmentId = visit.DepartmentId ?? item.DepartmentId,
            ServiceDate = visit.VisitedAt,
        }, isFreeFollowUp: visit.IsFreeFollowUp, ct);
        if (result.IsFailure)
        {
            logger.LogWarning("Consultation for visit {VisitNumber} was not charged: {Error}", visit.VisitNumber, result.Error!.Message);
        }
    }

    private Task<ServiceItem?> ItemAsync(string code, CancellationToken ct) =>
        db.Set<ServiceItem>().AsNoTracking().FirstOrDefaultAsync(s => s.Code == code && s.IsActive, ct);
}
