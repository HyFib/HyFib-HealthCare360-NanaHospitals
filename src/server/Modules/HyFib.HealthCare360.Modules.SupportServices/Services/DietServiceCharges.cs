using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// The diets charged by themselves, a charge a day per admission (<see cref="MealPlanService"/>): a diet's charge service
/// taken out of use would make billing refuse those days, so the services master refuses it while a diet in use charges
/// by it. A diet charged by no service, or a facility that does not charge diets, is not charged at all.
/// </summary>
internal sealed class DietServiceCharges(HospitalDbContext db) : IServiceChargeSource
{
    public async Task<IReadOnlyList<string>> UsesAsync(Guid serviceItemId, CancellationToken cancellationToken)
    {
        var diets = await db.Set<DietType>().AsNoTracking()
            .Where(t => t.ChargeServiceItemId == serviceItemId && t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => t.Name)
            .ToListAsync(cancellationToken);
        return [.. diets.Select(d => $"the diet {d} charges its days by it")];
    }
}
