using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Nursing;

/// <summary>
/// /api/v1/nursing — nursing notes, SBAR shift handovers, intake/output chart and risk assessments (Morse, Braden,
/// pain). Vitals are owned by the Clinical module and are not duplicated here. Reads are audited as PHI access and
/// honour restricted-record rules; writes need the chart to be open (admitted, or within 24 hours of discharge).
/// </summary>
internal static class NursingEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/nursing").WithTags("Nursing");

        group.MapGet("/admissions/{admissionId:guid}/notes", ListNotes).RequirePermission(InpatientPermissions.NursingChart);
        group.MapPost("/admissions/{admissionId:guid}/notes", AddNote).RequirePermission(InpatientPermissions.NursingChart).WithValidation<AddNursingNoteRequest>();

        group.MapGet("/admissions/{admissionId:guid}/handovers", ListHandovers).RequirePermission(InpatientPermissions.NursingChart);
        group.MapPost("/admissions/{admissionId:guid}/handovers", AddHandover).RequirePermission(InpatientPermissions.NursingChart).WithValidation<AddHandoverRequest>();
        group.MapPost("/handovers/{id:guid}/acknowledge", AcknowledgeHandover).RequirePermission(InpatientPermissions.NursingChart);
        group.MapGet("/wards/{wardId:guid}/handovers", WardHandovers).RequirePermission(InpatientPermissions.NursingChart);

        group.MapGet("/admissions/{admissionId:guid}/intake-output", IntakeOutput).RequirePermission(InpatientPermissions.NursingChart);
        group.MapPost("/admissions/{admissionId:guid}/intake-output", AddIntakeOutput).RequirePermission(InpatientPermissions.NursingChart).WithValidation<AddIntakeOutputRequest>();
        group.MapPost("/intake-output/{id:guid}/void", VoidIntakeOutput).RequirePermission(InpatientPermissions.NursingChart).WithValidation<VoidEntryRequest>();

        group.MapGet("/admissions/{admissionId:guid}/assessments", ListAssessments).RequirePermission(InpatientPermissions.NursingChart);
        group.MapPost("/admissions/{admissionId:guid}/assessments/morse", AddMorse).RequirePermission(InpatientPermissions.NursingChart).WithValidation<MorseAssessmentRequest>();
        group.MapPost("/admissions/{admissionId:guid}/assessments/braden", AddBraden).RequirePermission(InpatientPermissions.NursingChart).WithValidation<BradenAssessmentRequest>();
        group.MapPost("/admissions/{admissionId:guid}/assessments/pain", AddPain).RequirePermission(InpatientPermissions.NursingChart).WithValidation<PainAssessmentRequest>();
    }

    // ------------------------------------------------------------------ notes

    private static async Task<IResult> ListNotes(Guid admissionId, DateOnly? dutyDate, AdmissionAccess admissions, HospitalDbContext db, CancellationToken ct)
    {
        var access = await admissions.OpenForReadAsync(admissionId, "Viewed nursing notes", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var notes = db.Set<NursingNote>().AsNoTracking().Where(n => n.AdmissionId == admissionId);
        if (dutyDate is { } date)
        {
            notes = notes.Where(n => n.DutyDate == date);
        }

        return TypedResults.Ok(await notes.OrderByDescending(n => n.NoteFor).Take(500)
            .Select(n => new NursingNoteDto(n.Id, n.DutyDate, n.Shift, n.Category, n.Note, n.NoteFor, n.RecordedAt, n.RecordedByName, n.IsLateEntry))
            .ToListAsync(ct));
    }

    private static async Task<IResult> AddNote(
        Guid admissionId, AddNursingNoteRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForChartingAsync(admissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var admission = access.Value;
        var now = time.GetUtcNow();
        var noteFor = request.NoteFor ?? now;
        var note = new NursingNote
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            DutyDate = NursingShifts.DutyDate(noteFor),
            Shift = NursingShifts.At(noteFor),
            Category = request.Category,
            Note = request.Note.Trim(),
            NoteFor = noteFor,
            RecordedAt = now,
            RecordedBy = user.UserId,
            RecordedByName = user.DisplayName,
            IsLateEntry = NursingNote.IsLate(noteFor, now),
        };
        db.Add(note);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/nursing/admissions/{admission.Id}/notes",
            new NursingNoteDto(note.Id, note.DutyDate, note.Shift, note.Category, note.Note, note.NoteFor, note.RecordedAt, note.RecordedByName, note.IsLateEntry));
    }

    // ------------------------------------------------------------------ handovers

    private static async Task<IResult> ListHandovers(Guid admissionId, AdmissionAccess admissions, HospitalDbContext db, CancellationToken ct)
    {
        var access = await admissions.OpenForReadAsync(admissionId, "Viewed shift handovers", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var rows = await db.Set<ShiftHandover>().AsNoTracking()
            .Where(h => h.AdmissionId == admissionId)
            .OrderByDescending(h => h.HandedOverAt)
            .Take(200)
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> AddHandover(
        Guid admissionId, AddHandoverRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForChartingAsync(admissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var admission = access.Value;
        var now = time.GetUtcNow();
        var handover = new ShiftHandover
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            WardId = admission.WardId,
            DutyDate = NursingShifts.DutyDate(now),
            FromShift = NursingShifts.At(now),
            Situation = request.Situation.Trim(),
            Background = Clean(request.Background),
            Assessment = Clean(request.Assessment),
            Recommendation = Clean(request.Recommendation),
            PendingTasks = Clean(request.PendingTasks),
            HandedOverAt = now,
            HandedOverBy = user.UserId,
            HandedOverByName = user.DisplayName,
        };
        db.Add(handover);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/nursing/admissions/{admission.Id}/handovers", ToDto(handover));
    }

    private static async Task<IResult> AcknowledgeHandover(
        Guid id, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var handover = await db.Set<ShiftHandover>().FirstOrDefaultAsync(h => h.Id == id, ct);
        if (handover is null)
        {
            return Error.NotFound("Handover", id).ToProblem();
        }

        var access = await admissions.OpenForReadAsync(handover.AdmissionId, "Acknowledged a shift handover", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var result = handover.Acknowledge(user.UserId, user.DisplayName, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(handover));
    }

    /// <summary>The ward's handover sheet for a duty date (default: the current duty date), optionally one shift.</summary>
    private static async Task<IResult> WardHandovers(
        Guid wardId, DateOnly? dutyDate, NursingShift? shift, HospitalDbContext db, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var date = dutyDate ?? NursingShifts.DutyDate(time.GetUtcNow());
        var handovers = db.Set<ShiftHandover>().AsNoTracking().Where(h => h.WardId == wardId && h.DutyDate == date);
        if (shift is not null)
        {
            handovers = handovers.Where(h => h.FromShift == shift);
        }

        var rows = await (
                from h in handovers
                join p in db.Set<Patient>() on h.PatientId equals p.Id
                join a in db.Set<Admission>() on h.AdmissionId equals a.Id
                join b in db.Set<Bed>() on a.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                orderby h.HandedOverAt
                select new { Handover = h, p.Uhid, p.FullName, BedLabel = b != null ? b.Label : null })
            .Take(500)
            .ToListAsync(ct);

        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "ward.handovers.viewed", $"Viewed the ward handover sheet for {date:yyyy-MM-dd}")
        {
            EntityType = nameof(Ward),
            EntityId = wardId.ToString(),
        }, ct);
        return TypedResults.Ok(rows.Select(r => new WardHandoverItem(ToDto(r.Handover), r.Uhid, r.FullName, r.BedLabel)).ToList());
    }

    // ------------------------------------------------------------------ intake / output

    private static async Task<IResult> IntakeOutput(Guid admissionId, DateOnly? dutyDate, AdmissionAccess admissions, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForReadAsync(admissionId, "Viewed intake/output chart", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var date = dutyDate ?? NursingShifts.DutyDate(time.GetUtcNow());
        var (from, to) = FluidBalance.ChartDay(date);
        var entries = await db.Set<IntakeOutputEntry>().AsNoTracking()
            .Where(e => e.AdmissionId == admissionId && e.RecordedAt >= from && e.RecordedAt < to)
            .OrderBy(e => e.RecordedAt)
            .ToListAsync(ct);
        var balance = FluidBalance.Summarize(entries.Select(e => new FluidEntry(e.RecordedAt, e.Direction, e.Route, e.VolumeMl)), from, to);
        return TypedResults.Ok(new IntakeOutputChart(date, balance, entries.Select(ToDto).ToList()));
    }

    private static async Task<IResult> AddIntakeOutput(
        Guid admissionId, AddIntakeOutputRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForChartingAsync(admissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var admission = access.Value;
        var entry = new IntakeOutputEntry
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            RecordedAt = request.RecordedAt ?? time.GetUtcNow(),
            Direction = request.Direction,
            Route = request.Route,
            VolumeMl = request.VolumeMl,
            Description = Clean(request.Description),
            Notes = Clean(request.Notes),
            RecordedBy = user.UserId,
            RecordedByName = user.DisplayName,
        };
        db.Add(entry);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/nursing/admissions/{admission.Id}/intake-output", ToDto(entry));
    }

    /// <summary>Entries made in error are voided (soft-deleted with a reason), never edited.</summary>
    private static async Task<IResult> VoidIntakeOutput(Guid id, VoidEntryRequest request, AdmissionAccess admissions, HospitalDbContext db, CancellationToken ct)
    {
        var entry = await db.Set<IntakeOutputEntry>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
        {
            return Error.NotFound("Intake/output entry", id).ToProblem();
        }

        var access = await admissions.OpenForChartingAsync(entry.AdmissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        entry.VoidReason = request.Reason.Trim();
        db.Remove(entry);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ------------------------------------------------------------------ risk assessments

    private static async Task<IResult> ListAssessments(Guid admissionId, AssessmentType? type, AdmissionAccess admissions, HospitalDbContext db, CancellationToken ct)
    {
        var access = await admissions.OpenForReadAsync(admissionId, "Viewed nursing risk assessments", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var assessments = db.Set<NursingAssessment>().AsNoTracking().Where(a => a.AdmissionId == admissionId);
        if (type is not null)
        {
            assessments = assessments.Where(a => a.Type == type);
        }

        var rows = await assessments.OrderByDescending(a => a.AssessedAt).Take(200).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToDto).ToList());
    }

    private static Task<IResult> AddMorse(
        Guid admissionId, MorseAssessmentRequest r, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
        RecordAssessmentAsync(admissionId, AssessmentType.MorseFall,
            MorseFallScale.Calculate(new MorseFallInput(r.HistoryOfFalling, r.SecondaryDiagnosis, r.AmbulatoryAid, r.IntravenousAccess, r.Gait, r.OverestimatesAbility)),
            new
            {
                r.HistoryOfFalling, r.SecondaryDiagnosis, AmbulatoryAid = r.AmbulatoryAid.ToString(), r.IntravenousAccess, Gait = r.Gait.ToString(),
                r.OverestimatesAbility,
            },
            r.Notes, admissions, db, user, time, ct);

    private static Task<IResult> AddBraden(
        Guid admissionId, BradenAssessmentRequest r, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
        RecordAssessmentAsync(admissionId, AssessmentType.Braden,
            BradenScale.Calculate(new BradenInput(r.SensoryPerception, r.Moisture, r.Activity, r.Mobility, r.Nutrition, r.FrictionShear)),
            new { r.SensoryPerception, r.Moisture, r.Activity, r.Mobility, r.Nutrition, r.FrictionShear },
            r.Notes, admissions, db, user, time, ct);

    private static Task<IResult> AddPain(
        Guid admissionId, PainAssessmentRequest r, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
        RecordAssessmentAsync(admissionId, AssessmentType.Pain,
            PainScale.Calculate(new PainInput(r.Score, r.Scale, r.Location, r.Character)),
            new { r.Score, Scale = r.Scale.ToString(), r.Location, r.Character },
            r.Notes, admissions, db, user, time, ct);

    private static async Task<IResult> RecordAssessmentAsync(
        Guid admissionId, AssessmentType type, RiskScore score, object responses, string? notes, AdmissionAccess admissions, HospitalDbContext db,
        ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForChartingAsync(admissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var admission = access.Value;
        var assessment = new NursingAssessment
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            Type = type,
            Score = score.Score,
            Risk = score.Risk,
            Interpretation = score.Interpretation,
            Responses = JsonSerializer.Serialize(responses, Json),
            AssessedAt = time.GetUtcNow(),
            AssessedBy = user.UserId,
            AssessedByName = user.DisplayName,
            Notes = Clean(notes),
        };
        db.Add(assessment);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/nursing/admissions/{admission.Id}/assessments", ToDto(assessment));
    }

    private static HandoverDto ToDto(ShiftHandover h) => new(
        h.Id, h.AdmissionId, h.PatientId, h.WardId, h.DutyDate, h.FromShift, h.Situation, h.Background, h.Assessment, h.Recommendation, h.PendingTasks,
        h.HandedOverAt, h.HandedOverByName, h.AcknowledgedAt, h.AcknowledgedByName);

    private static IntakeOutputDto ToDto(IntakeOutputEntry e) => new(
        e.Id, e.RecordedAt, NursingShifts.At(e.RecordedAt), e.Direction, e.Route, e.VolumeMl, e.Description, e.Notes, e.RecordedByName);

    private static AssessmentDto ToDto(NursingAssessment a) => new(
        a.Id, a.Type, a.Score, a.Risk, a.Interpretation, JsonNode.Parse(a.Responses), a.AssessedAt, a.AssessedByName, a.Notes);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
