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
        // Cancelled at once (204), or, for an OT, procedure, implant or bed-day charge or one above the hospital's amount,
        // asked for (202) and cancelled when someone else who approves discounts approves (billing.charge_cancel).
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
        ChargeService service,
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
        var asked = await service.CancellationsAskedAsync(result.Items.Select(r => r.Charge.Id).ToList(), ct);
        var approvalAbove = new Dictionary<Guid, decimal>();
        foreach (var facilityId in result.Items.Select(r => r.Charge.FacilityId).Distinct())
        {
            approvalAbove[facilityId] = await service.CancelApprovalAboveAsync(facilityId, ct);
        }

        return TypedResults.Ok(result.Map(r => ChargeService.ToDto(r.Charge, r.PractitionerName) with
        {
            CancelNeedsApproval = ChargeService.CancelNeedsApproval(r.Charge, approvalAbove[r.Charge.FacilityId]),
            CancellationAskedAt = asked.TryGetValue(r.Charge.Id, out var at) ? at : null,
        }));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, ChargeService service, CancellationToken ct)
    {
        var charge = await db.Set<Charge>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (charge is null)
        {
            return Error.NotFound(nameof(Charge), id).ToProblem();
        }

        var doctor = charge.PractitionerId is { } pid ? await db.Set<Practitioner>().Where(p => p.Id == pid).Select(p => p.DisplayName).FirstOrDefaultAsync(ct) : null;
        var asked = await service.CancellationsAskedAsync([charge.Id], ct);
        return TypedResults.Ok(ChargeService.ToDto(charge, doctor) with
        {
            CancelNeedsApproval = ChargeService.CancelNeedsApproval(charge, await service.CancelApprovalAboveAsync(charge.FacilityId, ct)),
            CancellationAskedAt = asked.TryGetValue(charge.Id, out var at) ? at : null,
        });
    }

    private static async Task<IResult> Post(PostChargeRequest request, ChargeService charges, CancellationToken ct) =>
        (await charges.PostManualAsync(request, ct)).ToCreated(c => $"/api/v1/billing/charges/{c.Id}");

    private static async Task<IResult> Cancel(Guid id, CancelChargeRequest request, ChargeService charges, CancellationToken ct) =>
        (await charges.AskCancelAsync(id, request.Reason, ct)).ToHttpResult(outcome => outcome.Asked is { } asked
            ? TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked)
            : TypedResults.NoContent());
}
