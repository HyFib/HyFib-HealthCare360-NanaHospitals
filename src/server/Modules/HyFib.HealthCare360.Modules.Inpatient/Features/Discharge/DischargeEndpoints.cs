using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;

/// <summary>/api/v1/inpatient/admissions/{id}/discharge — initiate, revoke, billing clearance and completion; plus the pending-discharge worklist.</summary>
internal static class DischargeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inpatient/admissions/{id:guid}/discharge").WithTags("Discharge");

        group.MapPost("/initiate", async (Guid id, InitiateDischargeRequest r, DischargeService discharges, CancellationToken ct) =>
                (await discharges.InitiateAsync(id, r, ct)).ToOk())
            .RequirePermission(InpatientPermissions.Discharge).WithValidation<InitiateDischargeRequest>();
        group.MapPost("/revoke", async (Guid id, RevokeDischargeRequest r, DischargeService discharges, CancellationToken ct) =>
                (await discharges.RevokeAsync(id, r, ct)).ToOk())
            .RequirePermission(InpatientPermissions.Discharge).WithValidation<RevokeDischargeRequest>();
        group.MapGet("/clearance", async (Guid id, DischargeService discharges, CancellationToken ct) => (await discharges.ClearanceAsync(id, ct)).ToOk())
            .RequireAnyPermission(InpatientPermissions.Discharge, InpatientPermissions.AdmissionsRead);
        group.MapPost("/complete", async (Guid id, CompleteDischargeRequest r, DischargeService discharges, CancellationToken ct) =>
                (await discharges.CompleteAsync(id, r, ct)).ToOk())
            .RequirePermission(InpatientPermissions.Discharge).WithValidation<CompleteDischargeRequest>();

        app.MapGet("/api/v1/inpatient/discharges/pending", Pending)
            .WithTags("Discharge")
            .RequirePermission(InpatientPermissions.AdmissionsRead);
    }

    /// <summary>Admissions waiting in discharge (worklist for nursing, billing and the insurance desk), oldest first.</summary>
    private static async Task<IResult> Pending(Guid? wardId, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var admissions = db.Set<Admission>().AsNoTracking().Where(a => a.Status == AdmissionStatus.DischargeInitiated);
        if (wardId is not null)
        {
            admissions = admissions.Where(a => a.WardId == wardId);
        }

        var rows = await (
                from a in admissions
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                join w in db.Set<Ward>() on a.WardId equals w.Id into wards
                from w in wards.DefaultIfEmpty()
                join b in db.Set<Bed>() on a.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                join pr in db.Set<Practitioner>() on a.ConsultantPractitionerId equals pr.Id into consultants
                from pr in consultants.DefaultIfEmpty()
                orderby a.DischargeInitiatedAt
                select new
                {
                    a.Id, a.AdmissionNumber, a.PatientId, p.Uhid, p.FullName, WardName = w != null ? w.Name : null, BedLabel = b != null ? b.Label : null,
                    ConsultantName = pr != null ? pr.DisplayName : null, a.DischargeInitiatedAt, a.ExpectedDischargeDate,
                })
            .Take(500)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        return TypedResults.Ok(rows.Select(r => new PendingDischargeItem(
            r.Id, r.AdmissionNumber, r.PatientId, r.Uhid, r.FullName, r.WardName, r.BedLabel, r.ConsultantName, r.DischargeInitiatedAt ?? now,
            r.ExpectedDischargeDate, (int)Math.Max(0, (now - (r.DischargeInitiatedAt ?? now)).TotalMinutes))).ToList());
    }
}
