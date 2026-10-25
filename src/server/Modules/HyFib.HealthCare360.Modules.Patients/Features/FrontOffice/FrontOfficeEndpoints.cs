using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

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

/// <summary>A consent and its history: who recorded it, and, once withdrawn, when, by whom and why.</summary>
public sealed record ConsentDto(
    Guid Id, ConsentType Type, string ConsentVersion, bool Granted, DateTimeOffset CapturedAt, string? CapturedByName, string? WitnessName,
    string? SignedByRelation, string? SignedByName, Guid? DocumentFileId, DateTimeOffset? RevokedAt, string? RevokedReason, string? RevokedByName,
    string? Notes, bool IsActive);

public sealed record CaptureConsentRequest(
    ConsentType Type, string? ConsentVersion, bool Granted, string? WitnessName, string? SignedByRelation, string? SignedByName, Guid? DocumentFileId, string? Notes);

public sealed record RevokeConsentRequest(string Reason);

/// <summary>
/// The WhatsApp choices of the patient's mobile number (INT-WA), shown with the consents. They belong to the number, so
/// family members registered with it share them.
/// </summary>
/// <param name="Phone">The number, masked; null when the patient has none.</param>
/// <param name="CapturedByName">Who recorded the latest opt-in at the desk or in the portal; null for a START reply.</param>
/// <param name="OptedOut">A STOP reply (or the portal's "off") is in force: only sign-in codes go on WhatsApp.</param>
/// <param name="Reachable">Reminders and campaigns may go on WhatsApp: opted in, and no STOP in force.</param>
public sealed record WhatsAppConsentDto(
    string? Phone, bool OptedIn, DateTimeOffset? OptedInAt, WhatsAppOptInSource? Source, string? CapturedByName, bool OptedOut,
    DateTimeOffset? OptedOutAt, bool Reachable);

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
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.ConsentVersion).SafeText(16);
        RuleFor(x => x.WitnessName).SafeText(200);
        RuleFor(x => x.SignedByRelation).SafeText(64);
        RuleFor(x => x.SignedByName).SafeText(200);
        RuleFor(x => x.SignedByName).NotEmpty().When(x => !string.IsNullOrWhiteSpace(x.SignedByRelation))
            .WithMessage("Enter the name of the person consenting on the patient's behalf.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class RevokeConsentRequestValidator : AbstractValidator<RevokeConsentRequest>
{
    public RevokeConsentRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say why the consent is withdrawn.").SafeText(300);
    }
}

public sealed class AttachDocumentRequestValidator : AbstractValidator<AttachDocumentRequest>
{
    public AttachDocumentRequestValidator(TimeProvider time)
    {
        // The hospital's date (IST), which is a day ahead of UTC's between midnight and 05:30.
        var today = time.Today();
        RuleFor(x => x.FileId).NotEmpty();
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Title).NotEmpty().SafeText(200);
        RuleFor(x => x.DocumentDate).Must(d => d is null || d <= today).WithMessage("The document date cannot be in the future.");
        RuleFor(x => x.Source).SafeText(200);
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
            .RequirePermission(PatientsPermissions.Enquiries).WithValidation<SaveEnquiryRequest>().RequireVersion();

        office.MapGet("/visitor-passes", ListVisitorPasses).RequirePermission(PatientsPermissions.Visitors);
        office.MapPost("/visitor-passes", IssueVisitorPass).RequirePermission(PatientsPermissions.Visitors).WithValidation<IssueVisitorPassRequest>();
        office.MapPost("/visitor-passes/{id:guid}/checkout", (Guid id, HospitalDbContext db, TimeProvider t, CancellationToken ct) => CloseVisitorPass(id, VisitorPassStatus.CheckedOut, db, t, ct))
            .RequirePermission(PatientsPermissions.Visitors);
        office.MapPost("/visitor-passes/{id:guid}/revoke", (Guid id, HospitalDbContext db, TimeProvider t, CancellationToken ct) => CloseVisitorPass(id, VisitorPassStatus.Revoked, db, t, ct))
            .RequirePermission(PatientsPermissions.Visitors);

