using System.Linq.Expressions;
using FluentValidation;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
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

/// <summary>The letterhead details being edited, to preview before they are saved.</summary>
public sealed record LetterheadPreviewRequest(
    string? Name, string? LegalName, Address? Address, string? Phone, string? Email, string? Website, string? Gstin, string? Pan,
    string? RegistrationNumber, string? NabhNumber, string? LetterheadNote, Guid? LogoFileId);

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

/// <summary>An address as the organisation tables store it: each part within its column, and a 6-digit PIN code.</summary>
public sealed class AddressValidator : AbstractValidator<Address>
{
    public AddressValidator()
    {
        RuleFor(x => x.Line1).SafeText(200);
        RuleFor(x => x.Line2).SafeText(200);
        RuleFor(x => x.City).SafeText(100);
        RuleFor(x => x.District).SafeText(100);
        RuleFor(x => x.State).SafeText(100);
        RuleFor(x => x.PinCode).PinCode().When(x => !string.IsNullOrWhiteSpace(x.PinCode));
        RuleFor(x => x.Country).SafeText(100);
    }
}

public sealed class SaveFacilityRequestValidator : AbstractValidator<SaveFacilityRequest>
{
    public SaveFacilityRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[A-Z]{2,6}$").WithMessage("Use 2–6 capital letters.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.LegalName).SafeText(200);
        RuleFor(x => x.Address!).SetValidator(new AddressValidator()).When(x => x.Address is not null);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Email).MaximumLength(256).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Website).SafeText(256).Must(MasterRules.IsWebAddress).When(x => !string.IsNullOrWhiteSpace(x.Website))
            .WithMessage("Enter a web address such as www.nanahospitals.in.");
        RuleFor(x => x.Gstin).Gstin().When(x => !string.IsNullOrWhiteSpace(x.Gstin));
        RuleFor(x => x.Pan).Matches("^[A-Z]{5}[0-9]{4}[A-Z]$").When(x => !string.IsNullOrWhiteSpace(x.Pan)).WithMessage("Enter a valid PAN.");
        RuleFor(x => x.RegistrationNumber).SafeText(64);
        RuleFor(x => x.NabhNumber).SafeText(64);
        RuleFor(x => x.HfrId).SafeText(64);
        RuleFor(x => x.LetterheadNote).SafeText(300);
    }
}

public sealed class LetterheadPreviewRequestValidator : AbstractValidator<LetterheadPreviewRequest>
{
    public LetterheadPreviewRequestValidator()
    {
        RuleFor(x => x.Name).SafeText(200);
        RuleFor(x => x.LegalName).SafeText(200);
        RuleFor(x => x.Address!).SetValidator(new AddressValidator()).When(x => x.Address is not null);
        RuleFor(x => x.Phone).SafeText(32);
        RuleFor(x => x.Email).SafeText(256);
        RuleFor(x => x.Website).SafeText(256);
        RuleFor(x => x.Gstin).SafeText(15);
        RuleFor(x => x.Pan).SafeText(10);
        RuleFor(x => x.RegistrationNumber).SafeText(64);
        RuleFor(x => x.NabhNumber).SafeText(64);
        RuleFor(x => x.LetterheadNote).SafeText(300);
    }
}

public sealed class SaveDepartmentRequestValidator : AbstractValidator<SaveDepartmentRequest>
{
    public SaveDepartmentRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches("^[A-Z0-9-]+$").WithMessage("Use capital letters, digits or dashes.");
        RuleFor(x => x.Name).NotEmpty().SafeText(128);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Location).SafeText(128);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.TokenPrefix).MaximumLength(4).Matches("^[A-Z]*$").When(x => x.TokenPrefix is not null).WithMessage("Use up to 4 capital letters.");
        RuleFor(x => x.DisplayOrder).InclusiveBetween(0, 9999);
    }
}

public sealed class SaveSpecialtyRequestValidator : AbstractValidator<SaveSpecialtyRequest>
{
    public SaveSpecialtyRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches(MasterRules.CodePattern).WithMessage(MasterRules.CodeMessage);
        RuleFor(x => x.Name).NotEmpty().SafeText(128);
        RuleFor(x => x.Description).SafeText(500);
    }
}

