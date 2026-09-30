using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;

/// <summary>/api/v1/inpatient/admissions — admit, list, view, edit, transfer, cancel, bed history, charges and consultant visits (ADT, WB-03/04).</summary>
internal static class AdmissionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inpatient/admissions").WithTags("Admissions");

        group.MapGet("/", List).RequirePermission(InpatientPermissions.AdmissionsRead);
        group.MapGet("/{id:guid}", Get).RequirePermission(InpatientPermissions.AdmissionsRead);
        group.MapPost("/", async (AdmitPatientRequest r, AdmissionService admissions, CancellationToken ct) =>
                (await admissions.AdmitAsync(r, ct)).ToCreated(a => $"/api/v1/inpatient/admissions/{a.Id}"))
            .RequirePermission(InpatientPermissions.Admit).WithValidation<AdmitPatientRequest>();
        group.MapPut("/{id:guid}", async (Guid id, UpdateAdmissionRequest r, AdmissionService admissions, CancellationToken ct) =>
                (await admissions.UpdateAsync(id, r, ct)).ToNoContent())
            .RequirePermission(InpatientPermissions.Admit).WithValidation<UpdateAdmissionRequest>();
        group.MapPost("/{id:guid}/transfer", async (Guid id, TransferPatientRequest r, AdmissionService admissions, CancellationToken ct) =>
                (await admissions.TransferAsync(id, r, ct)).ToNoContent())
            .RequirePermission(InpatientPermissions.Transfer).WithValidation<TransferPatientRequest>();
        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelAdmissionRequest r, AdmissionService admissions, CancellationToken ct) =>
                (await admissions.CancelAsync(id, r, ct)).ToNoContent())
            .RequirePermission(InpatientPermissions.Admit).WithValidation<CancelAdmissionRequest>();

        group.MapGet("/{id:guid}/bed-history", BedHistory).RequirePermission(InpatientPermissions.AdmissionsRead);
        group.MapGet("/{id:guid}/charges", Charges).RequirePermission(InpatientPermissions.AdmissionsRead);
        group.MapGet("/{id:guid}/consultant-visits", Visits)
            .RequireAnyPermission(InpatientPermissions.AdmissionsRead, InpatientPermissions.NursingChart);
        group.MapPost("/{id:guid}/consultant-visits", async (Guid id, RecordConsultantVisitRequest r, InpatientChargeService charges, CancellationToken ct) =>
                (await charges.RecordVisitAsync(id, r, ct)).ToCreated(v => $"/api/v1/inpatient/admissions/{id}/consultant-visits"))
            .RequirePermission(InpatientPermissions.NursingChart).WithValidation<RecordConsultantVisitRequest>();
        group.MapPost("/consultant-visits/{visitId:guid}/cancel", async (Guid visitId, CancelConsultantVisitRequest r, InpatientChargeService charges, CancellationToken ct) =>
                (await charges.CancelVisitAsync(visitId, r.Reason, ct)).ToNoContent())
            .RequirePermission(InpatientPermissions.NursingChart).WithValidation<CancelConsultantVisitRequest>();

        app.MapGet("/api/v1/patients/{patientId:guid}/admissions", ForPatient)
            .WithTags("Admissions")
            .RequirePermission(InpatientPermissions.AdmissionsRead);
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        AdmissionStatus? status,
        bool? activeOnly,
        Guid? wardId,
        Guid? consultantId,
        DateOnly? from,
        DateOnly? to,
        HospitalDbContext db,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var admissions = db.Set<Admission>().AsNoTracking();
        if (activeOnly == true)
        {
            admissions = admissions.Where(a => a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated);
        }

        if (status is not null)
        {
            admissions = admissions.Where(a => a.Status == status);
        }

        if (wardId is not null)
        {
            admissions = admissions.Where(a => a.WardId == wardId);
        }

        if (consultantId is not null)
        {
            admissions = admissions.Where(a => a.ConsultantPractitionerId == consultantId);
        }

        if (from is { } fromDate)
        {
            var start = IndiaClock.StartOfDayUtc(fromDate);
            admissions = admissions.Where(a => a.AdmittedAt >= start);
        }

        if (to is { } toDate)
        {
            var end = IndiaClock.StartOfDayUtc(toDate.AddDays(1));
            admissions = admissions.Where(a => a.AdmittedAt < end);
        }

        if (page.SearchTerm is { } term)
        {
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            var number = term.ToUpperInvariant();
            admissions = admissions.Where(a => matching.Contains(a.PatientId) || a.AdmissionNumber == number);
        }

        var now = time.GetUtcNow();
        var today = time.Today();
        var result = await (
                from a in admissions
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                join w in db.Set<Ward>() on a.WardId equals w.Id into wards
                from w in wards.DefaultIfEmpty()
                join b in db.Set<Bed>() on a.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                join pr in db.Set<Practitioner>() on a.ConsultantPractitionerId equals pr.Id into consultants
                from pr in consultants.DefaultIfEmpty()
                orderby a.AdmittedAt descending
                select new
                {
                    a.Id,
                    a.AdmissionNumber,
                    a.PatientId,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    a.Status,
                    a.Type,
                    a.AdmittedAt,
                    a.DischargedAt,
                    a.WardId,
                    WardName = w != null ? w.Name : null,
                    a.BedId,
                    BedLabel = b != null ? b.Label : null,
                    a.ConsultantPractitionerId,
                    ConsultantName = pr != null ? pr.DisplayName : null,
                    a.PayerType,
                    a.IsMlc,
                })
            .ToPagedResultAsync(page, ct);

        return TypedResults.Ok(result.Map(a => new AdmissionListItem(
            a.Id, a.AdmissionNumber, a.PatientId, a.Uhid, a.FullName, a.Gender, a.DateOfBirth is { } dob ? Age.Format(dob, today) : null, a.Status, a.Type,
            a.AdmittedAt, a.DischargedAt, a.WardId, a.WardName, a.BedId, a.BedLabel, a.ConsultantPractitionerId, a.ConsultantName,
            LengthOfStay.Days(a.AdmittedAt, a.DischargedAt ?? now), a.PayerType, a.IsMlc)));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var a = await db.Set<Admission>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            return Error.NotFound(nameof(Admission), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(a.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == a.PatientId)
            .Select(p => new { p.Uhid, p.FullName, p.Gender, p.DateOfBirth })
            .FirstAsync(ct);
        var practitionerIds = new[] { a.AdmittingPractitionerId, a.ConsultantPractitionerId }.Where(x => x != null).Select(x => x!.Value).ToList();
        var practitioners = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => practitionerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var visitNumber = await db.Set<Visit>().Where(v => v.Id == a.VisitId).Select(v => v.VisitNumber).FirstOrDefaultAsync(ct);
        var departmentName = a.DepartmentId is { } departmentId
            ? await db.Set<Department>().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;
        var wardName = a.WardId is { } wardId ? await db.Set<Ward>().Where(w => w.Id == wardId).Select(w => w.Name).FirstOrDefaultAsync(ct) : null;
        var bedLabel = a.BedId is { } bedId ? await db.Set<Bed>().Where(b => b.Id == bedId).Select(b => b.Label).FirstOrDefaultAsync(ct) : null;

        await audit.PatientRecordAccessedAsync(a.PatientId, $"Viewed admission {a.AdmissionNumber}", ct);
        var now = time.GetUtcNow();
        return TypedResults.Ok(new AdmissionDetail(
            a.Id, a.AdmissionNumber, a.PatientId, patient.Uhid, patient.FullName, patient.Gender,
            patient.DateOfBirth is { } dob ? Age.Format(dob, time.Today()) : null, a.VisitId, visitNumber, a.SourceVisitId, a.Type, a.Source, a.Status,
            a.AdmittedAt, a.AdmittingPractitionerId, Name(practitioners, a.AdmittingPractitionerId), a.ConsultantPractitionerId,
            Name(practitioners, a.ConsultantPractitionerId), a.DepartmentId, departmentName, a.WardId, wardName, a.BedId, bedLabel, a.ProvisionalDiagnosis,
            a.ReferredBy, a.PayerType, a.PayerId, a.PolicyNumberMasked, a.IsMlc, a.MlcNumber, a.ExpectedDischargeDate, a.AttendantName, a.AttendantRelation,
            a.AttendantPhone, a.Notes, LengthOfStay.Days(a.AdmittedAt, a.DischargedAt ?? now), a.DischargeInitiatedAt, a.DischargeType, a.DischargedAt,
            a.DischargeNotes, a.CancelledAt, a.CancelReason, a.Version));
    }

    private static async Task<IResult> BedHistory(Guid id, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await (
                from s in db.Set<BedAssignment>().AsNoTracking()
                join b in db.Set<Bed>() on s.BedId equals b.Id
                join w in db.Set<Ward>() on s.WardId equals w.Id
                where s.AdmissionId == id
                orderby s.FromAt
                select new BedAssignmentDto(s.Id, s.BedId, b.Label, s.WardId, w.Name, s.Reason, s.TransferReason, s.FromAt, s.ToAt))
            .ToListAsync(ct));

    /// <summary>Bed and nursing day charges this module has posted (for billing reconciliation).</summary>
    private static async Task<IResult> Charges(Guid id, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await (
                from c in db.Set<BedDayCharge>().AsNoTracking()
                join s in db.Set<ServiceItem>() on c.ServiceItemId equals s.Id into services
                from s in services.DefaultIfEmpty()
                where c.AdmissionId == id
                orderby c.ChargeDate, c.Kind
                select new DailyChargeDto(c.ChargeDate, c.Kind, c.ServiceItemId, s != null ? s.Code : null, c.SourceReference, c.ChargeId, c.PostedAt, c.CancelledAt))
            .ToListAsync(ct));

    private static async Task<IResult> Visits(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var rows = await (
                from v in db.Set<ConsultantVisit>().AsNoTracking()
                join p in db.Set<Practitioner>() on v.PractitionerId equals p.Id into practitioners
                from p in practitioners.DefaultIfEmpty()
                where v.AdmissionId == id
                orderby v.VisitedAt descending
                select new { Visit = v, Name = p != null ? p.DisplayName : null })
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(r => InpatientChargeService.ToDto(r.Visit, r.Name)).ToList());
    }

    private static async Task<IResult> ForPatient(Guid patientId, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, "Viewed admission history", ct);
        var now = time.GetUtcNow();
        var rows = await (
                from a in db.Set<Admission>().AsNoTracking()
                join w in db.Set<Ward>() on a.WardId equals w.Id into wards
                from w in wards.DefaultIfEmpty()
                join pr in db.Set<Practitioner>() on a.ConsultantPractitionerId equals pr.Id into consultants
                from pr in consultants.DefaultIfEmpty()
                where a.PatientId == patientId
                orderby a.AdmittedAt descending
                select new
                {
                    a.Id,
                    a.AdmissionNumber,
                    a.Status,
                    a.Type,
                    a.AdmittedAt,
                    a.DischargedAt,
                    a.WardId,
                    WardName = w != null ? w.Name : null,
                    a.ConsultantPractitionerId,
                    ConsultantName = pr != null ? pr.DisplayName : null,
                    a.DischargeType,
                })
            .Take(100)
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(r => new PatientAdmissionItem(
            r.Id, r.AdmissionNumber, r.Status, r.Type, r.AdmittedAt, r.DischargedAt, r.WardId, r.WardName, r.ConsultantPractitionerId, r.ConsultantName,
            r.DischargeType, LengthOfStay.Days(r.AdmittedAt, r.DischargedAt ?? now))).ToList());
    }

    private static string? Name(Dictionary<Guid, string> names, Guid? id) => id is { } key && names.TryGetValue(key, out var name) ? name : null;
}