        var patients = app.MapGroup("/api/v1/patients").WithTags("Patients");
        patients.MapGet("/{patientId:guid}/consents", ListConsents).RequirePermission(PatientsPermissions.Read);
        patients.MapPost("/{patientId:guid}/consents", CaptureConsent).RequirePermission(PatientsPermissions.Consents).WithValidation<CaptureConsentRequest>();
        patients.MapGet("/{patientId:guid}/whatsapp", WhatsAppStatus).RequirePermission(PatientsPermissions.Read);
        patients.MapPost("/{patientId:guid}/whatsapp/opt-in", WhatsAppOptIn).RequirePermission(PatientsPermissions.Consents);
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
            enquiries = enquiries.Where(e => EF.Functions.ILike(e.Name, like, QueryExtensions.LikeEscape) || (e.Phone != null && e.Phone.Contains(term)) || EF.Functions.ILike(e.Details, like, QueryExtensions.LikeEscape));
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
            passes = passes.Where(v => EF.Functions.ILike(v.VisitorName, like, QueryExtensions.LikeEscape) || v.PassNumber == term.ToUpper());
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
            PassNumber = await numbers.NextAsync(PatientsNumberSeries.VisitorPass, facilityId, ct),
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

    // Restricted (VIP) records: consents need the same access as the demographics (the guard answers 404 for no patient).

    private static async Task<IResult> ListConsents(Guid patientId, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var users = db.Set<User>();
        var consents = await db.Set<PatientConsent>().AsNoTracking()
            .Where(c => c.PatientId == patientId)
            .OrderByDescending(c => c.CapturedAt)
            .Select(c => new ConsentDto(c.Id, c.Type, c.ConsentVersion, c.Granted, c.CapturedAt,
                users.Where(u => u.Id == c.CapturedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                c.WitnessName, c.SignedByRelation, c.SignedByName, c.DocumentFileId, c.RevokedAt, c.RevokedReason,
                users.Where(u => u.Id == c.RevokedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                c.Notes, c.Granted && c.RevokedAt == null))
            .ToListAsync(ct);
        await audit.PatientRecordAccessedAsync(patientId, "Viewed consents", ct);
        return TypedResults.Ok(consents);
    }

    private static async Task<IResult> WhatsAppStatus(
        Guid patientId, HospitalDbContext db, PatientAccessGuard guard, WhatsAppConsent whatsApp, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var phone = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.Phone).FirstOrDefaultAsync(ct);
        return TypedResults.Ok(await WhatsAppDtoAsync(db, whatsApp, phone, ct));
    }

    /// <summary>
    /// The patient told the desk they want WhatsApp messages: recorded with the user and the time. A STOP the number sent
    /// stays in force (families share numbers); only a START reply or the portal on that number lifts it.
    /// </summary>
    private static async Task<IResult> WhatsAppOptIn(
        Guid patientId, HospitalDbContext db, PatientAccessGuard guard, WhatsAppConsent whatsApp, ICurrentUser user, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var patient = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => new { p.Phone }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId).ToProblem();
        }

        if (!await whatsApp.OptInAsync(patient.Phone, patientId, WhatsAppOptInSource.RegistrationDesk, user.FacilityId, byNumberHolder: false, ct))
        {
            return Error.Rule("patient.no_mobile", "Add the patient's mobile number first: the WhatsApp opt-in is for that number.").ToProblem();
        }