public sealed class SavePractitionerRequestValidator : AbstractValidator<SavePractitionerRequest>
{
    public SavePractitionerRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches(MasterRules.CodePattern).WithMessage(MasterRules.CodeMessage);
        RuleFor(x => x.Title).SafeText(16);
        RuleFor(x => x.FirstName).NotEmpty().SafeText(100);
        RuleFor(x => x.LastName).SafeText(100);
        RuleFor(x => x.Gender).IsInEnum();
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Qualification).SafeText(200);
        RuleFor(x => x.RegistrationCouncil).SafeText(128);
        RuleFor(x => x.RegistrationNumber).SafeText(64);
        RuleFor(x => x.RegistrationNumber).NotEmpty().When(x => x.Kind is PractitionerKind.Consultant or PractitionerKind.Resident or PractitionerKind.Visiting)
            .WithMessage("Medical council registration number is required for doctors.");
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Email).MaximumLength(256).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.ConsultationFeeNew).InclusiveBetween(0, MasterRules.MaxFee).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.ConsultationFeeFollowUp).InclusiveBetween(0, MasterRules.MaxFee).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.FollowUpValidityDays).InclusiveBetween(0, 90);
        RuleFor(x => x.Languages).Must(l => l is null || l.Count <= 12).WithMessage("List at most 12 languages.");
        RuleForEach(x => x.Languages).NotEmpty().SafeText(40);
        RuleFor(x => x.Bio).SafeText(2000);
        RuleFor(x => x.FacilityIds).Must(f => f is null || f.Count <= 50).WithMessage("Choose at most 50 facilities.");
        RuleForEach(x => x.FacilityIds).NotEmpty();
    }
}

public sealed class SaveServiceItemRequestValidator : AbstractValidator<SaveServiceItemRequest>
{
    public SaveServiceItemRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Matches(MasterRules.CodePattern).WithMessage(MasterRules.CodeMessage);
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.ShortName).SafeText(64);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.BasePrice).InclusiveBetween(0, MasterRules.MaxPrice).PrecisionScale(12, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.TaxRate).InclusiveBetween(0, 28).PrecisionScale(5, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.SacCode).Matches("^[0-9]{4,8}$").When(x => !string.IsNullOrWhiteSpace(x.SacCode)).WithMessage("Enter the 4–8 digit SAC or HSN code.");
        RuleFor(x => x.Description).SafeText(1000);
    }
}

/// <summary>
/// The file store categories of the masters' images. The staff app uploads each image with its category, and a save accepts
/// only an image of the right one, so no other stored file (a patient's photo or document) can be put on a letterhead.
/// </summary>
public static class MasterImages
{
    public const string Logo = "logo";
    public const string Photo = "staff-photo";
    public const string Signature = "signature";
}

/// <summary>Rules the organisation masters share.</summary>
public static class MasterRules
{
    public const string CodePattern = "^[A-Za-z0-9][A-Za-z0-9._-]*$";
    public const string CodeMessage = "Use letters, digits, dots or dashes, starting with a letter or digit.";
    public const decimal MaxFee = 1_000_000m;
    public const decimal MaxPrice = 10_000_000m;

    /// <summary>
    /// Codes are permanent: modules find their records by code (the REG-NEW and CONS-* services, the LAB department, the
    /// facility code in UHIDs and bill numbers), so a renamed code would quietly break them.
    /// </summary>
    public const string PermanentCode = "A code can't be changed once it is saved: records and other screens refer to it.";

    /// <summary>"www.example.in" or a full http(s) address.</summary>
    public static bool IsWebAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        var withScheme = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && uri.Host.Contains('.', StringComparison.Ordinal)
            && !text.Any(char.IsWhiteSpace);
    }
}

