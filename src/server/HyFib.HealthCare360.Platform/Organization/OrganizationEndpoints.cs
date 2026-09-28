using System.Linq.Expressions;
using FluentValidation;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Organization;

public sealed record FacilityDto(
    Guid Id, string Code, string Name, string? LegalName, Address Address, string? Phone, string? Email, string? Website,
    string? Gstin, string? Pan, string? RegistrationNumber, string? NabhNumber, string? HfrId, string TimeZone, string Currency,
    Guid? LogoFileId, string? LetterheadNote, bool IsActive, uint Version);

public sealed record SaveFacilityRequest(
    string Code, string Name, string? LegalName, Address? Address, string? Phone, string? Email, string? Website, string? Gstin,
    string? Pan, string? RegistrationNumber, string? NabhNumber, string? HfrId, string? LetterheadNote, Guid? LogoFileId, bool IsActive, uint? Version);

public sealed record DepartmentDto(
    Guid Id, Guid FacilityId, string Code, string Name, DepartmentKind Kind, Guid? SpecialtyId, Guid? HeadPractitionerId,
    string? Location, string? Phone, string? TokenPrefix, int DisplayOrder, bool IsActive, uint Version);

public sealed record SaveDepartmentRequest(
    string Code, string Name, DepartmentKind Kind, Guid? SpecialtyId, Guid? HeadPractitionerId, string? Location, string? Phone,
    string? TokenPrefix, int DisplayOrder, bool IsActive, uint? Version);

public sealed record SpecialtyDto(Guid Id, string Code, string Name, string? Description, bool IsActive, uint Version);

public sealed record SaveSpecialtyRequest(string Code, string Name, string? Description, bool IsActive, uint? Version);

public sealed record PractitionerDto(
    Guid Id, string Code, string Title, string FirstName, string? LastName, string DisplayName, Gender Gender, string? Qualification,
    string? RegistrationCouncil, string? RegistrationNumber, Guid? SpecialtyId, string? SpecialtyName, Guid? DepartmentId, string? DepartmentName,
    PractitionerKind Kind, string? Phone, string? Email, decimal ConsultationFeeNew, decimal ConsultationFeeFollowUp, int FollowUpValidityDays,
    IReadOnlyList<string> Languages, string? Bio, Guid? PhotoFileId, Guid? SignatureFileId, bool AvailableForOnlineBooking,
    bool AvailableForTelehealth, IReadOnlyList<Guid> FacilityIds, bool IsActive, uint Version);

public sealed record SavePractitionerRequest(
    string Code, string Title, string FirstName, string? LastName, Gender Gender, string? Qualification, string? RegistrationCouncil,
    string? RegistrationNumber, Guid? SpecialtyId, Guid? DepartmentId, PractitionerKind Kind, string? Phone, string? Email,
    decimal ConsultationFeeNew, decimal ConsultationFeeFollowUp, int FollowUpValidityDays, IReadOnlyList<string>? Languages, string? Bio,
    Guid? PhotoFileId, Guid? SignatureFileId, bool AvailableForOnlineBooking, bool AvailableForTelehealth, IReadOnlyList<Guid>? FacilityIds,
    bool IsActive, uint? Version);

public sealed record ServiceItemDto(
    Guid Id, string Code, string Name, string? ShortName, ServiceCategory Category, Guid? DepartmentId, decimal BasePrice, decimal TaxRate,
    string? SacCode, bool IsDiscountAllowed, bool IsPriceEditable, string? Description, bool IsActive, uint Version);

public sealed record SaveServiceItemRequest(
    string Code, string Name, string? ShortName, ServiceCategory Category, Guid? DepartmentId, decimal BasePrice, decimal TaxRate,
    string? SacCode, bool IsDiscountAllowed, bool IsPriceEditable, string? Description, bool IsActive, uint? Version);

