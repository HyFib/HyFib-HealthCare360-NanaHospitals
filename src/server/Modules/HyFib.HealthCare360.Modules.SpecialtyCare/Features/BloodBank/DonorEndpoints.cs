using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;

/// <summary>
/// /api/v1/blood-bank — donors, screening questionnaire and eligibility, donations, component separation and
/// grouping/TTI results. Donor data (including reactive results) is confidential: every endpoint needs
/// <c>bloodbank.donors.manage</c> or <c>bloodbank.inventory.manage</c>, and donor record views are audited.
/// </summary>
internal static class DonorEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/blood-bank").WithTags("Blood Bank");

        group.MapGet("/donor-questions", () => TypedResults.Ok(DonorEligibility.Questions
                .Select(q => new DonorQuestionDto(q.Key, q.Text, q.OnlyFor, q.DeferralDays, q.DeferralDays is null)).ToList()))
            .RequirePermission(SpecialtyCarePermissions.BloodBankDonors);
        group.MapGet("/donors", ListDonors).RequirePermission(SpecialtyCarePermissions.BloodBankDonors);
        group.MapGet("/donors/{id:guid}", GetDonor).RequirePermission(SpecialtyCarePermissions.BloodBankDonors);
        group.MapPost("/donors", async (SaveDonorRequest r, DonorService s, CancellationToken ct) =>
                (await s.RegisterAsync(r, ct)).ToHttpResult(id => TypedResults.Created($"/api/v1/blood-bank/donors/{id}", new { id })))
            .RequirePermission(SpecialtyCarePermissions.BloodBankDonors).WithValidation<SaveDonorRequest>();
        group.MapPut("/donors/{id:guid}", async (Guid id, SaveDonorRequest r, DonorService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.BloodBankDonors).WithValidation<SaveDonorRequest>().RequireVersion();
        group.MapPost("/donors/{id:guid}/defer", async (Guid id, DeferDonorRequest r, DonorService s, CancellationToken ct) => (await s.DeferAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.BloodBankDonors).WithValidation<DeferDonorRequest>();
        group.MapPost("/donors/{id:guid}/screenings", async (Guid id, ScreenDonorRequest r, DonorService s, CancellationToken ct) =>
                (await s.ScreenAsync(id, r, ct)).ToHttpResult(x => TypedResults.Ok(ToDto(x))))
            .RequirePermission(SpecialtyCarePermissions.BloodBankDonors).WithValidation<ScreenDonorRequest>();
        group.MapPost("/donors/{id:guid}/donations", async (Guid id, CollectDonationRequest r, DonorService s, CancellationToken ct) =>
                (await s.CollectAsync(id, r, ct)).ToHttpResult(d => TypedResults.Created($"/api/v1/blood-bank/donations/{d}", new { id = d })))
            .RequirePermission(SpecialtyCarePermissions.BloodBankDonors).WithValidation<CollectDonationRequest>();

        group.MapGet("/donations", ListDonations).RequirePermission(SpecialtyCarePermissions.BloodBankInventory);
        group.MapGet("/donations/{id:guid}", GetDonation).RequirePermission(SpecialtyCarePermissions.BloodBankInventory);
        group.MapPost("/donations/{id:guid}/components", async (Guid id, SeparateComponentsRequest r, DonorService s, TimeProvider time, CancellationToken ct) =>
                (await s.SeparateAsync(id, r, ct)).ToHttpResult(units => TypedResults.Ok(units.Select(u => InventoryEndpoints.ToDto(u, time.GetUtcNow())).ToList())))
            .RequirePermission(SpecialtyCarePermissions.BloodBankInventory).WithValidation<SeparateComponentsRequest>();
        group.MapPut("/donations/{id:guid}/lab-results", async (Guid id, RecordLabResultsRequest r, DonorService s, CancellationToken ct) =>
                (await s.RecordLabResultsAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.BloodBankInventory).WithValidation<RecordLabResultsRequest>();
    }

    private static async Task<IResult> ListDonors([AsParameters] PageQuery query, BloodGroup? bloodGroup, bool? eligibleOnly, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.Today();
        var donors = db.Set<Donor>().AsNoTracking().Where(d => d.IsActive);
        if (bloodGroup is not null)
        {
            donors = donors.Where(d => d.BloodGroup == bloodGroup);
        }

        if (eligibleOnly == true)
        {
            donors = donors.Where(d => !d.IsPermanentlyDeferred && (d.DeferredUntil == null || d.DeferredUntil <= today));
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            var phone = Phone.Normalize(term);
            var number = term.ToUpperInvariant();
            donors = donors.Where(d => d.DonorNumber == number || EF.Functions.ILike(d.FullName, like, QueryExtensions.LikeEscape) || (phone != null && d.Phone == phone));
        }

        var result = await donors.OrderBy(d => d.FullName).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(d => new DonorListItem(
            d.Id, d.DonorNumber, d.FullName, d.Gender, Age.Format(d.DateOfBirth, today), d.BloodGroup, d.BloodGroupConfirmed, Phone.Mask(d.Phone),
            d.DonationCount, d.LastDonationAt, d.IsPermanentlyDeferred, d.DeferredUntil, NextEligibleOn(d))));
    }

    private static async Task<IResult> GetDonor(Guid id, HospitalDbContext db, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var donor = await db.Set<Donor>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (donor is null)
        {
            return Error.NotFound(nameof(Donor), id).ToProblem();
        }

        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "donor.accessed", $"Viewed donor {donor.DonorNumber}")
        {
            EntityType = nameof(Donor),
            EntityId = donor.Id.ToString(),
        }, ct);

        var screenings = await db.Set<DonorScreening>().AsNoTracking().Where(s => s.DonorId == id).OrderByDescending(s => s.ScreenedAt).Take(50).ToListAsync(ct);
        var donations = await DonationRows(db, db.Set<Donation>().AsNoTracking().Where(d => d.DonorId == id)).Take(100).ToListAsync(ct);
        return TypedResults.Ok(new DonorDetail(
            donor.Id, donor.DonorNumber, donor.FirstName, donor.LastName, donor.FullName, donor.Gender, donor.DateOfBirth,
            Age.Format(donor.DateOfBirth, time.Today()), donor.BloodGroup, donor.BloodGroupConfirmed, donor.Phone, donor.Email, donor.Address,
            donor.Occupation, donor.ConsentToNotify, donor.DonationCount, donor.LastDonationAt, NextEligibleOn(donor), donor.DeferredUntil,
            donor.IsPermanentlyDeferred, donor.DeferralReason, donor.Notes, donor.IsActive, screenings.Select(ToDto).ToList(), donations, donor.Version));
    }

    private static async Task<IResult> ListDonations([AsParameters] PageQuery query, DonationStatus? status, bool? pendingResults, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var donations = db.Set<Donation>().AsNoTracking();
        if (status is not null)
        {
            donations = donations.Where(d => d.Status == status);
        }

        if (pendingResults == true)
        {
            donations = donations.Where(d => d.Status != DonationStatus.Discarded && d.LabResultsAt == null);
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            donations = donations.Where(d => d.BagNumber == number);
        }

        return TypedResults.Ok(await DonationRows(db, donations).ToPagedResultAsync(page, ct));
    }

    private static async Task<IResult> GetDonation(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var row = await (
                from x in db.Set<Donation>().AsNoTracking()
                join o in db.Set<Donor>() on x.DonorId equals o.Id
                where x.Id == id
                select new { d = x, o.DonorNumber, o.FullName })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Error.NotFound(nameof(Donation), id).ToProblem();
        }

        var units = await db.Set<BloodComponent>().AsNoTracking().Where(c => c.DonationId == id)
            .OrderBy(SqlOrdering.Rank<BloodComponent, BloodComponentType>(c => c.ComponentType))
            .ToListAsync(ct);
        var d = row.d;
        return TypedResults.Ok(new DonationDetail(
            d.Id, d.BagNumber, d.DonorId, row.DonorNumber, row.FullName, d.BagType, d.VolumeMl, d.Type, d.ReplacementForPatientId, d.CollectedAt,
            d.CollectedByName, d.Status, d.BloodGroup, d.Tti, d.LabResultsAt, d.SeparatedAt, d.DonorReaction, d.Notes,
            units.Select(u => InventoryEndpoints.ToDto(u, time.GetUtcNow())).ToList(), d.Version));
    }

    private static IQueryable<DonationListItem> DonationRows(HospitalDbContext db, IQueryable<Donation> donations) =>
        from d in donations
        join o in db.Set<Donor>() on d.DonorId equals o.Id
        orderby d.CollectedAt descending
        select new DonationListItem(
            d.Id, d.BagNumber, d.DonorId, o.DonorNumber, o.FullName, d.BagType, d.VolumeMl, d.Type, d.CollectedAt, d.Status, d.BloodGroup,
            d.Tti.Hiv != TtiResult.Pending && d.Tti.HbsAg != TtiResult.Pending && d.Tti.Hcv != TtiResult.Pending
                && d.Tti.Syphilis != TtiResult.Pending && d.Tti.Malaria != TtiResult.Pending,
            d.Tti.Hiv == TtiResult.Reactive || d.Tti.HbsAg == TtiResult.Reactive || d.Tti.Hcv == TtiResult.Reactive
                || d.Tti.Syphilis == TtiResult.Reactive || d.Tti.Malaria == TtiResult.Reactive);

    /// <summary>Earliest date the donor may give whole blood again (interval and deferral), null if permanently deferred.</summary>
    private static DateOnly? NextEligibleOn(Donor donor)
    {
        if (donor.IsPermanentlyDeferred)
        {
            return null;
        }

        DateOnly? byInterval = donor.LastDonationAt is { } last
            ? SpecialtyClock.ToLocalDate(last).AddDays(DonorEligibility.IntervalDays(donor.Gender))
            : null;
        return new[] { byInterval, donor.DeferredUntil }.Where(d => d is not null).Max();
    }

    internal static DonorScreeningDto ToDto(DonorScreening s) => new(
        s.Id, s.ScreenedAt, s.ScreenedByName, s.HaemoglobinGdl, s.WeightKg, s.Systolic, s.Diastolic, s.Pulse, s.TemperatureC, s.Answers, s.Outcome,
        s.Reasons, s.DeferredUntil, s.MaxBagVolumeMl, s.DonationId);
}
