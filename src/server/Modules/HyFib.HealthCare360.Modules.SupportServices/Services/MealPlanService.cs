using System.Globalization;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Ward-wise meal plans (DIET-02), kitchen production lists (DIET-03), meal delivery tracking (DIET-04) and optional
/// daily diet charges to the IP bill (DIET-05). Planning is idempotent and reconciles with the live diet orders, so the
/// hourly job, a manual regeneration and a diet change during the day all converge on the same plan.
/// </summary>
public sealed class MealPlanService(
    HospitalDbContext db,
    IChargePoster chargePoster,
    IBillingStatusProvider billingStatus,
    ISettingsService settings,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<MealPlanService> logger)
{
    /// <summary>Facility setting (bool): post one diet charge per admission per day. Off by default (many hospitals include food in the bed tariff).</summary>
    public const string DietChargesSetting = "supportservices.diet.chargesEnabled";

    /// <summary>Generates or refreshes the plan of a date for every active diet order, then posts due diet charges.</summary>
    public async Task<MealPlanResult> GenerateAsync(DateOnly date, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var diets = await ActiveDietsAsync(date, null, ct);
        var existing = await db.Set<PlannedMeal>().Where(m => m.PlanDate == date).ToListAsync(ct);
        var changes = MealPlanner.Reconcile(date, diets, existing.Select(Snapshot).ToList(), now);
        Apply(changes, existing, date, now);
        await db.SaveChangesAsync(ct);

        var charges = date <= IndiaClock.LocalDate(now) ? await PostDietChargesAsync(date, diets, ct) : 0;

        // The charge poster only stages the charges in this unit of work: they reach the IP bill when saved.
        await db.SaveChangesAsync(ct);
        var planned = await db.Set<PlannedMeal>().CountAsync(m => m.PlanDate == date && m.Status != MealDeliveryStatus.Cancelled, ct);
        return new MealPlanResult(date, changes.ToAdd.Count, changes.ToCancel.Count, planned, charges);
    }

    /// <summary>
    /// Stages plan changes for one admission from in-memory active orders (a new, superseded or stopped order not yet
    /// saved). Used inside other units of work, including domain-event handlers, so it never saves.
    /// </summary>
    public async Task ReconcileAdmissionAsync(Guid admissionId, IReadOnlyCollection<DietOrder> activeOrders, DateOnly date, DateTimeOffset asOf, CancellationToken ct)
    {
        var existing = await db.Set<PlannedMeal>().Where(m => m.AdmissionId == admissionId && m.PlanDate == date).ToListAsync(ct);
        var diets = await DietsForAsync(admissionId, activeOrders.Where(o => o.IsEffectiveOn(date)).ToList(), ct);
        var changes = MealPlanner.Reconcile(date, diets, existing.Select(Snapshot).ToList(), asOf);
        Apply(changes, existing, date, asOf);
    }

    /// <summary>Stages the cancellation of an admission's planned meals served after <paramref name="from"/> (discharge).</summary>
    public async Task CancelFromAsync(Guid admissionId, DateTimeOffset from, CancellationToken ct)
    {
        var firstDate = IndiaClock.LocalDate(from);
        var meals = await db.Set<PlannedMeal>()
            .Where(m => m.AdmissionId == admissionId && m.PlanDate >= firstDate && m.Status == MealDeliveryStatus.Planned)
            .ToListAsync(ct);
        var cancel = MealPlanner.CancelFrom(meals.Select(Snapshot), from).ToHashSet();
        foreach (var meal in meals.Where(m => cancel.Contains(m.Id)))
        {
            meal.Cancel("Patient discharged.");
        }
    }

    public async Task<Result<PlannedMealDto>> RecordDeliveryAsync(Guid mealId, RecordDeliveryRequest request, CancellationToken ct)
    {
        var meal = await db.Set<PlannedMeal>().FirstOrDefaultAsync(m => m.Id == mealId, ct);
        if (meal is null)
        {
            return Error.NotFound("Meal", mealId);
        }

        db.Entry(meal).Property(m => m.Version).OriginalValue = request.Version;
        var recorded = meal.RecordDelivery(request.Status, request.Remarks, currentUser.UserId, currentUser.DisplayName, time.GetUtcNow());
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        await db.SaveChangesAsync(ct);
        return (await MealDtosAsync(db.Set<PlannedMeal>().AsNoTracking().Where(m => m.Id == mealId), ct)).Single();
    }

    /// <summary>Marks every planned meal of a ward and round as delivered (the pantry's one-tap round completion).</summary>
    public async Task<int> DeliverWardRoundAsync(BulkDeliveryRequest request, CancellationToken ct)
    {
        var meals = await db.Set<PlannedMeal>()
            .Where(m => m.PlanDate == request.Date && m.Slot == request.Slot && m.WardId == request.WardId && m.Status == MealDeliveryStatus.Planned)
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        var updated = meals.Count(m => m.RecordDelivery(MealDeliveryStatus.Delivered, null, currentUser.UserId, currentUser.DisplayName, now).IsSuccess);
        await db.SaveChangesAsync(ct);
        return updated;
    }

    public async Task<IReadOnlyList<WardMealPlan>> WardPlanAsync(DateOnly date, Guid? wardId, MealSlot? slot, CancellationToken ct)
    {
        var meals = db.Set<PlannedMeal>().AsNoTracking().Where(m => m.PlanDate == date && m.Status != MealDeliveryStatus.Cancelled);
        if (wardId is not null)
        {
            meals = meals.Where(m => m.WardId == wardId);
        }

        if (slot is not null)
        {
            meals = meals.Where(m => m.Slot == slot);
        }

        var items = await MealDtosAsync(meals, ct);
        var wardIds = items.Where(i => i.WardId != null).Select(i => i.WardId!.Value).Distinct().ToList();
        var wards = await db.Set<Ward>().AsNoTracking().Where(w => wardIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => new { w.Name, w.DisplayOrder }, ct);
        return items
            .GroupBy(i => i.WardId)
            .Select(g => new WardMealPlan(g.Key, g.Key is { } id && wards.TryGetValue(id, out var ward) ? ward.Name : null, g.Count(),
                g.OrderBy(i => i.Slot).ThenBy(i => i.BedLabel, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(w => w.WardId is { } id && wards.TryGetValue(id, out var ward) ? ward.DisplayOrder : int.MaxValue)
            .ToList();
    }

    /// <summary>Kitchen production list: meals per round and diet, split by food preference, with allergy and restriction notes.</summary>
    public async Task<ProductionSummary> ProductionAsync(DateOnly date, CancellationToken ct)
    {
        var meals = await db.Set<PlannedMeal>().AsNoTracking()
            .Where(m => m.PlanDate == date && m.Status != MealDeliveryStatus.Cancelled)
            .Select(m => new { m.Slot, m.DietCode, m.DietName, m.Preference, m.Restrictions, m.AllergyNotes, m.BedLabel })
            .ToListAsync(ct);
        var lines = meals
            .GroupBy(m => new { m.Slot, m.DietCode })
            .Select(g => new ProductionLine(
                g.Key.Slot,
                MealSlots.ServiceTime(g.Key.Slot),
                g.Key.DietCode,
                g.First().DietName,
                g.Count(m => m.Preference == FoodPreference.Vegetarian),
                g.Count(m => m.Preference == FoodPreference.NonVegetarian),
                g.Count(m => m.Preference == FoodPreference.Eggetarian),
                g.Count(m => m.Preference == FoodPreference.Jain),
                g.Count(),
                g.Where(m => m.Restrictions.Count > 0 || !string.IsNullOrWhiteSpace(m.AllergyNotes))
                    .Select(m => $"{m.BedLabel ?? "No bed"}: {string.Join(", ", m.Restrictions)}{(string.IsNullOrWhiteSpace(m.AllergyNotes) ? string.Empty : $" (allergy: {m.AllergyNotes})")}")
                    .ToList()))
            .OrderBy(l => l.Slot).ThenBy(l => l.DietCode, StringComparer.Ordinal)
            .ToList();
        return new ProductionSummary(date, meals.Count, lines);
    }

    internal async Task<List<PlannedMealDto>> MealDtosAsync(IQueryable<PlannedMeal> meals, CancellationToken ct)
    {
        var rows = await (
                from m in meals
                join p in db.Set<Patient>() on m.PatientId equals p.Id
                select new { Meal = m, p.Uhid, p.FullName })
            .ToListAsync(ct);
        return rows.Select(r => new PlannedMealDto(r.Meal.Id, r.Meal.PlanDate, r.Meal.Slot, MealSlots.ServiceTime(r.Meal.Slot), r.Meal.AdmissionId, r.Meal.PatientId,
                r.Uhid, r.FullName, r.Meal.WardId, r.Meal.BedLabel, r.Meal.DietCode, r.Meal.DietName, r.Meal.Preference, r.Meal.Restrictions,
                r.Meal.AllergyNotes, r.Meal.Instructions, r.Meal.Status, r.Meal.DeliveredAt, r.Meal.DeliveredByName, r.Meal.Remarks, r.Meal.Version))
            .ToList();
    }

    private async Task<List<DietForPlanning>> ActiveDietsAsync(DateOnly date, Guid? admissionId, CancellationToken ct)
    {
        var rows = await (
                from o in db.Set<DietOrder>()
                join a in db.Set<Admission>() on o.AdmissionId equals a.Id
                join d in db.Set<DietType>() on o.DietTypeId equals d.Id
                join b in db.Set<Bed>() on a.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                where o.Status == DietOrderStatus.Active && o.StartDate <= date && (o.EndDate == null || o.EndDate >= date)
                      && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated)
                      && (admissionId == null || o.AdmissionId == admissionId)
                select new { Order = o, a.WardId, BedLabel = b != null ? b.Label : null, d.Code, d.Name, d.Category })
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.Select(r => ToPlanning(r.Order, r.Code, r.Name, r.Category, r.WardId, r.BedLabel)).ToList();
    }

    private async Task<List<DietForPlanning>> DietsForAsync(Guid admissionId, IReadOnlyCollection<DietOrder> orders, CancellationToken ct)
    {
        if (orders.Count == 0)
        {
            return [];
        }

        var location = await (
                from a in db.Set<Admission>().AsNoTracking()
                join b in db.Set<Bed>() on a.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                where a.Id == admissionId
                select new { a.WardId, BedLabel = b != null ? b.Label : null })
            .FirstOrDefaultAsync(ct);
        var typeIds = orders.Select(o => o.DietTypeId).Distinct().ToList();
        var types = await db.Set<DietType>().AsNoTracking().Where(d => typeIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        return orders
            .Where(o => types.ContainsKey(o.DietTypeId))
            .Select(o => ToPlanning(o, types[o.DietTypeId].Code, types[o.DietTypeId].Name, types[o.DietTypeId].Category, location?.WardId, location?.BedLabel))
            .ToList();
    }

    /// <summary>
    /// One diet charge per admission per day ("{dietOrderId}:{date}"). If the diet changed during the day, a day already
    /// charged under the earlier order is not charged again (checked through Billing's charge states).
    /// </summary>
    private async Task<int> PostDietChargesAsync(DateOnly date, IReadOnlyCollection<DietForPlanning> diets, CancellationToken ct)
    {
        var posted = 0;
        foreach (var facilityDiets in diets.Where(d => !d.IsNilByMouth).GroupBy(d => d.FacilityId))
        {
            if (await settings.GetAsync<bool?>(DietChargesSetting, facilityDiets.Key, ct) != true)
            {
                continue;
            }

            // Every order of the admission started by the date, whatever its end date: superseding an order ends it the day
            // before the new one starts, but the day of the change may already be charged under it.
            var admissionIds = facilityDiets.Select(d => d.AdmissionId).Distinct().ToList();
            var billable = await (
                    from o in db.Set<DietOrder>().AsNoTracking()
                    join a in db.Set<Admission>() on o.AdmissionId equals a.Id
                    join t in db.Set<DietType>() on o.DietTypeId equals t.Id
                    where admissionIds.Contains(o.AdmissionId) && o.StartDate <= date
                    select new { o.Id, o.AdmissionId, o.PatientId, a.VisitId, t.Name, t.ChargeServiceItemId })
                .ToListAsync(ct);

            foreach (var diet in facilityDiets)
            {
                var order = billable.FirstOrDefault(b => b.Id == diet.DietOrderId);
                if (order?.ChargeServiceItemId is not { } serviceItemId)
                {
                    continue;
                }

                var sameDay = billable.Where(b => b.AdmissionId == diet.AdmissionId).Select(b => DietChargeKeys.For(b.Id, date)).ToList();
                var states = await billingStatus.GetStatesAsync(DietChargeKeys.Source, sameDay, ct);
                if (states.Values.Any(s => s is ChargeBillingState.Unbilled or ChargeBillingState.Billed or ChargeBillingState.Paid))
                {
                    continue;
                }

                var result = await chargePoster.PostAsync(new ChargeRequest
                {
                    PatientId = order.PatientId,
                    VisitId = order.VisitId,
                    AdmissionId = order.AdmissionId,
                    ServiceItemId = serviceItemId,
                    Quantity = 1,
                    Description = $"Diet charges ({order.Name}) for {date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}",
                    SourceModule = DietChargeKeys.Source,
                    SourceReference = DietChargeKeys.For(order.Id, date),
                    ServiceDate = IndiaClock.StartOfDayUtc(date),
                }, ct);
                if (result.IsSuccess)
                {
                    posted++;
                }
                else
                {
                    logger.LogWarning("Diet charge for order {DietOrderId} on {Date} was not accepted by billing: {Code}", order.Id, date, result.Error!.Code);
                }
            }
        }

        return posted;
    }

    private void Apply(MealPlanChanges changes, List<PlannedMeal> existing, DateOnly date, DateTimeOffset now)
    {
        var cancel = changes.ToCancel.ToHashSet();
        foreach (var meal in existing.Where(m => cancel.Contains(m.Id)))
        {
            meal.Cancel("Diet order changed or stopped.");
        }

        foreach (var add in changes.ToAdd)
        {
            db.Add(new PlannedMeal
            {
                FacilityId = add.Diet.FacilityId,
                PlanDate = date,
                Slot = add.Slot,
                DietOrderId = add.Diet.DietOrderId,
                AdmissionId = add.Diet.AdmissionId,
                PatientId = add.Diet.PatientId,
                WardId = add.Diet.WardId,
                BedLabel = add.Diet.BedLabel,
                DietTypeId = add.Diet.DietTypeId,
                DietCode = add.Diet.DietCode,
                DietName = add.Diet.DietName,
                Preference = add.Diet.Preference,
                Restrictions = add.Diet.Restrictions.ToList(),
                AllergyNotes = add.Diet.AllergyNotes,
                Instructions = add.Diet.Instructions,
                Status = MealDeliveryStatus.Planned,
                CreatedAt = now,
            });
        }
    }

    private static DietForPlanning ToPlanning(DietOrder o, string code, string name, DietCategory category, Guid? wardId, string? bedLabel) => new(
        o.Id, o.AdmissionId, o.PatientId, o.FacilityId, o.DietTypeId, code, name, category == DietCategory.NilByMouth, o.MealSlots, o.Preference,
        o.Restrictions, o.AllergyNotes, o.Instructions, wardId, bedLabel);

    private static PlannedMealSnapshot Snapshot(PlannedMeal m) => new(m.Id, m.DietOrderId, m.AdmissionId, m.PlanDate, m.Slot, m.Status);
}
