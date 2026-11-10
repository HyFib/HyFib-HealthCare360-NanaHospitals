using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Nursing;

public sealed record NursingSafetyEventDto(
    Guid Id,
    NursingSafetyEventKind Kind,
    DateTimeOffset OccurredAt,
    string Location,
    string Description,
    string? ImmediateAction,
    FallInjury? FallInjury,
    bool? Witnessed,
    PressureInjuryStage? Stage,
    string? BodySite,
    bool PresentOnAdmission,
    DateTimeOffset RecordedAt,
    string? RecordedByName,
    string? IncidentNumber);

/// <summary>A fall, or a pressure injury found. Fall: the injury (and whether witnessed). Pressure injury: the stage, the site and whether present on admission.</summary>
public sealed record RecordSafetyEventRequest(
    NursingSafetyEventKind Kind,
    DateTimeOffset OccurredAt,
    string Location,
    string Description,
    string? ImmediateAction,
    FallInjury? FallInjury,
    bool? Witnessed,
    PressureInjuryStage? Stage,
    string? BodySite,
    bool PresentOnAdmission);

public sealed class RecordSafetyEventRequestValidator : AbstractValidator<RecordSafetyEventRequest>
{
    public RecordSafetyEventRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.OccurredAt).Must(at => at <= time.GetUtcNow().AddMinutes(5)).WithMessage("The time cannot be in the future.");
        RuleFor(x => x.Location).NotEmpty().SafeText(150);
        RuleFor(x => x.Description).NotEmpty().MinimumLength(10).SafeText(2000);
        RuleFor(x => x.ImmediateAction).SafeText(1000);
        RuleFor(x => x.BodySite).SafeText(100);
        When(x => x.Kind == NursingSafetyEventKind.Fall, () =>
        {
            RuleFor(x => x.FallInjury).NotNull().IsInEnum().WithMessage("Say what injury the fall did (none, minor, moderate, major or death).");
            RuleFor(x => x.Witnessed).NotNull().WithMessage("Say whether anyone saw the fall.");
            RuleFor(x => x.Stage).Null().WithMessage("A fall has no pressure injury stage.");
            RuleFor(x => x.PresentOnAdmission).Equal(false).WithMessage("A fall in hospital was not present on admission.");
        });
        When(x => x.Kind == NursingSafetyEventKind.PressureInjury, () =>
        {
            RuleFor(x => x.Stage).NotNull().IsInEnum().WithMessage("Give the pressure injury's stage.");
            RuleFor(x => x.BodySite).NotEmpty().WithMessage("Say where on the body it is.");
            RuleFor(x => x.FallInjury).Null().WithMessage("A pressure injury has no fall injury.");
        });
    }
}

/// <summary>
/// /api/v1/nursing/admissions/{id}/safety-events — falls and pressure injuries on the nursing chart (NABH COP, PSQ). A fall,
/// and a pressure injury that developed in hospital, raises an incident in the Quality register in the same save.
/// </summary>
internal static class SafetyEventEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/nursing").WithTags("Nursing");
        group.MapGet("/admissions/{admissionId:guid}/safety-events", List).RequirePermission(InpatientPermissions.NursingChart);
        group.MapPost("/admissions/{admissionId:guid}/safety-events", Record).RequirePermission(InpatientPermissions.NursingChart)
            .WithValidation<RecordSafetyEventRequest>();
    }

    private static async Task<IResult> List(Guid admissionId, AdmissionAccess admissions, HospitalDbContext db, CancellationToken ct)
    {
        var access = await admissions.OpenForReadAsync(admissionId, "Viewed falls and pressure injuries", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var rows = await db.Set<NursingSafetyEvent>().AsNoTracking().Where(e => e.AdmissionId == admissionId).OrderByDescending(e => e.OccurredAt).Take(200).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(Dto).ToList());
    }

    private static async Task<IResult> Record(
        Guid admissionId, RecordSafetyEventRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, IIncidentRegister incidents,
        TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForChartingAsync(admissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var admission = access.Value;
        var admittedAt = await db.Set<Admission>().Where(a => a.Id == admissionId).Select(a => a.AdmittedAt).FirstAsync(ct);
        if (request.OccurredAt < admittedAt && !request.PresentOnAdmission)
        {
            return Error.Field("occurredAt", "This is before the admission. A pressure injury found on admission is marked present on admission.").ToProblem();
        }

        var row = new NursingSafetyEvent
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            WardId = admission.WardId,
            Kind = request.Kind,
            OccurredAt = request.OccurredAt,
            Location = request.Location.Trim(),
            Description = request.Description.Trim(),
            ImmediateAction = string.IsNullOrWhiteSpace(request.ImmediateAction) ? null : request.ImmediateAction.Trim(),
            FallInjury = request.FallInjury,
            Witnessed = request.Witnessed,
            Stage = request.Stage,
            BodySite = string.IsNullOrWhiteSpace(request.BodySite) ? null : request.BodySite.Trim(),
            PresentOnAdmission = request.PresentOnAdmission,
            RecordedAt = time.GetUtcNow(),
            RecordedBy = user.UserId,
            RecordedByName = user.DisplayName,
        };
        db.Add(row);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (row.IsReportable)
        {
            var raised = await incidents.ReportAsync(new ReportedSafetyEvent(
                row.FacilityId, row.PatientId,
                row.Kind == NursingSafetyEventKind.Fall ? SafetyEventKind.PatientFall : SafetyEventKind.PressureInjury,
                NursingSafetyHarm.Of(row), row.OccurredAt, row.Location, Describe(row), row.ImmediateAction,
                "Inpatient", nameof(NursingSafetyEvent), row.Id, Reference(row)), ct);
            if (raised.IsFailure)
            {
                return raised.Error!.ToProblem();
            }

            row.IncidentNumber = raised.Value.IncidentNumber;
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/v1/nursing/admissions/{admissionId}/safety-events", Dto(row));
    }

    private static string Reference(NursingSafetyEvent e) =>
        e.Kind == NursingSafetyEventKind.Fall ? $"Fall ({e.FallInjury} injury)" : $"Pressure injury {e.Stage}, {e.BodySite}";

    private static string Describe(NursingSafetyEvent e) =>
        e.Kind == NursingSafetyEventKind.Fall
            ? $"Patient fall{(e.Witnessed == true ? " (witnessed)" : " (not witnessed)")}, injury: {e.FallInjury}. {e.Description}"
            : $"Hospital-acquired pressure injury, {e.Stage} at {e.BodySite}. {e.Description}";

    private static NursingSafetyEventDto Dto(NursingSafetyEvent e) =>
        new(e.Id, e.Kind, e.OccurredAt, e.Location, e.Description, e.ImmediateAction, e.FallInjury, e.Witnessed, e.Stage, e.BodySite, e.PresentOnAdmission,
            e.RecordedAt, e.RecordedByName, e.IncidentNumber);
}
