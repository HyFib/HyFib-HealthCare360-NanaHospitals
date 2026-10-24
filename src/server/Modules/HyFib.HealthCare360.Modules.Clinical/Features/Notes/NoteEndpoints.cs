using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Notes;

/// <summary>
/// /api/v1/emr — clinical notes (progress, procedure, referral, counselling). Only the author edits or signs a note;
/// signed notes are corrected with an addendum that links to the original.
/// </summary>
internal static class NoteEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr").WithTags("EMR");

        group.MapGet("/patients/{patientId:guid}/notes", List).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/patients/{patientId:guid}/notes", Write).RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<WriteNoteRequest>();
        group.MapPut("/notes/{id:guid}", Edit).RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<EditNoteRequest>();
        group.MapPost("/notes/{id:guid}/sign", Sign).RequirePermission(ClinicalPermissions.EncountersSign).WithValidation<SignNoteRequest>();
        group.MapPost("/notes/{id:guid}/addendum", Addendum).RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<AddendumRequest>();
    }

    private static async Task<IResult> List(Guid patientId, [AsParameters] PageQuery query, Guid? encounterId, ClinicalNoteType? type, ClinicalAccess access,
        HospitalDbContext db, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed clinical notes", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var notes = db.Set<ClinicalNote>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(n => n.PatientId == patientId);
        if (encounterId is not null)
        {
            notes = notes.Where(n => n.EncounterId == encounterId);
        }

        if (type is not null)
        {
            notes = notes.Where(n => n.Type == type);
        }

        var page = await notes.OrderByDescending(n => n.CreatedAt).ToPagedResultAsync(query.ToRequest(), ct);
        var authors = await PrintQueries.PractitionerNamesAsync(db, page.Items.Select(n => n.AuthorPractitionerId), ct);
        return TypedResults.Ok(page.Map(n => ToDto(n, n.AuthorPractitionerId is { } a ? authors.GetValueOrDefault(a) : null)));
    }

    private static Task<IResult> Write(Guid patientId, WriteNoteRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        CancellationToken ct) =>
        CreateAsync(patientId, request, null, access, db, user, ct);

    private static async Task<IResult> CreateAsync(Guid patientId, WriteNoteRequest request, Guid? amendsNoteId, ClinicalAccess access, HospitalDbContext db,
        ICurrentUser user, CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        if (request.EncounterId is { } encounterId
            && !await db.Set<Encounter>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(e => e.Id == encounterId && e.PatientId == patientId, ct))
        {
            return Error.Field("encounterId", "This encounter does not belong to the patient.").ToProblem();
        }

        var note = ClinicalNote.Draft(patientId, request.Type, request.Title, request.Body);
        note.FacilityId = facilityId;
        note.EncounterId = request.EncounterId;
        note.AdmissionId = request.AdmissionId;
        note.AuthorUserId = user.UserId;
        note.AuthorPractitionerId = user.PractitionerId;
        note.AmendsNoteId = amendsNoteId;
        db.Add(note);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/emr/patients/{patientId}/notes", ToDto(note, null));
    }

    private static Task<IResult> Edit(Guid id, EditNoteRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user, CancellationToken ct) =>
        ChangeAsync(id, request.Version, note => note.Edit(request.Type, request.Title, request.Body), access, db, user, ct);

    private static Task<IResult> Sign(Guid id, SignNoteRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user, TimeProvider time,
        CancellationToken ct) =>
        ChangeAsync(id, request.Version, note => note.Sign(user.UserId, time.GetUtcNow()), access, db, user, ct);

    private static async Task<IResult> Addendum(Guid id, AddendumRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        CancellationToken ct)
    {
        var original = await db.Set<ClinicalNote>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (original is null)
        {
            return Error.NotFound("Note", id).ToProblem();
        }

        if (!original.IsSigned)
        {
            return Error.Rule("note.not_signed", "This note is not signed yet; edit it directly.").ToProblem();
        }

        var addendum = new WriteNoteRequest(original.EncounterId, original.AdmissionId, original.Type,
            $"Addendum to {original.Title ?? original.Type.ToString()}", request.Body);
        return await CreateAsync(original.PatientId, addendum, original.Id, access, db, user, ct);
    }

    private static async Task<IResult> ChangeAsync(Guid id, uint version, Func<ClinicalNote, Result> change, ClinicalAccess access, HospitalDbContext db,
        ICurrentUser user, CancellationToken ct)
    {
        var note = await db.Set<ClinicalNote>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
        {
            return Error.NotFound("Note", id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(note.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        if (note.AuthorUserId is not null && note.AuthorUserId != user.UserId)
        {
            return Error.Forbidden("note.not_author", "Only the author can edit or sign this note.").ToProblem();
        }

        db.SetExpectedVersion(note, version);
        var result = change(note);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(note, null));
    }

    internal static ClinicalNoteDto ToDto(ClinicalNote n, string? authorName) => new(
        n.Id, n.PatientId, n.EncounterId, n.AdmissionId, n.Type, n.Title, n.Body, n.AuthorUserId, n.AuthorPractitionerId, authorName, n.AmendsNoteId,
        n.IsSigned, n.SignedAt, n.CreatedAt, n.Version);
}
