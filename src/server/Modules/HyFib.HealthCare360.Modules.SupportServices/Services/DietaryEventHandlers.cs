using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

// Handlers run inside the raising module's unit of work (before commit): they only stage changes, never save, and
// ignore events they cannot act on, so a dietary concern never blocks a clinical order, a discharge or a merge.

/// <summary>
/// A clinician's diet order (ServiceOrderPlaced, category Dietary) becomes the admission's diet order. The service code
/// is matched to a diet type ("DIET-DIABETIC" → DIABETIC); without a match the order starts as a normal diet flagged for
/// the dietitian, and preferences/allergies are carried over from the previous order when there is one.
/// </summary>
internal sealed class DietServiceOrderPlacedHandler(HospitalDbContext db, MealPlanService mealPlans, TimeProvider time) : IDomainEventHandler<ServiceOrderPlaced>
{
    public async Task HandleAsync(ServiceOrderPlaced domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.Category != ServiceCategory.Dietary || domainEvent.AdmissionId is not { } admissionId
            || await db.Set<DietOrder>().AnyAsync(o => o.ServiceOrderItemId == domainEvent.OrderItemId, cancellationToken))
        {
            return;
        }

        var admission = await db.Set<Admission>().AsNoTracking()
            .Where(a => a.Id == admissionId && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated))
            .Select(a => new { a.Id, a.FacilityId, a.PatientId })
            .FirstOrDefaultAsync(cancellationToken);
        if (admission is null)
        {
            return;
        }

        var candidates = DietCodes.Candidates(domainEvent.ServiceCode);
        var matched = await db.Set<DietType>().AsNoTracking()
            .Where(d => d.IsActive && candidates.Contains(d.Code))
            .FirstOrDefaultAsync(cancellationToken);
        var dietType = matched ?? await db.Set<DietType>().AsNoTracking().FirstOrDefaultAsync(d => d.IsActive && d.Code == DietCodes.Normal, cancellationToken);
        if (dietType is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var today = IndiaClock.LocalDate(now);
        var current = await db.Set<DietOrder>()
            .Where(o => o.AdmissionId == admissionId && o.Status == DietOrderStatus.Active)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);
        var previous = current.FirstOrDefault();
        current.ForEach(o => o.Supersede(today, now));

        var instructions = string.IsNullOrWhiteSpace(domainEvent.ClinicalNotes) ? domainEvent.ServiceName : domainEvent.ClinicalNotes;
        var order = new DietOrder
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            DietTypeId = dietType.Id,
            Preference = previous?.Preference ?? FoodPreference.Vegetarian,
            Restrictions = previous?.Restrictions.ToList() ?? [],
            AllergyNotes = previous?.AllergyNotes,
            Instructions = instructions.Length <= 500 ? instructions : instructions[..500],
            FluidRestrictionMl = previous?.FluidRestrictionMl,
            CalorieTargetKcal = previous?.CalorieTargetKcal,
            MealSlots = dietType.DefaultMealSlots,
            StartDate = today,
            Status = DietOrderStatus.Active,
            Source = DietOrderSource.ClinicalOrder,
            ServiceOrderItemId = domainEvent.OrderItemId,
            OrderedByPractitionerId = domainEvent.OrderingPractitionerId,
            NeedsReview = matched is null || previous is null,
        };
        db.Add(order);
        await mealPlans.ReconcileAdmissionAsync(admission.Id, [order], today, now, cancellationToken);
    }
}

/// <summary>The clinical diet order was cancelled: its diet order stops and the remaining planned meals are cancelled.</summary>
internal sealed class DietServiceOrderCancelledHandler(HospitalDbContext db, MealPlanService mealPlans, TimeProvider time) : IDomainEventHandler<ServiceOrderCancelled>
{
    public async Task HandleAsync(ServiceOrderCancelled domainEvent, CancellationToken cancellationToken)
    {
        var order = await db.Set<DietOrder>()
            .FirstOrDefaultAsync(o => o.ServiceOrderItemId == domainEvent.OrderItemId && o.Status == DietOrderStatus.Active, cancellationToken);
        if (order is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var reason = $"Clinical order cancelled: {domainEvent.Reason}";
        order.Stop(null, reason.Length <= 300 ? reason : reason[..300], IndiaClock.LocalDate(now), now);
        await mealPlans.ReconcileAdmissionAsync(order.AdmissionId, [], IndiaClock.LocalDate(now), now, cancellationToken);
    }
}

/// <summary>Discharge (DIET-01): the diet order completes and meals planned after the discharge time are cancelled.</summary>
internal sealed class DietPatientDischargedHandler(HospitalDbContext db, MealPlanService mealPlans, TimeProvider time) : IDomainEventHandler<PatientDischarged>
{
    public async Task HandleAsync(PatientDischarged domainEvent, CancellationToken cancellationToken)
    {
        var orders = await db.Set<DietOrder>()
            .Where(o => o.AdmissionId == domainEvent.AdmissionId && o.Status == DietOrderStatus.Active)
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        orders.ForEach(o => o.CompleteOnDischarge(IndiaClock.LocalDate(domainEvent.DischargedAt), now));
        await mealPlans.CancelFromAsync(domainEvent.AdmissionId, domainEvent.DischargedAt, cancellationToken);
    }
}

/// <summary>
/// A cancelled admission needs no more meals: its diet order stops on the cancellation day and meals planned from the
/// cancellation time on are cancelled, so the kitchen stops preparing them and nothing more is charged.
/// </summary>
internal sealed class DietAdmissionCancelledHandler(HospitalDbContext db, MealPlanService mealPlans, TimeProvider time) : IDomainEventHandler<AdmissionCancelled>
{
    public async Task HandleAsync(AdmissionCancelled domainEvent, CancellationToken cancellationToken)
    {
        var orders = await db.Set<DietOrder>()
            .Where(o => o.AdmissionId == domainEvent.AdmissionId && o.Status == DietOrderStatus.Active)
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var order in orders)
        {
            order.Stop(null, "Admission cancelled.", IndiaClock.LocalDate(domainEvent.CancelledAt), now);
        }

        await mealPlans.CancelFromAsync(domainEvent.AdmissionId, domainEvent.CancelledAt, cancellationToken);
    }
}

/// <summary>Duplicate UHIDs merged: re-point this module's patient references to the surviving record, across facilities.</summary>
internal sealed class SupportServicesPatientsMergedHandler(HospitalDbContext db) : IDomainEventHandler<PatientsMerged>
{
    public async Task HandleAsync(PatientsMerged domainEvent, CancellationToken cancellationToken)
    {
        var from = domainEvent.SourcePatientId;
        var to = domainEvent.TargetPatientId;
        await RepointAsync<DietOrder>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<PlannedMeal>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<SetIssue>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<CanteenSale>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
    }

    private async Task RepointAsync<T>(Expression<Func<T, bool>> predicate, Action<T> repoint, CancellationToken ct)
        where T : class
    {
        var rows = await db.Set<T>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(predicate).ToListAsync(ct);
        rows.ForEach(repoint);
    }
}