/// <summary>Organisation masters: facilities, departments, specialities, practitioners and the service catalogue (HMS-03).</summary>
internal static class OrganizationEndpoints
{
    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/webp"];

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
        masters.MapPost("/facilities", (SaveFacilityRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveFacility(null, r, db, u, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveFacilityRequest>();
        masters.MapPut("/facilities/{id:guid}", (Guid id, SaveFacilityRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveFacility(id, r, db, u, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveFacilityRequest>();
        masters.MapPost("/facilities/letterhead-preview", PreviewLetterhead)
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<LetterheadPreviewRequest>();

        masters.MapGet("/departments", ListDepartments).RequirePermission(PlatformPermissions.MastersRead);
        masters.MapGet("/departments/{id:guid}", async (Guid id, HospitalDbContext db, CancellationToken ct) =>
                await db.Set<Department>().Where(d => d.Id == id).Select(ToDepartmentDto).FirstOrDefaultAsync(ct) is { } dto
                    ? TypedResults.Ok(dto)
                    : Error.NotFound(nameof(Department), id).ToProblem())
            .RequirePermission(PlatformPermissions.MastersRead);
        masters.MapPost("/departments", (SaveDepartmentRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveDepartment(null, r, db, u, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveDepartmentRequest>();
        masters.MapPut("/departments/{id:guid}", (Guid id, SaveDepartmentRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveDepartment(id, r, db, u, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveDepartmentRequest>();

        masters.MapGet("/specialties", async (HospitalDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await db.Set<Specialty>().OrderBy(s => s.Name).Select(ToSpecialtyDto).ToListAsync(ct)))
            .RequirePermission(PlatformPermissions.MastersRead);
        masters.MapGet("/specialties/{id:guid}", async (Guid id, HospitalDbContext db, CancellationToken ct) =>
                await db.Set<Specialty>().Where(s => s.Id == id).Select(ToSpecialtyDto).FirstOrDefaultAsync(ct) is { } dto
                    ? TypedResults.Ok(dto)
                    : Error.NotFound(nameof(Specialty), id).ToProblem())
            .RequirePermission(PlatformPermissions.MastersRead);
        masters.MapPost("/specialties", (SaveSpecialtyRequest r, HospitalDbContext db, CancellationToken ct) => SaveSpecialty(null, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveSpecialtyRequest>();
        masters.MapPut("/specialties/{id:guid}", (Guid id, SaveSpecialtyRequest r, HospitalDbContext db, CancellationToken ct) => SaveSpecialty(id, r, db, ct))
            .RequirePermission(PlatformPermissions.MastersManage).WithValidation<SaveSpecialtyRequest>();

        masters.MapGet("/practitioners", ListPractitioners).RequirePermission(PlatformPermissions.MastersRead);
        masters.MapGet("/practitioners/{id:guid}", async (Guid id, HospitalDbContext db, CancellationToken ct) =>
                await PractitionerQuery(db, db.Set<Practitioner>().Where(p => p.Id == id)).FirstOrDefaultAsync(ct) is { } dto
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
                    query = query.Where(p => EF.Functions.ILike(p.DisplayName, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(p.Code, like, QueryExtensions.LikeEscape));
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
                    query = query.Where(s => EF.Functions.ILike(s.Name, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(s.Code, like, QueryExtensions.LikeEscape));
                }

                return TypedResults.Ok(await query.OrderBy(s => s.Name).Take(50).Select(ToServiceDto).ToListAsync(ct));
            })
            .RequirePermission(PlatformPermissions.MastersRead);
    }

    // ---------------------------------------------------------------- facilities

    private static readonly Expression<Func<Facility, FacilityDto>> ToFacilityDto = f => new FacilityDto(
        f.Id, f.Code, f.Name, f.LegalName, f.Address, f.Phone, f.Email, f.Website, f.Gstin, f.Pan, f.RegistrationNumber, f.NabhNumber,
        f.HfrId, f.TimeZone, f.Currency, f.LogoFileId, f.LetterheadNote, f.IsActive, f.Version);

    private static async Task<IResult> SaveFacility(Guid? id, SaveFacilityRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        Facility facility;

        // Taking a facility out of use checks the others in use and saves in one transaction (see TakingOutOfUseProblemAsync).
        await using var transaction = id is not null && !request.IsActive ? await db.Database.BeginTransactionAsync(ct) : null;
        if (id is { } existingId)
        {
            var found = await db.Set<Facility>().FirstOrDefaultAsync(f => f.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Facility), existingId).ToProblem();
            }

            if (found.Code != code)
            {
                return Error.Field("code", MasterRules.PermanentCode).ToProblem();
            }

            if (found.IsActive && !request.IsActive && await TakingOutOfUseProblemAsync(found, db, user, ct) is { } rule)
            {
                return rule.ToProblem();
            }

            facility = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(facility, v);
            }
        }
        else
        {
            if (await db.Set<Facility>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AnyAsync(f => f.Code == code, ct))
            {
                return Error.Conflict("facility.duplicate_code", "Another facility already uses that code.").ToProblem();
            }

            facility = new Facility { Code = code };
        }

        var problems = new FieldProblems();
        problems.Add("logoFileId", await ImageProblemAsync(db, request.LogoFileId, id is null ? null : facility.LogoFileId, MasterImages.Logo, ct));
        if (problems.ToError() is { } invalid)
        {
            return invalid.ToProblem();
        }

        if (id is null)
        {
            db.Add(facility);
        }

        facility.Name = request.Name.Trim();
        facility.LegalName = Clean(request.LegalName);
        facility.Address = CleanAddress(request.Address);
        facility.Phone = Phone.Normalize(request.Phone);
        facility.Email = Clean(request.Email);
        facility.Website = Clean(request.Website);
        facility.Gstin = Clean(request.Gstin)?.ToUpperInvariant();
        facility.Pan = Clean(request.Pan)?.ToUpperInvariant();
        facility.RegistrationNumber = Clean(request.RegistrationNumber);
        facility.NabhNumber = Clean(request.NabhNumber);
        facility.HfrId = Clean(request.HfrId);
        facility.LetterheadNote = Clean(request.LetterheadNote);
        facility.LogoFileId = request.LogoFileId;
        facility.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return id is null ? TypedResults.Created($"/api/v1/masters/facilities/{facility.Id}", new { facility.Id }) : TypedResults.NoContent();
    }

    /// <summary>
    /// Staff sign in to a facility, and only to one in use: the facility the user is signed in to, and the last one in use,
    /// stay in use. The facilities in use are locked until the caller's transaction ends, so two administrators taking out
    /// the last two at the same moment are served one after the other, and the second sees the first one's change.
    /// </summary>
    private static async Task<Error?> TakingOutOfUseProblemAsync(Facility facility, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId == facility.Id)
        {
            return Error.Rule("facility.current", "You are signed in to this facility. Sign in to another facility to take this one out of use.");
        }

        var inUse = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM org.facilities WHERE is_active AND NOT is_deleted FOR UPDATE")
            .ToListAsync(ct);
        return inUse.Any(other => other != facility.Id)
            ? null
            : Error.Rule("facility.last_active", "At least one facility must stay in use, because staff sign in to a facility.");
    }

    /// <summary>
    /// The letterhead as documents will print it, from details not yet saved: the report letterhead with a sample report under
    /// it. The logo is shown only when it would be accepted on save.
    /// </summary>
    private static async Task<IResult> PreviewLetterhead(LetterheadPreviewRequest request, HospitalDbContext db, DocumentAssets assets, CancellationToken ct)
    {
        var logo = await ImageProblemAsync(db, request.LogoFileId, null, MasterImages.Logo, ct) is null ? await assets.ImageAsync(request.LogoFileId, ct) : null;
        var name = Clean(request.Name) ?? "Hospital name";
        var letterhead = new DocumentLetterhead(
            name,
            Clean(request.LegalName),
            Clean(CleanAddress(request.Address).ToString()),
            Phone.Normalize(request.Phone) ?? Clean(request.Phone),
            Clean(request.Email),
            Clean(request.Website),
            Clean(request.RegistrationNumber),
            Clean(request.NabhNumber),
            Clean(request.Gstin)?.ToUpperInvariant(),
            Clean(request.Pan)?.ToUpperInvariant(),
            Clean(request.LetterheadNote),
            logo);

        var billLine = string.Join(" · ", new[]
        {
            Clean(request.LegalName) ?? name,
            letterhead.Gstin is { } gstin ? $"GSTIN {gstin}" : null,
            letterhead.Pan is { } pan ? $"PAN {pan}" : null,
            letterhead.RegistrationNumber is { } registration ? $"Registration {registration}" : null,
        }.Where(part => part is not null));

        var document = new PrintableDocument
        {
            Title = "Letterhead preview",
            FileName = "letterhead-preview",
            Styles = DocumentParts.Styles,
            Body = Html.Of($"""
                {DocumentParts.Letterhead(letterhead)}
                {DocumentParts.Title("Sample report")}
                {DocumentParts.Fields([("Patient", "Sample patient"), ("UHID", "—"), ("Report no.", "—"), ("Date", "—")])}
                <p class="muted">Lab, radiology, cardiology and health check-up reports and MIS exports begin like this. Prescriptions and discharge summaries print the same details beside the doctor's name.</p>
                <p class="muted">Bills print: {billLine}</p>
                """),
        };

        return TypedResults.Ok(document.ToDto());
    }

    // ---------------------------------------------------------------- departments

    private static readonly Expression<Func<Department, DepartmentDto>> ToDepartmentDto = d => new DepartmentDto(
        d.Id, d.FacilityId, d.Code, d.Name, d.Kind, d.SpecialtyId, d.HeadPractitionerId, d.Location, d.Phone, d.TokenPrefix, d.DisplayOrder,
        d.IsActive, d.Version);

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
            query = query.Where(d => EF.Functions.ILike(d.Name, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(d.Code, like, QueryExtensions.LikeEscape));
        }

        return TypedResults.Ok(await query.OrderBy(d => d.DisplayOrder).ThenBy(d => d.Name).Select(ToDepartmentDto).ToListAsync(ct));
    }

    private static async Task<IResult> SaveDepartment(Guid? id, SaveDepartmentRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        Department department;
        if (id is { } existingId)
        {
            var found = await db.Set<Department>().FirstOrDefaultAsync(d => d.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Department), existingId).ToProblem();
            }

            if (found.Code != code)
            {
                return Error.Field("code", MasterRules.PermanentCode).ToProblem();
            }

            department = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(department, v);
            }
        }
        else
        {
            if (await db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AnyAsync(d => d.Code == code, ct))
            {
                return Error.Conflict("department.duplicate_code", "Another department in this facility uses that code.").ToProblem();
            }

            department = new Department { FacilityId = facilityId, Code = code };
        }

        var problems = new FieldProblems();
        problems.Add("specialtyId", await SpecialtyProblemAsync(db, request.SpecialtyId, id is null ? null : department.SpecialtyId, ct));
        problems.Add("headPractitionerId", await PractitionerProblemAsync(db, request.HeadPractitionerId, id is null ? null : department.HeadPractitionerId, ct));
        if (problems.ToError() is { } invalid)
        {
            return invalid.ToProblem();
        }

        if (id is null)
        {
            db.Add(department);
        }

        department.Name = request.Name.Trim();
        department.Kind = request.Kind;
        department.SpecialtyId = request.SpecialtyId;
        department.HeadPractitionerId = request.HeadPractitionerId;
        department.Location = Clean(request.Location);
        department.Phone = Phone.Normalize(request.Phone);
        department.TokenPrefix = Clean(request.TokenPrefix)?.ToUpperInvariant();
        department.DisplayOrder = request.DisplayOrder;
        department.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/masters/departments/{department.Id}", new { department.Id }) : TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- specialties

    private static readonly Expression<Func<Specialty, SpecialtyDto>> ToSpecialtyDto =
        s => new SpecialtyDto(s.Id, s.Code, s.Name, s.Description, s.IsActive, s.Version);

    private static async Task<IResult> SaveSpecialty(Guid? id, SaveSpecialtyRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        Specialty specialty;
        if (id is { } existingId)
        {
            var found = await db.Set<Specialty>().FirstOrDefaultAsync(s => s.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Specialty), existingId).ToProblem();
            }

            if (found.Code != code)
            {
                return Error.Field("code", MasterRules.PermanentCode).ToProblem();
            }

            specialty = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(specialty, v);
            }
        }
        else
        {
            if (await db.Set<Specialty>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AnyAsync(s => s.Code == code, ct))
            {
                return Error.Conflict("specialty.duplicate_code", "Another speciality uses that code.").ToProblem();
            }

            specialty = new Specialty { Code = code };
            db.Add(specialty);
        }

        specialty.Name = request.Name.Trim();
        specialty.Description = Clean(request.Description);
        specialty.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/masters/specialties/{specialty.Id}", new { specialty.Id }) : TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- practitioners

    /// <summary>
    /// The given practitioners as <see cref="PractitionerDto"/>, in name order. Filter the entities before projecting them:
    /// EF cannot translate a filter or a sort on a record built by its constructor.
    /// </summary>
    internal static IQueryable<PractitionerDto> PractitionerQuery(HospitalDbContext db, IQueryable<Practitioner> practitioners) =>
        from p in practitioners
        join s in db.Set<Specialty>() on p.SpecialtyId equals s.Id into specialties
        from s in specialties.DefaultIfEmpty()
        join d in db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.DepartmentId equals d.Id into departments
        from d in departments.DefaultIfEmpty()
        orderby p.DisplayName
        select new PractitionerDto(
            p.Id, p.Code, p.Title, p.FirstName, p.LastName, p.DisplayName, p.Gender, p.Qualification, p.RegistrationCouncil, p.RegistrationNumber,
            p.SpecialtyId, s != null ? s.Name : null, p.DepartmentId, d != null ? d.Name : null, p.Kind, p.Phone, p.Email,
            p.ConsultationFeeNew, p.ConsultationFeeFollowUp, p.FollowUpValidityDays, p.Languages, p.Bio, p.PhotoFileId, p.SignatureFileId,
            p.AvailableForOnlineBooking, p.AvailableForTelehealth, p.FacilityIds, p.IsActive, p.Version);

    private static async Task<IResult> ListPractitioners(
        [AsParameters] PageQuery query, Guid? departmentId, Guid? specialtyId, PractitionerKind? kind, bool? active, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var practitioners = FilterPractitioners(db.Set<Practitioner>(), departmentId, specialtyId, kind, active, page.SearchTerm);
        return TypedResults.Ok(await PractitionerQuery(db, practitioners).ToPagedResultAsync(page, ct));
    }

    internal static IQueryable<Practitioner> FilterPractitioners(
        IQueryable<Practitioner> q, Guid? departmentId, Guid? specialtyId, PractitionerKind? kind, bool? active, string? searchTerm)
    {
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

        if (searchTerm is not null)
        {
            var like = searchTerm.ToLikePattern();
            q = q.Where(p => EF.Functions.ILike(p.DisplayName, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(p.Code, like, QueryExtensions.LikeEscape) || (p.RegistrationNumber != null && EF.Functions.ILike(p.RegistrationNumber, like, QueryExtensions.LikeEscape)));
        }

        return q;
    }

    private static async Task<IResult> SavePractitioner(Guid? id, SavePractitionerRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        Practitioner practitioner;
        if (id is { } existingId)
        {
            var found = await db.Set<Practitioner>().FirstOrDefaultAsync(p => p.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Practitioner), existingId).ToProblem();
            }

            if (found.Code != code)
            {
                return Error.Field("code", MasterRules.PermanentCode).ToProblem();
            }

            practitioner = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(practitioner, v);
            }
        }
        else
        {
            if (await db.Set<Practitioner>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AnyAsync(p => p.Code == code, ct))
            {
                return Error.Conflict("practitioner.duplicate_code", "Another practitioner uses that code.").ToProblem();
            }

            practitioner = new Practitioner { Code = code };
        }

        var facilityIds = request.FacilityIds?.Distinct().ToList() ?? [];
        var problems = new FieldProblems();
        problems.Add("specialtyId", await SpecialtyProblemAsync(db, request.SpecialtyId, id is null ? null : practitioner.SpecialtyId, ct));
        problems.Add("departmentId", await DepartmentProblemAsync(db, request.DepartmentId, id is null ? null : practitioner.DepartmentId, ct));
        problems.Add("facilityIds", await FacilitiesProblemAsync(db, facilityIds, id is null ? [] : practitioner.FacilityIds, ct));
        problems.Add("photoFileId", await ImageProblemAsync(db, request.PhotoFileId, id is null ? null : practitioner.PhotoFileId, MasterImages.Photo, ct));
        problems.Add("signatureFileId", await ImageProblemAsync(db, request.SignatureFileId, id is null ? null : practitioner.SignatureFileId, MasterImages.Signature, ct));
        if (problems.ToError() is { } invalid)
        {
            return invalid.ToProblem();
        }

        if (id is null)
        {
            db.Add(practitioner);
        }

        practitioner.Title = Clean(request.Title) ?? string.Empty;
        practitioner.FirstName = request.FirstName.Trim();
        practitioner.LastName = Clean(request.LastName);
        practitioner.DisplayName = Practitioner.ComposeDisplayName(practitioner.Title, practitioner.FirstName, practitioner.LastName);
        practitioner.Gender = request.Gender;
        practitioner.Qualification = Clean(request.Qualification);
        practitioner.RegistrationCouncil = Clean(request.RegistrationCouncil);
        practitioner.RegistrationNumber = Clean(request.RegistrationNumber);
        practitioner.SpecialtyId = request.SpecialtyId;
        practitioner.DepartmentId = request.DepartmentId;
        practitioner.Kind = request.Kind;
        practitioner.Phone = Phone.Normalize(request.Phone);
        practitioner.Email = Clean(request.Email);
        practitioner.ConsultationFeeNew = Money.Round(request.ConsultationFeeNew);
        practitioner.ConsultationFeeFollowUp = Money.Round(request.ConsultationFeeFollowUp);
        practitioner.FollowUpValidityDays = request.FollowUpValidityDays;
        practitioner.Languages = request.Languages?.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        practitioner.Bio = Clean(request.Bio);
        practitioner.PhotoFileId = request.PhotoFileId;
        practitioner.SignatureFileId = request.SignatureFileId;
        practitioner.AvailableForOnlineBooking = request.AvailableForOnlineBooking;
        practitioner.AvailableForTelehealth = request.AvailableForTelehealth;
        practitioner.FacilityIds = facilityIds;
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
            q = q.Where(s => EF.Functions.ILike(s.Name, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(s.Code, like, QueryExtensions.LikeEscape));
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
        ServiceItem service;
        if (id is { } existingId)
        {
            var found = await db.Set<ServiceItem>().FirstOrDefaultAsync(s => s.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(ServiceItem), existingId).ToProblem();
            }

            if (found.Code != code)
            {
                return Error.Field("code", MasterRules.PermanentCode).ToProblem();
            }

            service = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(service, v);
            }
        }
        else
        {
            if (await db.Set<ServiceItem>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AnyAsync(s => s.Code == code, ct))
            {
                return Error.Conflict("service.duplicate_code", "Another service uses that code.").ToProblem();
            }

            service = new ServiceItem { Code = code };
        }

        var problems = new FieldProblems();
        problems.Add("departmentId", await DepartmentProblemAsync(db, request.DepartmentId, id is null ? null : service.DepartmentId, ct));
        if (problems.ToError() is { } invalid)
        {
            return invalid.ToProblem();
        }

        if (id is null)
        {
            db.Add(service);
        }

        service.Name = request.Name.Trim();
        service.ShortName = Clean(request.ShortName);
        service.Category = request.Category;
        service.DepartmentId = request.DepartmentId;
        service.BasePrice = Money.Round(request.BasePrice);
        service.TaxRate = request.TaxRate;
        service.SacCode = Clean(request.SacCode);
        service.IsDiscountAllowed = request.IsDiscountAllowed;
        service.IsPriceEditable = request.IsPriceEditable;
        service.Description = Clean(request.Description);
        service.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/masters/services/{service.Id}", new { service.Id }) : TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- references

    // A reference is checked only when it changes, so a record that already points at something since taken out of use can
    // still be saved; a new choice must be in use.

    private static async Task<string?> SpecialtyProblemAsync(HospitalDbContext db, Guid? id, Guid? current, CancellationToken ct) =>
        id is { } value && value != current && !await db.Set<Specialty>().AnyAsync(s => s.Id == value && s.IsActive, ct)
            ? "Choose a speciality that is in use."
            : null;

    // Practitioners and services serve every facility, so their department may belong to any of them.
    private static async Task<string?> DepartmentProblemAsync(HospitalDbContext db, Guid? id, Guid? current, CancellationToken ct) =>
        id is { } value && value != current
        && !await db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(d => d.Id == value && d.IsActive, ct)
            ? "Choose a department that is in use."
            : null;

    private static async Task<string?> PractitionerProblemAsync(HospitalDbContext db, Guid? id, Guid? current, CancellationToken ct) =>
        id is { } value && value != current && !await db.Set<Practitioner>().AnyAsync(p => p.Id == value && p.IsActive, ct)
            ? "Choose a doctor who is in use."
            : null;

    private static async Task<string?> FacilitiesProblemAsync(HospitalDbContext db, List<Guid> ids, IReadOnlyCollection<Guid> current, CancellationToken ct)
    {
        var added = ids.Except(current).ToList();
        return added.Count > 0 && await db.Set<Facility>().CountAsync(f => added.Contains(f.Id) && f.IsActive, ct) != added.Count
            ? "Choose facilities that are in use."
            : null;
    }

    /// <summary>
    /// A logo, photo or signature must be an image uploaded as one (file category <paramref name="category"/>, see
    /// <see cref="MasterImages"/>) that documents can embed: PNG, JPEG or WebP of at most <see cref="DocumentAssets.MaxImageBytes"/>.
    /// The category is what keeps out every other stored image, including patients' photos, which carry no patient id on
    /// the file itself.
    /// </summary>
    internal static async Task<string?> ImageProblemAsync(HospitalDbContext db, Guid? id, Guid? current, string category, CancellationToken ct)
    {
        if (id is not { } value || value == current)
        {
            return null;
        }

        var file = await db.Set<StoredFile>().AsNoTracking().Where(f => f.Id == value)
            .Select(f => new { f.ContentType, f.SizeBytes, f.PatientId, f.Category })
            .FirstOrDefaultAsync(ct);
        return file switch
        {
            null => "The image was not found. Upload it again.",
            _ when file.PatientId is not null || file.Category != category => "Upload the image here: only an image uploaded for it can be used.",
            _ when !ImageTypes.Contains(file.ContentType) => "Use a PNG, JPEG or WebP image.",
            _ when file.SizeBytes > DocumentAssets.MaxImageBytes => "Use an image of 300 KB or less: documents leave out bigger images.",
            _ => null,
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Address CleanAddress(Address? address) => address is null
        ? new Address()
        : new Address
        {
            Line1 = Clean(address.Line1),
            Line2 = Clean(address.Line2),
            City = Clean(address.City),
            District = Clean(address.District),
            State = Clean(address.State),
            PinCode = Clean(address.PinCode),
            Country = Clean(address.Country) ?? "India",
        };

    /// <summary>Field problems found against the database, answered together as one 400 validation problem.</summary>
    private sealed class FieldProblems
    {
        private readonly Dictionary<string, string[]> _fields = new(StringComparer.Ordinal);

        public void Add(string field, string? message)
        {
            if (message is not null)
            {
                _fields[field] = [message];
            }
        }

        public Error? ToError() => _fields.Count == 0 ? null : Error.Validation("validation", "Please correct the highlighted fields.", _fields);
    }
}