        await db.SaveChangesAsync(ct);
        await audit.PatientRecordAccessedAsync(patientId, "Recorded a WhatsApp opt-in", ct);
        return TypedResults.Ok(await WhatsAppDtoAsync(db, whatsApp, patient.Phone, ct));
    }

    private static async Task<WhatsAppConsentDto> WhatsAppDtoAsync(HospitalDbContext db, WhatsAppConsent whatsApp, string? phone, CancellationToken ct)
    {
        var status = await whatsApp.StatusAsync(phone, ct);
        var capturedBy = status.CapturedByUserId is { } userId
            ? await db.Set<User>().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        return new WhatsAppConsentDto(
            Phone.Mask(phone), status.OptedIn, status.OptedInAt, status.Source, capturedBy, status.OptedOut, status.OptedOutAt, status.Reachable);
    }

    private static async Task<IResult> CaptureConsent(Guid patientId, CaptureConsentRequest request, HospitalDbContext db, PatientAccessGuard guard,
        ICurrentUser user, ISettingsService settings, TimeProvider time, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var consent = new PatientConsent
        {
            PatientId = patientId,
            Type = request.Type,
            // Without a version of its own, a consent is given against the privacy notice in force (DPDP s.5).
            ConsentVersion = string.IsNullOrWhiteSpace(request.ConsentVersion)
                ? await PrivacyNotice.CurrentVersionAsync(settings, user.FacilityId, ct)
                : request.ConsentVersion.Trim(),
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

        // A refusal of a standing permission ends the earlier grants, as the checks that use it already read it.
        if (!consent.Granted && PatientConsent.IsStandingPermission(consent.Type))
        {
            await RevokeStandingAsync(db, patientId, consent.Type, null, RefusedLaterReason, user.UserId, consent.CapturedAt, ct);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/patients/{patientId}/consents", new { consent.Id });
    }

    private const string RefusedLaterReason = "The patient refused when asked again.";

    /// <summary>
    /// Withdraws a consent (DPDP Act 2023 s6(4): as easy as giving it). It takes effect at once wherever the consent is
    /// checked: record sharing with other providers, campaigns, feedback requests and follow-up reminders. For a standing
    /// permission it withdraws every grant of the type, so no older copy keeps it in force.
    /// </summary>
    private static async Task<IResult> RevokeConsent(Guid consentId, RevokeConsentRequest request, HospitalDbContext db, PatientAccessGuard guard,
        ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var consent = await db.Set<PatientConsent>().FirstOrDefaultAsync(c => c.Id == consentId, ct);
        if (consent is null)
        {
            return Error.NotFound("Consent", consentId).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(consent.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var now = time.GetUtcNow();
        var revoked = consent.Revoke(request.Reason, user.UserId, now);
        if (revoked.IsFailure)
        {
            return revoked.Error!.ToProblem();
        }

        if (PatientConsent.IsStandingPermission(consent.Type))
        {
            await RevokeStandingAsync(db, consent.PatientId, consent.Type, consent.Id, request.Reason, user.UserId, now, ct);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Withdraws the patient's other standing grants of <paramref name="type"/> (the caller saves).</summary>
    private static async Task RevokeStandingAsync(HospitalDbContext db, Guid patientId, ConsentType type, Guid? exceptId, string reason, Guid? userId,
        DateTimeOffset at, CancellationToken ct)
    {
        var standing = await db.Set<PatientConsent>()
            .Where(c => c.PatientId == patientId && c.Type == type && c.Granted && c.RevokedAt == null && c.Id != exceptId)
            .ToListAsync(ct);
        foreach (var grant in standing)
        {
            grant.Revoke(reason, userId, at);
        }
    }

    // ------------------------------------------------------------------ documents

    // Restricted (VIP) records: adding and removing documents need the access that lists them (the guard answers 404 for
    // no patient). The data audit records each document added or removed, with the patient.

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

    private static async Task<IResult> AttachDocument(Guid patientId, AttachDocumentRequest request, HospitalDbContext db, PatientAccessGuard guard, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

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

    private static async Task<IResult> RemoveDocument(Guid documentId, HospitalDbContext db, PatientAccessGuard guard, CancellationToken ct)
    {
        var document = await db.Set<PatientDocument>().FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null)
        {
            return Error.NotFound("Document", documentId).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(document.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        db.Remove(document);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
