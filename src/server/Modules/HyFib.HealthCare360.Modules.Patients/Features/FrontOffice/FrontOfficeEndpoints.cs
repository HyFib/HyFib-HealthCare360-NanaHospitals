using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;

public sealed record EnquiryDto(
    Guid Id, string Name, string? Phone, EnquiryType Type, EnquiryChannel Channel, string Details, EnquiryStatus Status,
    string? Response, Guid? PatientId, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, uint Version);

public sealed record SaveEnquiryRequest(
    string Name, string? Phone, EnquiryType Type, EnquiryChannel Channel, string Details, EnquiryStatus Status, string? Response, Guid? PatientId, uint? Version);

public sealed record VisitorPassDto(
    Guid Id, string PassNumber, Guid? PatientId, string? PatientName, string? PatientUhid, string VisitorName, string? Phone, string? Relation,
    string? IdProofType, string? IdProofLast4, string? Purpose, string? Location, bool IsAttendant, DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil, VisitorPassStatus Status, DateTimeOffset? CheckedOutAt);

public sealed record IssueVisitorPassRequest(
    Guid? PatientId, string VisitorName, string? Phone, string? Relation, string? IdProofType, string? IdProofNumber, string? Purpose,
    string? Location, bool IsAttendant, int ValidHours);

public sealed record ConsentDto(
    Guid Id, ConsentType Type, string ConsentVersion, bool Granted, DateTimeOffset CapturedAt, string? WitnessName, string? SignedByRelation,
    string? SignedByName, Guid? DocumentFileId, DateTimeOffset? RevokedAt, string? RevokedReason, string? Notes, bool IsActive);

public sealed record CaptureConsentRequest(
    ConsentType Type, string? ConsentVersion, bool Granted, string? WitnessName, string? SignedByRelation, string? SignedByName, Guid? DocumentFileId, string? Notes);

public sealed record RevokeConsentRequest(string Reason);

public sealed record PatientDocumentDto(
    Guid Id, Guid FileId, PatientDocumentType Type, string Title, DateOnly? DocumentDate, string? Source, string? Notes, DateTimeOffset CreatedAt,
    string? FileName, string? ContentType, long? SizeBytes);

public sealed record AttachDocumentRequest(Guid FileId, PatientDocumentType Type, string Title, DateOnly? DocumentDate, string? Source, string? Notes);

public sealed record DepartmentQueueStat(Guid DepartmentId, string DepartmentName, int Waiting, int Served, string? NowServing);

public sealed record VisitTypeStat(VisitType Type, int Count);

public sealed record FrontOfficeDashboard(
    int RegistrationsToday, int VisitsToday, int OpenVisits, int CompletedVisits, int WaitingTokens, double? AverageWaitMinutes,
    int OpenEnquiries, int VisitorsInside, IReadOnlyList<DepartmentQueueStat> Departments, IReadOnlyList<VisitTypeStat> VisitsByType);

public sealed class SaveEnquiryRequestValidator : AbstractValidator<SaveEnquiryRequest>
{
    public SaveEnquiryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Details).NotEmpty().SafeText(2000);
        RuleFor(x => x.Response).SafeText(2000);
    }
}

public sealed class IssueVisitorPassRequestValidator : AbstractValidator<IssueVisitorPassRequest>
{
    public IssueVisitorPassRequestValidator()
    {
        RuleFor(x => x.VisitorName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Relation).MaximumLength(64);
        RuleFor(x => x.IdProofType).MaximumLength(64);
        RuleFor(x => x.IdProofNumber).MaximumLength(32);
        RuleFor(x => x.Purpose).MaximumLength(200);
        RuleFor(x => x.Location).MaximumLength(128);
        RuleFor(x => x.ValidHours).InclusiveBetween(1, 24 * 7);
    }
}