public sealed class SaveFacilityRequestValidator : AbstractValidator<SaveFacilityRequest>
{
    public SaveFacilityRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[A-Z]{2,6}$").WithMessage("Use 2–6 capital letters.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Gstin).Gstin().When(x => !string.IsNullOrWhiteSpace(x.Gstin));
        RuleFor(x => x.Pan).Matches("^[A-Z]{5}[0-9]{4}[A-Z]$").When(x => !string.IsNullOrWhiteSpace(x.Pan)).WithMessage("Enter a valid PAN.");
        RuleFor(x => x.Address!.PinCode).PinCode().When(x => !string.IsNullOrWhiteSpace(x.Address?.PinCode));
    }
}

public sealed class SaveDepartmentRequestValidator : AbstractValidator<SaveDepartmentRequest>
{
    public SaveDepartmentRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches("^[A-Z0-9-]+$").WithMessage("Use capital letters, digits or dashes.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.TokenPrefix).MaximumLength(4).Matches("^[A-Z]*$").When(x => x.TokenPrefix is not null);
        RuleFor(x => x.Phone).PhoneNumber();
    }
}

public sealed class SaveSpecialtyRequestValidator : AbstractValidator<SaveSpecialtyRequest>
{
    public SaveSpecialtyRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
    }
}

public sealed class SavePractitionerRequestValidator : AbstractValidator<SavePractitionerRequest>
{
    public SavePractitionerRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16);
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).MaximumLength(100);
        RuleFor(x => x.Title).MaximumLength(16);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.ConsultationFeeNew).GreaterThanOrEqualTo(0);
        RuleFor(x => x.ConsultationFeeFollowUp).GreaterThanOrEqualTo(0);
        RuleFor(x => x.FollowUpValidityDays).InclusiveBetween(0, 90);
        RuleFor(x => x.RegistrationNumber).NotEmpty().When(x => x.Kind is PractitionerKind.Consultant or PractitionerKind.Resident or PractitionerKind.Visiting)
            .WithMessage("Medical council registration number is required for doctors.");
    }
}

public sealed class SaveServiceItemRequestValidator : AbstractValidator<SaveServiceItemRequest>
{
    public SaveServiceItemRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.BasePrice).GreaterThanOrEqualTo(0);
        RuleFor(x => x.TaxRate).InclusiveBetween(0, 28);
    }
}

