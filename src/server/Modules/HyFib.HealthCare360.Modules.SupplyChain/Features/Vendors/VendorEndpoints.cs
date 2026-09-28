using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;

public sealed record VendorListItem(
    Guid Id,
    string Code,
    string Name,
    string? Gstin,
    string? DrugLicenseNumber,
    DateOnly? DrugLicenseValidUntil,
    bool DrugLicenseExpired,
    string? ContactPerson,
    string? Phone,
    string? City,
    int PaymentTermsDays,
    decimal Rating,
    bool IsActive);

public sealed record VendorDetail(
    Guid Id,
    string Code,
    string Name,
    string? Gstin,
    string? Pan,
    string? DrugLicenseNumber,
    DateOnly? DrugLicenseValidUntil,
    string? ContactPerson,
    string? Phone,
    string? Email,
    Address Address,
    int PaymentTermsDays,
    decimal Rating,
    string? Notes,
    bool IsActive,
    uint Version);

public sealed record SaveVendorRequest(
    string Code,
    string Name,
    string? Gstin,
    string? Pan,
    string? DrugLicenseNumber,
    DateOnly? DrugLicenseValidUntil,
    string? ContactPerson,
    string? Phone,
    string? Email,
    Address? Address,
    int PaymentTermsDays,
    decimal Rating,
    string? Notes,
    bool IsActive,
    uint? Version);

public sealed class SaveVendorRequestValidator : AbstractValidator<SaveVendorRequest>
{
    public SaveVendorRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Must(CatalogRules.ValidCode).WithMessage("Use letters, digits, '-', '_' or '/'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.Gstin).Gstin().When(x => !string.IsNullOrWhiteSpace(x.Gstin));
        RuleFor(x => x.Pan).Matches("^[A-Z]{5}[0-9]{4}[A-Z]$").When(x => !string.IsNullOrWhiteSpace(x.Pan)).WithMessage("PAN has 5 letters, 4 digits and a letter.");

        // A GSTIN embeds the holder's PAN (characters 3–12); a mismatch means one of them is mistyped.
        RuleFor(x => x)
            .Must(x => string.IsNullOrWhiteSpace(x.Gstin) || string.IsNullOrWhiteSpace(x.Pan) || x.Gstin.Length < 12 || x.Gstin.Substring(2, 10) == x.Pan)
            .WithName("pan").WithMessage("The PAN does not match the one inside the GSTIN.");
        RuleFor(x => x.DrugLicenseNumber).MaximumLength(64);
        RuleFor(x => x.ContactPerson).SafeText(120);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address!.PinCode).PinCode().When(x => !string.IsNullOrWhiteSpace(x.Address?.PinCode));
        RuleFor(x => x.PaymentTermsDays).InclusiveBetween(0, 365);
        RuleFor(x => x.Rating).InclusiveBetween(0, 5);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

/// <summary>/api/v1/purchase/vendors — vendor master shared by all facilities (INV vendor master).</summary>
internal static class VendorEndpoints
{
    private static readonly Dictionary<string, Expression<Func<Vendor, object?>>> Sort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = v => v.Code,
        ["name"] = v => v.Name,
        ["rating"] = v => v.Rating,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/purchase/vendors").WithTags("Purchase");

        group.MapGet("/", List).RequireAnyPermission(SupplyChainPermissions.VendorReaders);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(SupplyChainPermissions.VendorReaders);
        group.MapPost("/", (SaveVendorRequest r, HospitalDbContext db, CancellationToken ct) => Save(null, r, db, ct))
            .RequirePermission(SupplyChainPermissions.PurchaseManage).WithValidation<SaveVendorRequest>();
        group.MapPut("/{id:guid}", (Guid id, SaveVendorRequest r, HospitalDbContext db, CancellationToken ct) => Save(id, r, db, ct))
            .RequirePermission(SupplyChainPermissions.PurchaseManage).WithValidation<SaveVendorRequest>();
    }

    private static async Task<IResult> List([AsParameters] PageQuery query, bool? includeInactive, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var vendors = db.Set<Vendor>().AsNoTracking();
        if (includeInactive != true)
        {
            vendors = vendors.Where(v => v.IsActive);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            var upper = term.ToUpperInvariant();
            vendors = vendors.Where(v => EF.Functions.ILike(v.Name, like) || v.Code == upper || v.Gstin == upper);
        }

        var today = HospitalClock.Today(time);
        var result = await vendors.OrderByKey(page, Sort, v => v.Name)
            .Select(v => new VendorListItem(
                v.Id, v.Code, v.Name, v.Gstin, v.DrugLicenseNumber, v.DrugLicenseValidUntil,
                v.DrugLicenseValidUntil != null && v.DrugLicenseValidUntil < today,
                v.ContactPerson, v.Phone, v.Address.City, v.PaymentTermsDays, v.Rating, v.IsActive))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var v = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return v is null
            ? Error.NotFound(nameof(Vendor), id).ToProblem()
            : TypedResults.Ok(new VendorDetail(v.Id, v.Code, v.Name, v.Gstin, v.Pan, v.DrugLicenseNumber, v.DrugLicenseValidUntil, v.ContactPerson,
                v.Phone, v.Email, v.Address, v.PaymentTermsDays, v.Rating, v.Notes, v.IsActive, v.Version));
    }

    private static async Task<IResult> Save(Guid? id, SaveVendorRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        var gstin = string.IsNullOrWhiteSpace(request.Gstin) ? null : request.Gstin.Trim().ToUpperInvariant();
        if (await db.Set<Vendor>().AnyAsync(v => v.Code == code && v.Id != id, ct))
        {
            return Error.Conflict("vendor.duplicate_code", $"Vendor code {code} is already used.").ToProblem();
        }

        if (gstin is not null)
        {
            var holder = await db.Set<Vendor>().Where(v => v.Gstin == gstin && v.Id != id).Select(v => v.Name).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Conflict("vendor.duplicate_gstin", $"GSTIN {gstin} is already registered for {holder}.").ToProblem();
            }
        }

        Vendor vendor;
        if (id is { } existingId)
        {
            var found = await db.Set<Vendor>().FirstOrDefaultAsync(v => v.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Vendor), existingId).ToProblem();
            }

            vendor = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(vendor, version);
            }
        }
        else
        {
            vendor = new Vendor();
            db.Add(vendor);
        }

        vendor.Code = code;
        vendor.Name = request.Name.Trim();
        vendor.Gstin = gstin;
        vendor.Pan = string.IsNullOrWhiteSpace(request.Pan) ? null : request.Pan.Trim().ToUpperInvariant();
        vendor.DrugLicenseNumber = request.DrugLicenseNumber?.Trim();
        vendor.DrugLicenseValidUntil = request.DrugLicenseValidUntil;
        vendor.ContactPerson = request.ContactPerson?.Trim();
        vendor.Phone = Phone.Normalize(request.Phone);
        vendor.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
        vendor.Address = request.Address ?? new Address();
        vendor.PaymentTermsDays = request.PaymentTermsDays;
        vendor.Rating = request.Rating;
        vendor.Notes = request.Notes?.Trim();
        vendor.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/purchase/vendors/{vendor.Id}", new { vendor.Id }) : TypedResults.NoContent();
    }
}
