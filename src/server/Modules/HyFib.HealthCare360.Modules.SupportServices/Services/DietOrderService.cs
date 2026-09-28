using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Diet orders from doctors, dietitians and nurses (DIET-01) and from clinical orders. A new order supersedes the
/// admission's current one and today's (and an already planned tomorrow's) meal plan is reconciled in the same
/// transaction, so a switch to nil-by-mouth before surgery cancels the next meal immediately.
/// </summary>
public sealed class DietOrderService(HospitalDbContext db, MealPlanService mealPlans, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<Result<DietOrderDto>> CreateAsync(CreateDietOrderRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().AsNoTracking()
            .Where(a => a.Id == request.AdmissionId)
            .Select(a => new { a.Id, a.FacilityId, a.PatientId, a.Status })
            .FirstOrDefaultAsync(ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), request.AdmissionId);
        }

        if (admission.Status is not (AdmissionStatus.Admitted or AdmissionStatus.DischargeInitiated))
        {
            return Error.Rule("diet.admission_closed", "Diet orders can be written only for admitted patients.");
        }

        var dietType = await db.Set<DietType>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.DietTypeId && d.IsActive, ct);
        if (dietType is null)
        {
            return Error.Field("dietTypeId", "Choose an active diet.");
        }

        var (source, orderedBy) = await PrescriberAsync(request.OrderedByPractitionerId, ct);
        var now = time.GetUtcNow();
        var today = time.Today();
        var start = request.StartDate is { } s && s > today ? s : today;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await db.Set<DietOrder>().Where(o => o.AdmissionId == admission.Id && o.Status == DietOrderStatus.Active).ToListAsync(ct);
        current.ForEach(o => o.Supersede(start, now));
        var order = new DietOrder
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            DietTypeId = dietType.Id,
            Preference = request.Preference,
            Restrictions = (request.Restrictions ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            AllergyNotes = Clean(request.AllergyNotes),
            Instructions = Clean(request.Instructions),
            FluidRestrictionMl = request.FluidRestrictionMl,
            CalorieTargetKcal = request.CalorieTargetKcal,
            MealSlots = request.MealSlots ?? dietType.DefaultMealSlots,
            StartDate = start,
            Status = DietOrderStatus.Active,
            Source = source,
            OrderedByPractitionerId = request.OrderedByPractitionerId ?? currentUser.PractitionerId,
            OrderedByName = orderedBy ?? currentUser.DisplayName,
        };
        db.Add(order);
        await db.SaveChangesAsync(ct);

        await ReplanAsync(admission.Id, [order], now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(order.Id, ct);
    }

    public async Task<Result<DietOrderDto>> StopAsync(Guid orderId, string reason, CancellationToken ct)
    {
        var order = await db.Set<DietOrder>().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("Diet order", orderId);
        }

        var now = time.GetUtcNow();
        var stopped = order.Stop(currentUser.UserId, reason, time.Today(), now);
        if (stopped.IsFailure)
        {
            return stopped.Error!;
        }

        await ReplanAsync(order.AdmissionId, [], now, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(order.Id, ct);
    }

    public async Task<Result<DietOrderDto>> ReviewAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Set<DietOrder>().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound("Diet order", orderId);
        }

        var reviewed = order.MarkReviewed(currentUser.UserId, time.GetUtcNow());
        if (reviewed.IsFailure)
        {
            return reviewed.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(order.Id, ct);
    }

    public async Task<Result<DietOrderDto>> GetAsync(Guid orderId, CancellationToken ct)
    {
        var order = (await DtosAsync(db.Set<DietOrder>().AsNoTracking().Where(o => o.Id == orderId), ct)).FirstOrDefault();
        return order is null ? Error.NotFound("Diet order", orderId) : order;
    }

    internal async Task<List<DietOrderDto>> DtosAsync(IQueryable<DietOrder> orders, CancellationToken ct)
    {
        var rows = await (
                from o in orders
                join t in db.Set<DietType>() on o.DietTypeId equals t.Id
                join a in db.Set<Admission>() on o.AdmissionId equals a.Id
                join p in db.Set<Patient>() on o.PatientId equals p.Id
                join w in db.Set<Ward>() on a.WardId equals w.Id into wards
                from w in wards.DefaultIfEmpty()
                join b in db.Set<Bed>() on a.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                orderby o.CreatedAt descending
                select new
                {
                    Order = o, t.Code, t.Name, a.AdmissionNumber, p.Uhid, p.FullName, WardName = w != null ? w.Name : null, BedLabel = b != null ? b.Label : null,
                })
            .Take(500)
            .ToListAsync(ct);
        return rows.Select(r => new DietOrderDto(
                r.Order.Id, r.Order.AdmissionId, r.AdmissionNumber, r.Order.PatientId, r.Uhid, r.FullName, r.WardName, r.BedLabel, r.Order.DietTypeId,
                r.Code, r.Name, r.Order.Preference, r.Order.Restrictions, r.Order.AllergyNotes, r.Order.Instructions, r.Order.FluidRestrictionMl,
                r.Order.CalorieTargetKcal, r.Order.MealSlots, MealSlots.Split(r.Order.MealSlots), r.Order.StartDate, r.Order.EndDate, r.Order.Status,
                r.Order.Source, r.Order.OrderedByName, r.Order.NeedsReview, r.Order.CreatedAt, r.Order.StoppedAt, r.Order.StopReason))
            .ToList();
    }

    /// <summary>Reconciles today's plan and, if the kitchen already planned it, tomorrow's.</summary>
    private async Task ReplanAsync(Guid admissionId, IReadOnlyCollection<DietOrder> activeOrders, DateTimeOffset now, CancellationToken ct)
    {
        var today = IndiaClock.LocalDate(now);
        await mealPlans.ReconcileAdmissionAsync(admissionId, activeOrders, today, now, ct);
        var tomorrow = today.AddDays(1);
        if (await db.Set<PlannedMeal>().AnyAsync(m => m.PlanDate == tomorrow, ct))
        {
            await mealPlans.ReconcileAdmissionAsync(admissionId, activeOrders, tomorrow, now, ct);
        }
    }

    /// <summary>Who ordered the diet: dietitians and nurses are told apart from doctors by their practitioner record.</summary>
    private async Task<(DietOrderSource Source, string? Name)> PrescriberAsync(Guid? practitionerId, CancellationToken ct)
    {
        var id = practitionerId ?? currentUser.PractitionerId;
        if (id is null)
        {
            return (DietOrderSource.Nurse, currentUser.DisplayName);
        }

        var practitioner = await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == id).Select(p => new { p.Kind, p.DisplayName }).FirstOrDefaultAsync(ct);
        return practitioner?.Kind switch
        {
            PractitionerKind.Dietitian => (DietOrderSource.Dietitian, practitioner.DisplayName),
            PractitionerKind.Nurse => (DietOrderSource.Nurse, practitioner.DisplayName),
            null => (DietOrderSource.Nurse, currentUser.DisplayName),
            _ => (DietOrderSource.Doctor, practitioner.DisplayName),
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