/// <summary>Organisation masters: facilities, departments, specialities, practitioners and the service catalogue (HMS-03).</summary>
internal static class OrganizationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var masters = app.MapGroup("/api/v1/masters").WithTags("Masters");

        masters.MapGet("/facilities", async (HospitalDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await db.Set<Facility>().OrderBy(f => f.Name).Select(ToFacilityDto).ToListAsync(ct)))
            .RequirePermission(PlatformPermissions.MastersRead);
        masters.MapGet("/facilities/{id:guid}", async (Guid id, HospitalDbContext db, CancellationToken ct) =>
                await db.Set<Facility>().Where(f => f.Id == id).Select(ToFacilityDto).FirstOrDefaultAsync(ct) is { } dto
                    ? TypedResults.Ok(dto)
                    : Error.NotFound(nameof(Facility), id).ToProblem())
            .RequirePermission(PlatformPermissions.MastersRead);
        masters.MapPost("/facilities", (SaveFacilityRequest r, HospitalDbContext db, CancellationToken ct) => SaveFacility(null, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveFacilityRequest>();
        masters.MapPut("/facilities/{id:guid}", (Guid id, SaveFacilityRequest r, HospitalDbContext db, CancellationToken ct) => SaveFacility(id, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveFacilityRequest>();

        masters.MapGet("/departments", ListDepartments).RequirePermission(PlatformPermissions.MastersRead);
        masters.MapPost("/departments", (SaveDepartmentRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveDepartment(null, r, db, u, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveDepartmentRequest>();
        masters.MapPut("/departments/{id:guid}", (Guid id, SaveDepartmentRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveDepartment(id, r, db, u, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveDepartmentRequest>();

        masters.MapGet("/specialties", async (HospitalDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await db.Set<Specialty>().OrderBy(s => s.Name)
                    .Select(s => new SpecialtyDto(s.Id, s.Code, s.Name, s.Description, s.IsActive, s.Version)).ToListAsync(ct)))
            .RequirePermission(PlatformPermissions.MastersRead);
        masters.MapPost("/specialties", (SaveSpecialtyRequest r, HospitalDbContext db, CancellationToken ct) => SaveSpecialty(null, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveSpecialtyRequest>();
        masters.MapPut("/specialties/{id:guid}", (Guid id, SaveSpecialtyRequest r, HospitalDbContext db, CancellationToken ct) => SaveSpecialty(id, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveSpecialtyRequest>();

        masters.MapGet("/practitioners", ListPractitioners).RequirePermission(PlatformPermissions.MastersRead);
        masters.MapGet("/practitioners/{id:guid}", async (Guid id, HospitalDbContext db, CancellationToken ct) =>
                await PractitionerQuery(db).Where(p => p.Id == id).FirstOrDefaultAsync(ct) is { } dto
                    ? TypedResults.Ok(dto)
                    : Error.NotFound(nameof(Practitioner), id).ToProblem())
            .RequirePermission(PlatformPermissions.MastersRead);
        masters.MapPost("/practitioners", (SavePractitionerRequest r, HospitalDbContext db, CancellationToken ct) => SavePractitioner(null, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SavePractitionerRequest>();
        masters.MapPut("/practitioners/{id:guid}", (Guid id, SavePractitionerRequest r, HospitalDbContext db, CancellationToken ct) => SavePractitioner(id, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SavePractitionerRequest>();

        masters.MapGet("/services", ListServices).RequirePermission(PlatformPermissions.MastersRead);
        masters.MapGet("/services/{id:guid}", async (Guid id, HospitalDbContext db, CancellationToken ct) =>
                await db.Set<ServiceItem>().Where(s => s.Id == id).Select(ToServiceDto).FirstOrDefaultAsync(ct) is { } dto
                    ? TypedResults.Ok(dto)
                    : Error.NotFound(nameof(ServiceItem), id).ToProblem())
            .RequirePermission(PlatformPermissions.MastersRead);
        masters.MapPost("/services", (SaveServiceItemRequest r, HospitalDbContext db, CancellationToken ct) => SaveService(null, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveServiceItemRequest>();
        masters.MapPut("/services/{id:guid}", (Guid id, SaveServiceItemRequest r, HospitalDbContext db, CancellationToken ct) => SaveService(id, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveServiceItemRequest>();

        var lookups = masters.MapGroup("/lookups");
        lookups.MapGet("/departments", async (DepartmentKind? kind, HospitalDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await db.Set<Department>()
                    .Where(d => d.IsActive && (kind == null || d.Kind == kind))
                    .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Name)
                    .Select(d => new LookupItem(d.Id, d.Code, d.Name)).ToListAsync(ct)))
            .RequirePermission(PlatformPermissions.MastersRead);
        lookups.MapGet("/specialties", async (HospitalDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await db.Set<Specialty>().Where(s => s.IsActive).OrderBy(s => s.Name)
                    .Select(s => new LookupItem(s.Id, s.Code, s.Name)).ToListAsync(ct)))
            .RequirePermission(PlatformPermissions.MastersRead);
        lookups.MapGet("/practitioners", async (Guid? departmentId, string? search, HospitalDbContext db, ICurrentUser user, CancellationToken ct) =>
            {
                var query = db.Set<Practitioner>().Where(p => p.IsActive && (departmentId == null || p.DepartmentId == departmentId));
                if (user.FacilityId is { } facilityId)
                {
                    query = query.Where(p => p.FacilityIds.Count == 0 || p.FacilityIds.Contains(facilityId));
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var like = search.Trim().ToLikePattern();
                    query = query.Where(p => EF.Functions.ILike(p.DisplayName, like) || EF.Functions.ILike(p.Code, like));
                }

                return TypedResults.Ok(await query.OrderBy(p => p.DisplayName).Take(50).Select(p => new LookupItem(p.Id, p.Code, p.DisplayName)).ToListAsync(ct));
            })
            .RequirePermission(PlatformPermissions.MastersRead);
        lookups.MapGet("/services", async (ServiceCategory? category, string? search, HospitalDbContext db, CancellationToken ct) =>
            {
                var query = db.Set<ServiceItem>().Where(s => s.IsActive && (category == null || s.Category == category));
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var like = search.Trim().ToLikePattern();
                    query = query.Where(s => EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.Code, like));
                }

                return TypedResults.Ok(await query.OrderBy(s => s.Name).Take(50).Select(ToServiceDto).ToListAsync(ct));
            })
            .RequirePermission(PlatformPermissions.MastersRead);
    }

    // ---------------------------------------------------------------- facilities

    private static readonly Expression<Func<Facility, FacilityDto>> ToFacilityDto = f => new FacilityDto(
        f.Id, f.Code, f.Name, f.LegalName, f.Address, f.Phone, f.Email, f.Website, f.Gstin, f.Pan, f.RegistrationNumber, f.NabhNumber,
        f.HfrId, f.TimeZone, f.Currency, f.LogoFileId, f.LetterheadNote, f.IsActive, f.Version);

    private static async Task<IResult> SaveFacility(Guid? id, SaveFacilityRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Facility>().AnyAsync(f => f.Code == code && f.Id != id, ct))
        {
            return Error.Conflict("facility.duplicate_code", "Another facility already uses that code.").ToProblem();
        }

        Facility facility;
        if (id is { } existingId)
        {
            var found = await db.Set<Facility>().FirstOrDefaultAsync(f => f.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Facility), existingId).ToProblem();
            }

            facility = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(facility, v);
            }
        }
        else
        {
            facility = new Facility();
            db.Add(facility);
        }

        facility.Code = code;
        facility.Name = request.Name.Trim();
        facility.LegalName = request.LegalName?.Trim();
        facility.Address = request.Address ?? new Address();
        facility.Phone = Phone.Normalize(request.Phone);
        facility.Email = request.Email?.Trim();
        facility.Website = request.Website?.Trim();
        facility.Gstin = request.Gstin?.Trim().ToUpperInvariant();
        facility.Pan = request.Pan?.Trim().ToUpperInvariant();
        facility.RegistrationNumber = request.RegistrationNumber?.Trim();
        facility.NabhNumber = request.NabhNumber?.Trim();
        facility.HfrId = request.HfrId?.Trim();
        facility.LetterheadNote = request.LetterheadNote?.Trim();
        facility.LogoFileId = request.LogoFileId;
        facility.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/masters/facilities/{facility.Id}", new { facility.Id }) : TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- departments

    private static async Task<IResult> ListDepartments(DepartmentKind? kind, bool? active, string? search, HospitalDbContext db, CancellationToken ct)
    {
        var query = db.Set<Department>().AsQueryable();
        if (kind is not null)
        {
            query = query.Where(d => d.Kind == kind);
        }

        if (active is not null)
        {
            query = query.Where(d => d.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = search.Trim().ToLikePattern();
            query = query.Where(d => EF.Functions.ILike(d.Name, like) || EF.Functions.ILike(d.Code, like));
        }

        return TypedResults.Ok(await query.OrderBy(d => d.DisplayOrder).ThenBy(d => d.Name)
            .Select(d => new DepartmentDto(d.Id, d.FacilityId, d.Code, d.Name, d.Kind, d.SpecialtyId, d.HeadPractitionerId, d.Location, d.Phone,
                d.TokenPrefix, d.DisplayOrder, d.IsActive, d.Version))
            .ToListAsync(ct));
    }

    private static async Task<IResult> SaveDepartment(Guid? id, SaveDepartmentRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Department>().AnyAsync(d => d.Code == code && d.Id != id, ct))
        {
            return Error.Conflict("department.duplicate_code", "Another department in this facility uses that code.").ToProblem();
        }

        Department department;
        if (id is { } existingId)
        {
            var found = await db.Set<Department>().FirstOrDefaultAsync(d => d.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Department), existingId).ToProblem();
            }

            department = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(department, v);
            }
        }
        else
        {
            department = new Department { FacilityId = facilityId };
            db.Add(department);
        }

        department.Code = code;
        department.Name = request.Name.Trim();
        department.Kind = request.Kind;
        department.SpecialtyId = request.SpecialtyId;
        department.HeadPractitionerId = request.HeadPractitionerId;
        department.Location = request.Location?.Trim();
        department.Phone = Phone.Normalize(request.Phone);
        department.TokenPrefix = string.IsNullOrWhiteSpace(request.TokenPrefix) ? null : request.TokenPrefix.Trim().ToUpperInvariant();
        department.DisplayOrder = request.DisplayOrder;
        department.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/masters/departments/{department.Id}", new { department.Id }) : TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- specialties

    private static async Task<IResult> SaveSpecialty(Guid? id, SaveSpecialtyRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Specialty>().AnyAsync(s => s.Code == code && s.Id != id, ct))
        {
            return Error.Conflict("specialty.duplicate_code", "Another speciality uses that code.").ToProblem();
        }

        Specialty specialty;
        if (id is { } existingId)
        {
            var found = await db.Set<Specialty>().FirstOrDefaultAsync(s => s.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Specialty), existingId).ToProblem();
            }

            specialty = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(specialty, v);
            }
        }
        else
        {
            specialty = new Specialty();
            db.Add(specialty);
        }

        specialty.Code = code;
        specialty.Name = request.Name.Trim();
        specialty.Description = request.Description?.Trim();
        specialty.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/masters/specialties/{specialty.Id}", new { specialty.Id }) : TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- practitioners

    private static IQueryable<PractitionerDto> PractitionerQuery(HospitalDbContext db) =>
        from p in db.Set<Practitioner>()
        join s in db.Set<Specialty>() on p.SpecialtyId equals s.Id into specialties
        from s in specialties.DefaultIfEmpty()
        join d in db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.DepartmentId equals d.Id into departments
        from d in departments.DefaultIfEmpty()
        select new PractitionerDto(
            p.Id, p.Code, p.Title, p.FirstName, p.LastName, p.DisplayName, p.Gender, p.Qualification, p.RegistrationCouncil, p.RegistrationNumber,
            p.SpecialtyId, s != null ? s.Name : null, p.DepartmentId, d != null ? d.Name : null, p.Kind, p.Phone, p.Email,
            p.ConsultationFeeNew, p.ConsultationFeeFollowUp, p.FollowUpValidityDays, p.Languages, p.Bio, p.PhotoFileId, p.SignatureFileId,
            p.AvailableForOnlineBooking, p.AvailableForTelehealth, p.FacilityIds, p.IsActive, p.Version);

    private static async Task<IResult> ListPractitioners(
        [AsParameters] PageQuery query, Guid? departmentId, Guid? specialtyId, PractitionerKind? kind, bool? active, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var q = PractitionerQuery(db);
        if (departmentId is not null)
        {
            q = q.Where(p => p.DepartmentId == departmentId);
        }

        if (specialtyId is not null)
        {
            q = q.Where(p => p.SpecialtyId == specialtyId);
        }

        if (kind is not null)
        {
            q = q.Where(p => p.Kind == kind);
        }

        if (active is not null)
        {
            q = q.Where(p => p.IsActive == active);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            q = q.Where(p => EF.Functions.ILike(p.DisplayName, like) || EF.Functions.ILike(p.Code, like) || (p.RegistrationNumber != null && EF.Functions.ILike(p.RegistrationNumber, like)));
        }

        return TypedResults.Ok(await q.OrderBy(p => p.DisplayName).ToPagedResultAsync(page, ct));
    }

    private static async Task<IResult> SavePractitioner(Guid? id, SavePractitionerRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Practitioner>().AnyAsync(p => p.Code == code && p.Id != id, ct))
        {
            return Error.Conflict("practitioner.duplicate_code", "Another practitioner uses that code.").ToProblem();
        }

        Practitioner practitioner;
        if (id is { } existingId)
        {
            var found = await db.Set<Practitioner>().FirstOrDefaultAsync(p => p.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Practitioner), existingId).ToProblem();
            }

            practitioner = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(practitioner, v);
            }
        }
        else
        {
            practitioner = new Practitioner();
            db.Add(practitioner);
        }

        practitioner.Code = code;
        practitioner.Title = request.Title?.Trim() ?? string.Empty;
        practitioner.FirstName = request.FirstName.Trim();
        practitioner.LastName = request.LastName?.Trim();
        practitioner.DisplayName = Practitioner.ComposeDisplayName(practitioner.Title, practitioner.FirstName, practitioner.LastName);
        practitioner.Gender = request.Gender;
        practitioner.Qualification = request.Qualification?.Trim();
        practitioner.RegistrationCouncil = request.RegistrationCouncil?.Trim();
        practitioner.RegistrationNumber = request.RegistrationNumber?.Trim();
        practitioner.SpecialtyId = request.SpecialtyId;
        practitioner.DepartmentId = request.DepartmentId;
        practitioner.Kind = request.Kind;
        practitioner.Phone = Phone.Normalize(request.Phone);
        practitioner.Email = request.Email?.Trim();
        practitioner.ConsultationFeeNew = Money.Round(request.ConsultationFeeNew);
        practitioner.ConsultationFeeFollowUp = Money.Round(request.ConsultationFeeFollowUp);
        practitioner.FollowUpValidityDays = request.FollowUpValidityDays;
        practitioner.Languages = request.Languages?.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct().ToList() ?? [];
        practitioner.Bio = request.Bio?.Trim();
        practitioner.PhotoFileId = request.PhotoFileId;
        practitioner.SignatureFileId = request.SignatureFileId;
        practitioner.AvailableForOnlineBooking = request.AvailableForOnlineBooking;
        practitioner.AvailableForTelehealth = request.AvailableForTelehealth;
        practitioner.FacilityIds = request.FacilityIds?.Distinct().ToList() ?? [];
        practitioner.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/masters/practitioners/{practitioner.Id}", new { practitioner.Id }) : TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- services

    private static readonly Expression<Func<ServiceItem, ServiceItemDto>> ToServiceDto = s => new ServiceItemDto(
        s.Id, s.Code, s.Name, s.ShortName, s.Category, s.DepartmentId, s.BasePrice, s.TaxRate, s.SacCode, s.IsDiscountAllowed,
        s.IsPriceEditable, s.Description, s.IsActive, s.Version);

    private static async Task<IResult> ListServices(
        [AsParameters] PageQuery query, ServiceCategory? category, Guid? departmentId, bool? active, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var q = db.Set<ServiceItem>().AsQueryable();
        if (category is not null)
        {
            q = q.Where(s => s.Category == category);
        }

        if (departmentId is not null)
        {
            q = q.Where(s => s.DepartmentId == departmentId);
        }

        if (active is not null)
        {
            q = q.Where(s => s.IsActive == active);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            q = q.Where(s => EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.Code, like));
        }

        var sorts = new Dictionary<string, Expression<Func<ServiceItem, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] = s => s.Code,
            ["name"] = s => s.Name,
            ["price"] = s => s.BasePrice,
            ["category"] = s => s.Category,
        };

        return TypedResults.Ok(await q.OrderByKey(page, sorts, s => s.Name).Select(ToServiceDto).ToPagedResultAsync(page, ct));
    }

    private static async Task<IResult> SaveService(Guid? id, SaveServiceItemRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<ServiceItem>().AnyAsync(s => s.Code == code && s.Id != id, ct))
        {
            return Error.Conflict("service.duplicate_code", "Another service uses that code.").ToProblem();
        }

        ServiceItem service;
        if (id is { } existingId)
        {
            var found = await db.Set<ServiceItem>().FirstOrDefaultAsync(s => s.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(ServiceItem), existingId).ToProblem();
            }

            service = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(service, v);
            }
        }
        else
        {
            service = new ServiceItem();
            db.Add(service);
        }

        service.Code = code;
        service.Name = request.Name.Trim();
        service.ShortName = request.ShortName?.Trim();
        service.Category = request.Category;
        service.DepartmentId = request.DepartmentId;
        service.BasePrice = Money.Round(request.BasePrice);
        service.TaxRate = request.TaxRate;
        service.SacCode = request.SacCode?.Trim();
        service.IsDiscountAllowed = request.IsDiscountAllowed;
        service.IsPriceEditable = request.IsPriceEditable;
        service.Description = request.Description?.Trim();
        service.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/masters/services/{service.Id}", new { service.Id }) : TypedResults.NoContent();
    }
}