public sealed class CaptureConsentRequestValidator : AbstractValidator<CaptureConsentRequest>
{
    public CaptureConsentRequestValidator()
    {
        RuleFor(x => x.ConsentVersion).MaximumLength(16);
        RuleFor(x => x.WitnessName).MaximumLength(200);
        RuleFor(x => x.SignedByName).NotEmpty().When(x => !string.IsNullOrWhiteSpace(x.SignedByRelation))
            .WithMessage("Enter the name of the person consenting on the patient's behalf.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class AttachDocumentRequestValidator : AbstractValidator<AttachDocumentRequest>
{
    public AttachDocumentRequestValidator()
    {
        RuleFor(x => x.FileId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Source).MaximumLength(200);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

/// <summary>Enquiries, visitor passes, consents, documents and the front-office dashboard (FO-*, EHR-04/06).</summary>
internal static class FrontOfficeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var office = app.MapGroup("/api/v1/front-office").WithTags("Front Office");

        office.MapGet("/dashboard", Dashboard).RequirePermission(PatientsPermissions.Dashboard);

        office.MapGet("/enquiries", ListEnquiries).RequirePermission(PatientsPermissions.Enquiries);
        office.MapPost("/enquiries", (SaveEnquiryRequest r, HospitalDbContext db, ICurrentUser u, TimeProvider t, CancellationToken ct) => SaveEnquiry(null, r, db, u, t, ct))
            .RequirePermission(PatientsPermissions.Enquiries).WithValidation<SaveEnquiryRequest>();
        office.MapPut("/enquiries/{id:guid}", (Guid id, SaveEnquiryRequest r, HospitalDbContext db, ICurrentUser u, TimeProvider t, CancellationToken ct) => SaveEnquiry(id, r, db, u, t, ct))
            .RequirePermission(PatientsPermissions.Enquiries).WithValidation<SaveEnquiryRequest>();

        office.MapGet("/visitor-passes", ListVisitorPasses).RequirePermission(PatientsPermissions.Visitors);
        office.MapPost("/visitor-passes", IssueVisitorPass).RequirePermission(PatientsPermissions.Visitors).WithValidation<IssueVisitorPassRequest>();
        office.MapPost("/visitor-passes/{id:guid}/checkout", (Guid id, HospitalDbContext db, TimeProvider t, CancellationToken ct) => CloseVisitorPass(id, VisitorPassStatus.CheckedOut, db, t, ct))
            .RequirePermission(PatientsPermissions.Visitors);
        office.MapPost("/visitor-passes/{id:guid}/revoke", (Guid id, HospitalDbContext db, TimeProvider t, CancellationToken ct) => CloseVisitorPass(id, VisitorPassStatus.Revoked, db, t, ct))
            .RequirePermission(PatientsPermissions.Visitors);

        var patients = app.MapGroup("/api/v1/patients").WithTags("Patients");
        patients.MapGet("/{patientId:guid}/consents", ListConsents).RequirePermission(PatientsPermissions.Read);
        patients.MapPost("/{patientId:guid}/consents", CaptureConsent).RequirePermission(PatientsPermissions.Consents).WithValidation<CaptureConsentRequest>();
        patients.MapPost("/consents/{consentId:guid}/revoke", RevokeConsent).RequirePermission(PatientsPermissions.Consents).WithValidation<RevokeConsentRequest>();
        patients.MapGet("/{patientId:guid}/documents", ListDocuments).RequirePermission(PatientsPermissions.Documents);
        patients.MapPost("/{patientId:guid}/documents", AttachDocument).RequirePermission(PatientsPermissions.Documents).WithValidation<AttachDocumentRequest>();
        patients.MapDelete("/documents/{documentId:guid}", RemoveDocument).RequirePermission(PatientsPermissions.Documents);
    }

    // ------------------------------------------------------------------ dashboard

    private static async Task<IResult> Dashboard(HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = time.Today();
        var dayStart = VisitService.StartOfLocalDay(now);

        var registrations = await db.Set<Patient>().CountAsync(p => p.RegisteredAt >= dayStart && p.RegisteredFacilityId == user.FacilityId, ct);
        var visitsToday = db.Set<Visit>().Where(v => v.VisitedAt >= dayStart);
        var byType = await visitsToday.GroupBy(v => v.Type).Select(g => new VisitTypeStat(g.Key, g.Count())).ToListAsync(ct);
        var open = await visitsToday.CountAsync(v => v.Status == VisitStatus.Open || v.Status == VisitStatus.InProgress, ct);
        var completed = await visitsToday.CountAsync(v => v.Status == VisitStatus.Completed, ct);

        var tokens = await db.Set<QueueToken>().AsNoTracking().Where(t => t.TokenDate == today)
            .Select(t => new { t.DepartmentId, t.Status, t.Label, t.IssuedAt, t.CalledAt, t.Sequence })
            .ToListAsync(ct);
        var waits = tokens.Where(t => t.CalledAt != null).Select(t => (t.CalledAt!.Value - t.IssuedAt).TotalMinutes).ToList();

        var departmentNames = await db.Set<Department>().AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var departments = tokens.GroupBy(t => t.DepartmentId).Select(g => new DepartmentQueueStat(
                g.Key,
                departmentNames.GetValueOrDefault(g.Key, "Department"),
                g.Count(t => t.Status == TokenStatus.Waiting),
                g.Count(t => t.Status == TokenStatus.Completed),
                g.Where(t => t.Status is TokenStatus.Called or TokenStatus.InService).OrderByDescending(t => t.CalledAt).Select(t => t.Label).FirstOrDefault()))
            .OrderByDescending(d => d.Waiting)
            .ToList();

        var openEnquiries = await db.Set<Enquiry>().CountAsync(e => e.Status == EnquiryStatus.Open || e.Status == EnquiryStatus.InProgress, ct);
        var visitorsInside = await db.Set<VisitorPass>().CountAsync(v => v.Status == VisitorPassStatus.Active && v.ValidUntil > now, ct);

        return TypedResults.Ok(new FrontOfficeDashboard(
            registrations, byType.Sum(t => t.Count), open, completed, tokens.Count(t => t.Status == TokenStatus.Waiting),
            waits.Count == 0 ? null : Math.Round(waits.Average(), 1), openEnquiries, visitorsInside, departments, byType));
    }

    // ------------------------------------------------------------------ enquiries

    private static async Task<IResult> ListEnquiries([AsParameters] PageQuery query, EnquiryStatus? status, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var enquiries = db.Set<Enquiry>().AsNoTracking();
        if (status is not null)
        {
            enquiries = enquiries.Where(e => e.Status == status);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            enquiries = enquiries.Where(e => EF.Functions.ILike(e.Name, like) || (e.Phone != null && e.Phone.Contains(term)) || EF.Functions.ILike(e.Details, like));
        }

        return TypedResults.Ok(await enquiries.OrderByDescending(e => e.CreatedAt)
            .Select(e => new EnquiryDto(e.Id, e.Name, e.Phone, e.Type, e.Channel, e.Details, e.Status, e.Response, e.PatientId, e.CreatedAt, e.ResolvedAt, e.Version))
            .ToPagedResultAsync(page, ct));
    }

    private static async Task<IResult> SaveEnquiry(Guid? id, SaveEnquiryRequest request, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        Enquiry enquiry;
        if (id is { } existingId)
        {
            var found = await db.Set<Enquiry>().FirstOrDefaultAsync(e => e.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Enquiry), existingId).ToProblem();
            }

            enquiry = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(enquiry, version);
            }
        }
        else
        {
            if (user.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.").ToProblem();
            }

            enquiry = new Enquiry { FacilityId = facilityId, AssignedToUserId = user.UserId };
            db.Add(enquiry);
        }

        enquiry.Name = request.Name.Trim();
        enquiry.Phone = Phone.Normalize(request.Phone);
        enquiry.Type = request.Type;
        enquiry.Channel = request.Channel;
        enquiry.Details = request.Details.Trim();
        enquiry.Response = request.Response?.Trim();
        enquiry.PatientId = request.PatientId;
        if (enquiry.Status != request.Status && request.Status is EnquiryStatus.Resolved or EnquiryStatus.Converted)
        {
            enquiry.ResolvedAt = time.GetUtcNow();
        }

        enquiry.Status = request.Status;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/front-office/enquiries/{enquiry.Id}", new { enquiry.Id }) : TypedResults.NoContent();
    }

