using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Referrals;

/// <summary>
/// /api/v1/crm/referring-doctors: the referring-doctor master with referral counts (CRM-05). Counts come from visits'
/// "referred by" text. Only aggregates are shown, never the referred patients.
/// </summary>
internal static class ReferralEndpoints
{
    private const int MaxUnmatched = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/crm").WithTags("CRM").RequirePermission(EngagementPermissions.ReferralsManage);

        group.MapGet("/referring-doctors", List);
        group.MapGet("/referring-doctors/{id:guid}", Get);
        group.MapGet("/referring-doctors/{id:guid}/stats", async (Guid id, DateOnly? from, DateOnly? to, ReferralService referrals, TimeProvider time, CancellationToken ct) =>
        {
            var period = ReportPeriod.Resolve(from, to, time.Today());
            return period.IsFailure ? period.Error!.ToProblem() : (await referrals.StatsAsync(id, period.Value, ct)).ToOk();
        });
        group.MapGet("/referrals/unmatched", Unmatched);
        group.MapPost("/referring-doctors", Create).WithValidation<SaveReferringDoctorRequest>();
        group.MapPut("/referring-doctors/{id:guid}", Update).WithValidation<SaveReferringDoctorRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        bool? activeOnly,
        DateOnly? from,
        DateOnly? to,
        HospitalDbContext db,
        ReferralService referrals,
        TimeProvider time,
        CancellationToken ct)
    {
        var period = ReportPeriod.Resolve(from, to, time.Today());
        if (period.IsFailure)
        {
            return period.Error!.ToProblem();
        }

        var page = query.ToRequest();
        var doctors = db.Set<ReferringDoctor>().AsNoTracking();
        if (activeOnly == true)
        {
            doctors = doctors.Where(d => d.IsActive);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            doctors = doctors.Where(d => EF.Functions.ILike(d.Name, like, QueryExtensions.LikeEscape) || (d.Clinic != null && EF.Functions.ILike(d.Clinic, like, QueryExtensions.LikeEscape))
                                         || (d.City != null && EF.Functions.ILike(d.City, like, QueryExtensions.LikeEscape)));
        }

        var rows = await doctors.Select(d => new { d.Id, d.Name, d.Clinic, d.Specialty, d.City, d.Phone, d.IsActive }).ToListAsync(ct);
        var (tallies, _) = await referrals.AttributeAsync(period.Value, ct);

        // The master is small (hundreds of doctors), so ranking by referrals and paging happen in memory.
        var items = rows
            .Select(d => (Doctor: d, Tally: tallies.GetValueOrDefault(d.Id, new ReferralTally(0, null))))
            .OrderByDescending(x => x.Tally.Visits).ThenBy(x => x.Doctor.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ReferringDoctorListItem(x.Doctor.Id, x.Doctor.Name, x.Doctor.Clinic, x.Doctor.Specialty, x.Doctor.City, x.Doctor.Phone,
                x.Doctor.IsActive, x.Tally.Visits, x.Tally.LastReferralAt))
            .ToList();
        return TypedResults.Ok(new PagedResult<ReferringDoctorListItem>(items.Skip(page.Skip).Take(page.Take).ToList(), page.Page, page.Take, items.Count));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var d = await db.Set<ReferringDoctor>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return d is null
            ? Error.NotFound(nameof(ReferringDoctor), id).ToProblem()
            : TypedResults.Ok(new ReferringDoctorDetail(d.Id, d.Name, d.Clinic, d.Phone, d.Email, d.Specialty, d.City, d.RegistrationNumber,
                d.AlternateNames, d.Notes, d.IsActive, d.Version));
    }

    /// <summary>Referrer texts that match no registered doctor: candidates for a new doctor or an extra spelling.</summary>
    private static async Task<IResult> Unmatched(DateOnly? from, DateOnly? to, ReferralService referrals, TimeProvider time, CancellationToken ct)
    {
        var period = ReportPeriod.Resolve(from, to, time.Today());
        if (period.IsFailure)
        {
            return period.Error!.ToProblem();
        }

        var (_, unmatched) = await referrals.AttributeAsync(period.Value, ct);
        return TypedResults.Ok(unmatched.Take(MaxUnmatched).ToList());
    }

    private static async Task<IResult> Create(SaveReferringDoctorRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var duplicate = await DuplicateAsync(db, request.RegistrationNumber, null, ct);
        if (duplicate is not null)
        {
            return duplicate.ToProblem();
        }

        var doctor = new ReferringDoctor { FacilityId = facilityId };
        Apply(doctor, request);
        db.Add(doctor);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/crm/referring-doctors/{doctor.Id}", doctor.Id);
    }

    private static async Task<IResult> Update(Guid id, SaveReferringDoctorRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var doctor = await db.Set<ReferringDoctor>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doctor is null)
        {
            return Error.NotFound(nameof(ReferringDoctor), id).ToProblem();
        }

        var duplicate = await DuplicateAsync(db, request.RegistrationNumber, id, ct);
        if (duplicate is not null)
        {
            return duplicate.ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(doctor, version);
        }

        Apply(doctor, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Error?> DuplicateAsync(HospitalDbContext db, string? registrationNumber, Guid? exceptId, CancellationToken ct)
    {
        var number = Clean(registrationNumber)?.ToUpperInvariant();
        return number is not null && await db.Set<ReferringDoctor>().AnyAsync(d => d.RegistrationNumber == number && d.Id != exceptId, ct)
            ? Error.Conflict("referral.duplicate_doctor", $"A referring doctor with registration number {number} already exists.")
            : null;
    }

    private static void Apply(ReferringDoctor doctor, SaveReferringDoctorRequest request)
    {
        doctor.Name = request.Name.Trim();
        doctor.Clinic = Clean(request.Clinic);
        doctor.Phone = Phone.Normalize(request.Phone);
        doctor.Email = Clean(request.Email)?.ToLowerInvariant();
        doctor.Specialty = Clean(request.Specialty);
        doctor.City = Clean(request.City);
        doctor.RegistrationNumber = Clean(request.RegistrationNumber)?.ToUpperInvariant();
        doctor.AlternateNames = (request.AlternateNames ?? [])
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        doctor.Notes = Clean(request.Notes);
        doctor.IsActive = request.IsActive;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
