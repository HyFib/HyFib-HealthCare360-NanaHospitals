using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;
using HyFib.HealthCare360.Modules.VirtualCare.Services;

namespace HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;

/// <summary>
/// /api/v1/portal/telehealth — the patient's own and family video consultations (TEL-01, PP-04), scoped by
/// <see cref="PortalPatientScope"/>. Portal users join the hub with their portal token; guests use the SMS link instead.
/// Off while either the portal or video consultations are switched off.
/// </summary>
internal static class PortalTelehealthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/portal/telehealth").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal)
            .RequireFeature(FeatureKeys.PatientPortal)
            .RequireFeature(FeatureKeys.Telehealth);
        group.MapGet("/sessions", List);
        group.MapGet("/sessions/{id:guid}/join", JoinInfo);
        group.MapGet("/sessions/{id:guid}/messages", Messages);
    }

    /// <summary>Upcoming consultations and those of the last 30 days, soonest first.</summary>
    private static async Task<IResult> List(PortalPatientScope patients, HospitalDbContext db, VirtualCareSettings settings, TimeProvider time, CancellationToken ct)
    {
        var ids = await patients.AccessiblePatientIdsAsync(ct);
        if (ids.Count == 0)
        {
            return TypedResults.Ok(Array.Empty<PortalTelehealthSession>());
        }

        var now = time.GetUtcNow();
        var since = now.AddDays(-30);
        var rows = await (
                from s in db.Set<TelehealthSession>().AsNoTracking()
                join p in db.Set<Patient>() on s.PatientId equals p.Id
                join pr in db.Set<Practitioner>() on s.PractitionerId equals pr.Id into practitioners
                from pr in practitioners.DefaultIfEmpty()
                where ids.Contains(s.PatientId) && s.ScheduledEndAt >= since
                orderby s.ScheduledStartAt
                select new { Session = s, PatientName = p.FullName, PractitionerName = pr != null ? pr.DisplayName : null })
            .Take(100)
            .ToListAsync(ct);

        var result = new List<PortalTelehealthSession>();
        foreach (var row in rows)
        {
            var earlyJoin = await settings.EarlyJoinAsync(row.Session.FacilityId, ct);
            result.Add(new PortalTelehealthSession(
                row.Session.Id, row.Session.PatientId, row.PatientName, row.Session.PractitionerId, row.PractitionerName ?? "Doctor",
                row.Session.AppointmentNumber, row.Session.Status, row.Session.ScheduledStartAt, row.Session.ScheduledEndAt,
                row.Session.JoinOpensAt(earlyJoin), row.Session.EnsureJoinable(now, earlyJoin).IsSuccess));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<IResult> JoinInfo(
        Guid id,
        PortalPatientScope patients,
        HospitalDbContext db,
        VirtualCareSettings settings,
        TimeProvider time,
        CancellationToken ct)
    {
        var session = await FindOwnAsync(id, patients, db, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(TelehealthSession), id).ToProblem();
        }

        var earlyJoin = await settings.EarlyJoinAsync(session.FacilityId, ct);
        var iceServers = await settings.IceServersAsync(session.FacilityId, ct);
        var doctor = await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == session.PractitionerId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
        var department = session.DepartmentId is { } departmentId
            ? await db.Set<Department>().AsNoTracking().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == session.PatientId).Select(p => p.FullName).FirstOrDefaultAsync(ct);

        return TypedResults.Ok(new TelehealthJoinInfo(
            session.Id, session.Status, session.ScheduledStartAt, session.ScheduledEndAt, doctor ?? "Doctor", department, patient ?? "Patient",
            session.EnsureJoinable(time.GetUtcNow(), earlyJoin).IsSuccess, session.JoinOpensAt(earlyJoin), TelehealthHub.Path,
            iceServers.Select(IceServerDto.From).ToList()));
    }

    private static async Task<IResult> Messages(Guid id, PortalPatientScope patients, HospitalDbContext db, CancellationToken ct)
    {
        var session = await FindOwnAsync(id, patients, db, ct);
        return session is null
            ? Error.NotFound(nameof(TelehealthSession), id).ToProblem()
            : TypedResults.Ok(await TelehealthQueries.MessagesAsync(db, id, ct));
    }

    private static async Task<TelehealthSession?> FindOwnAsync(Guid id, PortalPatientScope patients, HospitalDbContext db, CancellationToken ct)
    {
        var ids = await patients.AccessiblePatientIdsAsync(ct);
        return ids.Count == 0
            ? null
            : await db.Set<TelehealthSession>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && ids.Contains(s.PatientId), ct);
    }
}
