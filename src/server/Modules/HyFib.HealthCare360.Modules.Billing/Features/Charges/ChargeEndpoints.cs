using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Charges;

/// <summary>/api/v1/billing/charges — captured services (MB-01): browse, manual charge, cancel an unbilled charge.</summary>
internal static class ChargeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/charges").WithTags("Billing - Charges");

        group.MapGet("/", List).RequireAnyPermission(BillingPermissions.ChargesRead, BillingPermissions.OpCreate, BillingPermissions.IpManage);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(BillingPermissions.ChargesRead, BillingPermissions.OpCreate, BillingPermissions.IpManage);
        group.MapPost("/", Post).RequirePermission(BillingPermissions.ChargesManage).WithValidation<PostChargeRequest>();
        group.MapPost("/{id:guid}/cancel", Cancel).RequirePermission(BillingPermissions.ChargesManage).WithValidation<CancelChargeRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        Guid? patientId,
        Guid? visitId,
        Guid? admissionId,
        ChargeStatus? status,
        bool? unbilledOnly,
        HospitalDbContext db,
        CancellationToken ct)
    {
        if (patientId is null && visitId is null && admissionId is null)
        {
            return Error.Field("patientId", "Filter by patient, visit or admission.").ToProblem();
        }

        var page = query.ToRequest();
        var charges = db.Set<Charge>().AsNoTracking();
        if (patientId is not null)
        {
            charges = charges.Where(c => c.PatientId == patientId);
        }

        if (visitId is not null)
        {
            charges = charges.Where(c => c.VisitId == visitId);
        }

        if (admissionId is not null)
        {
            charges = charges.Where(c => c.AdmissionId == admissionId);
        }

        if (status is not null)
        {
            charges = charges.Where(c => c.Status == status);
        }

        if (unbilledOnly == true)
        {
            charges = charges.Where(c => c.Status == ChargeStatus.Unbilled && c.BillId == null);
        }

        var result = await (
                from c in charges.OrderByDescending(c => c.ServiceDate)
                join p in db.Set<Practitioner>() on c.PractitionerId equals (Guid?)p.Id into doctors
                from p in doctors.DefaultIfEmpty()
                select new { Charge = c, PractitionerName = p == null ? null : p.DisplayName })
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(r => ChargeService.ToDto(r.Charge, r.PractitionerName)));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var charge = await db.Set<Charge>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (charge is null)
        {
            return Error.NotFound(nameof(Charge), id).ToProblem();
        }

        var doctor = charge.PractitionerId is { } pid ? await db.Set<Practitioner>().Where(p => p.Id == pid).Select(p => p.DisplayName).FirstOrDefaultAsync(ct) : null;
        return TypedResults.Ok(ChargeService.ToDto(charge, doctor));
    }

    private static async Task<IResult> Post(PostChargeRequest request, ChargeService charges, CancellationToken ct) =>
        (await charges.PostManualAsync(request, ct)).ToCreated(c => $"/api/v1/billing/charges/{c.Id}");

    private static async Task<IResult> Cancel(Guid id, CancelChargeRequest request, HospitalDbContext db, ChargeService charges, CancellationToken ct)
    {
        var charge = await db.Set<Charge>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (charge is null)
        {
            return Error.NotFound(nameof(Charge), id).ToProblem();
        }

        var cancelled = await charges.CancelChargeAsync(charge, request.Reason, ct);
        if (cancelled.IsFailure)
        {
            return cancelled.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
