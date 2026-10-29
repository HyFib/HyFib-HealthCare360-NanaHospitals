using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>A facility's rate list, and the languages it is printed in beside English.</summary>
/// <param name="Rates">The rates.</param>
/// <param name="Language">The language its own words are printed in beside English: the local language, when HC360 writes it.</param>
/// <param name="LocalLanguage">The facility's local language (<see cref="LocalLanguages"/>): each service's name in it prints under the English one.</param>
/// <param name="UnknownSetting">The hospital's choice of the facility's language, when it names no language.</param>
/// <param name="UnknownState">The facility's state, when it names none and so gives no local language.</param>
public sealed record RateListView(
    RateList Rates, string? Language, IndianLanguage? LocalLanguage = null, string? UnknownSetting = null, string? UnknownState = null);

/// <summary>
/// The rates a self-paying patient is charged at a facility today, read from what a bill reads: the plan that prices a
/// self-paying patient there, its prices and concession, the services in use, the doctors who consult there, the bed
/// classes of its wards, the packages that plan may bill, and whether it charges a registration fee.
/// </summary>
public sealed class RateListService(
    HospitalDbContext db,
    TariffService tariffs,
    ISettingsService settings,
    IRoomClassDirectory roomClasses,
    TimeProvider time)
{
    private static readonly PractitionerKind[] Doctors = [.. PractitionerKinds.Doctors];

    public async Task<RateListView?> ForFacilityAsync(Guid facilityId, CancellationToken ct)
    {
        var facility = await db.Set<Facility>().AsNoTracking()
            .Where(f => f.Id == facilityId && f.IsActive)
            .Select(f => new { f.Gstin, f.Address })
            .FirstOrDefaultAsync(ct);
        if (facility is null)
        {
            return null;
        }

        var local = await LocalLanguages.OfFacilityAsync(settings, facilityId, facility.Address, facility.Gstin, ct);
        var plan = await tariffs.ResolvePlanAsync(PayerType.Self, null, facilityId, ct);
        var items = await db.Set<ServiceItem>().AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => new RateListItem(s.Id, s.Code, s.Name, s.LocalNames, s.Category, s.BasePrice, s.TaxRate, s.IsDiscountAllowed, s.IsPriceEditable))
            .ToListAsync(ct);

        var prices = plan is null
            ? []
            : await db.Set<TariffPrice>().AsNoTracking()
                .Where(p => p.TariffPlanId == plan.Id)
                .Select(p => new { p.ServiceItemId, Price = new PlanPrice(p.Id, p.Price, p.EffectiveFrom, p.EffectiveTo) })
                .ToListAsync(ct);

        // A package for any plan, or for this one (BillService refuses one priced for another plan).
        var planId = plan?.Id;
        var packages = await db.Set<Package>().AsNoTracking()
            .Where(p => p.IsActive && (p.TariffPlanId == null || p.TariffPlanId == planId))
            .Select(p => new RateListPackage(p.Code, p.Name, p.ServiceItemId, p.Price, p.LengthOfStayDays, p.UsedFor))
            .ToListAsync(ct);

        return new RateListView(
            RateListBuilder.Build(new RateListInput
            {
                AsOn = BillingTime.Today(time),
                Items = items,
                Plan = plan is null
                    ? null
                    : new RateListPlan(plan.Id, plan.Name, plan.DefaultDiscountPercent, plan.BedMultipliers.ToDictionary(m => m.BedServiceItemId, m => m.Multiplier)),
                PlanPrices = prices.GroupBy(p => p.ServiceItemId).ToDictionary(g => g.Key, g => (IReadOnlyList<PlanPrice>)[.. g.Select(p => p.Price)]),
                Doctors = await DoctorsAsync(facilityId, ct),
                Rooms = await roomClasses.ForFacilityAsync(facilityId, ct),
                Packages = packages,
                RegistrationFee = await settings.GetAsync<bool?>(VisitChargesHandler.RegistrationFeeSetting, facilityId, ct) ?? true,
                LocalLanguage = local.Language?.Code,
            }),
            local.Language is { } language && PatientLanguages.IsSupported(language.Code) ? language.Code : null,
            local.Language,
            local.UnknownSetting,
            local.UnknownState);
    }

    /// <summary>
    /// The doctors who consult at the facility (consultants, visiting doctors and residents: a visit with any of them is
    /// charged at their fee), with their department (or specialty).
    /// </summary>
    private async Task<IReadOnlyList<RateListDoctor>> DoctorsAsync(Guid facilityId, CancellationToken ct)
    {
        var doctors = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.IsActive && Doctors.Contains(p.Kind) && (p.FacilityIds.Count == 0 || p.FacilityIds.Contains(facilityId)))
            .Select(p => new { p.DisplayName, p.DepartmentId, p.SpecialtyId, p.ConsultationFeeNew, p.FollowUpValidityDays, p.AvailableForTelehealth })
            .ToListAsync(ct);

        // A doctor's department may belong to another of their facilities.
        var departmentIds = doctors.Select(d => d.DepartmentId).OfType<Guid>().Distinct().ToList();
        var departments = await db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(d => departmentIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var specialtyIds = doctors.Select(d => d.SpecialtyId).OfType<Guid>().Distinct().ToList();
        var specialties = await db.Set<Specialty>().AsNoTracking()
            .Where(s => specialtyIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        return
        [
            .. doctors.Select(d => new RateListDoctor(
                d.DisplayName,
                d.DepartmentId is { } department && departments.TryGetValue(department, out var name) ? name
                : d.SpecialtyId is { } specialty ? specialties.GetValueOrDefault(specialty) : null,
                d.ConsultationFeeNew, d.FollowUpValidityDays, d.AvailableForTelehealth)),
        ];
    }
}