    // ------------------------------------------------------------------ visitor passes

    private static async Task<IResult> ListVisitorPasses([AsParameters] PageQuery query, bool? activeOnly, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var now = time.GetUtcNow();
        var passes = db.Set<VisitorPass>().AsNoTracking();
        if (activeOnly != false)
        {
            passes = passes.Where(v => v.Status == VisitorPassStatus.Active && v.ValidUntil > now);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            passes = passes.Where(v => EF.Functions.ILike(v.VisitorName, like) || v.PassNumber == term.ToUpper());
        }

        var result = await (
                from v in passes
                join p in db.Set<Patient>() on v.PatientId equals p.Id into patients
                from p in patients.DefaultIfEmpty()
                orderby v.ValidFrom descending
                select new VisitorPassDto(
                    v.Id, v.PassNumber, v.PatientId, p != null ? p.FullName : null, p != null ? p.Uhid : null, v.VisitorName, v.Phone, v.Relation,
                    v.IdProofType, v.IdProofLast4, v.Purpose, v.Location, v.IsAttendant, v.ValidFrom, v.ValidUntil,
                    v.Status == VisitorPassStatus.Active && v.ValidUntil <= now ? VisitorPassStatus.Expired : v.Status, v.CheckedOutAt))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> IssueVisitorPass(
        IssueVisitorPassRequest request, HospitalDbContext db, INumberSeries numbers, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        if (request.IsAttendant && request.PatientId is { } patientId)
        {
            var attendants = await db.Set<VisitorPass>().CountAsync(v => v.PatientId == patientId && v.IsAttendant && v.Status == VisitorPassStatus.Active && v.ValidUntil > time.GetUtcNow(), ct);
            if (attendants >= 1)
            {
                return Error.Rule("visitor.attendant_limit", "This patient already has an active attendant pass. Check it out before issuing another.").ToProblem();
            }
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var pass = new VisitorPass
        {
            FacilityId = facilityId,
            PassNumber = await numbers.NextAsync("visitor-pass", facilityId, "VP{YY}{MM}{DD}-{SEQ:4}", NumberResetPolicy.Daily, ct),
            PatientId = request.PatientId,
            VisitorName = request.VisitorName.Trim(),
            Phone = Phone.Normalize(request.Phone),
            Relation = request.Relation?.Trim(),
            IdProofType = request.IdProofType?.Trim(),
            IdProofLast4 = string.IsNullOrWhiteSpace(request.IdProofNumber) ? null : Masking.Normalize(request.IdProofNumber)[^Math.Min(4, Masking.Normalize(request.IdProofNumber).Length)..],
            Purpose = request.Purpose?.Trim(),
            Location = request.Location?.Trim(),
            IsAttendant = request.IsAttendant,
            ValidFrom = now,
            ValidUntil = now.AddHours(request.ValidHours),
            Status = VisitorPassStatus.Active,
        };
        db.Add(pass);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/v1/front-office/visitor-passes/{pass.Id}", new { pass.Id, pass.PassNumber, pass.ValidUntil });
    }

    private static async Task<IResult> CloseVisitorPass(Guid id, VisitorPassStatus status, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var pass = await db.Set<VisitorPass>().FirstOrDefaultAsync(v => v.Id == id, ct);
        if (pass is null)
        {
            return Error.NotFound(nameof(VisitorPass), id).ToProblem();
        }

        if (pass.Status != VisitorPassStatus.Active)
        {
            return Error.Rule("visitor.closed", "This pass is already closed.").ToProblem();
        }

        pass.Status = status;
        pass.CheckedOutAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ------------------------------------------------------------------ consents

    private static async Task<IResult> ListConsents(Guid patientId, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<PatientConsent>().AsNoTracking()
            .Where(c => c.PatientId == patientId)
            .OrderByDescending(c => c.CapturedAt)
            .Select(c => new ConsentDto(c.Id, c.Type, c.ConsentVersion, c.Granted, c.CapturedAt, c.WitnessName, c.SignedByRelation, c.SignedByName,
                c.DocumentFileId, c.RevokedAt, c.RevokedReason, c.Notes, c.Granted && c.RevokedAt == null))
            .ToListAsync(ct));

    private static async Task<IResult> CaptureConsent(Guid patientId, CaptureConsentRequest request, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Set<Patient>().AnyAsync(p => p.Id == patientId, ct))
        {
            return Error.NotFound(nameof(Patient), patientId).ToProblem();
        }

        var consent = new PatientConsent
        {
            PatientId = patientId,
            Type = request.Type,
            ConsentVersion = string.IsNullOrWhiteSpace(request.ConsentVersion) ? "1.0" : request.ConsentVersion.Trim(),
            Granted = request.Granted,
            CapturedAt = time.GetUtcNow(),
            CapturedByUserId = user.UserId,
            WitnessName = request.WitnessName?.Trim(),
            SignedByRelation = request.SignedByRelation?.Trim(),
            SignedByName = request.SignedByName?.Trim(),
            DocumentFileId = request.DocumentFileId,
            Notes = request.Notes?.Trim(),
        };
        db.Add(consent);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/patients/{patientId}/consents", new { consent.Id });
    }

    private static async Task<IResult> RevokeConsent(Guid consentId, RevokeConsentRequest request, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var consent = await db.Set<PatientConsent>().FirstOrDefaultAsync(c => c.Id == consentId, ct);
        if (consent is null)
        {
            return Error.NotFound("Consent", consentId).ToProblem();
        }

        if (consent.RevokedAt is not null)
        {
            return Error.Rule("consent.revoked", "This consent is already revoked.").ToProblem();
        }

        consent.RevokedAt = time.GetUtcNow();
        consent.RevokedReason = request.Reason.Trim();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ------------------------------------------------------------------ documents

    private static async Task<IResult> ListDocuments(Guid patientId, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, "Listed documents", ct);
        var documents = await (
                from d in db.Set<PatientDocument>().AsNoTracking()
                join f in db.Set<StoredFile>() on d.FileId equals f.Id into files
                from f in files.DefaultIfEmpty()
                where d.PatientId == patientId
                orderby d.CreatedAt descending
                select new PatientDocumentDto(d.Id, d.FileId, d.Type, d.Title, d.DocumentDate, d.Source, d.Notes, d.CreatedAt,
                    f != null ? f.FileName : null, f != null ? f.ContentType : null, f != null ? f.SizeBytes : null))
            .ToListAsync(ct);
        return TypedResults.Ok(documents);
    }

    private static async Task<IResult> AttachDocument(Guid patientId, AttachDocumentRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var file = await db.Set<StoredFile>().FirstOrDefaultAsync(f => f.Id == request.FileId, ct);
        if (file is null)
        {
            return Error.Field("fileId", "Upload the file first.").ToProblem();
        }

        if (file.PatientId is { } owner && owner != patientId)
        {
            return Error.Rule("document.other_patient", "This file belongs to a different patient.").ToProblem();
        }

        file.PatientId = patientId;
        var document = new PatientDocument
        {
            PatientId = patientId,
            FileId = request.FileId,
            Type = request.Type,
            Title = request.Title.Trim(),
            DocumentDate = request.DocumentDate,
            Source = request.Source?.Trim(),
            Notes = request.Notes?.Trim(),
        };
        db.Add(document);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/patients/{patientId}/documents", new { document.Id });
    }

    private static async Task<IResult> RemoveDocument(Guid documentId, HospitalDbContext db, CancellationToken ct)
    {
        var document = await db.Set<PatientDocument>().FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null)
        {
            return Error.NotFound("Document", documentId).ToProblem();
        }

        db.Remove(document);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
