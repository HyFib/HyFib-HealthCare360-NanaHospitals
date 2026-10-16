using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Procedures;

/// <summary>/api/v1/emr — the patient's procedures: recorded on the chart or from completed operations, corrected and voided (EHR-01).</summary>
internal static class ProcedureEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr").WithTags("EMR");

        group.MapGet("/patients/{patientId:guid}/procedures", List).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/patients/{patientId:guid}/procedures", Record).RequirePermission(ClinicalPermissions.EmrWrite)
            .WithValidation<RecordProcedureRequest>();
        group.MapPut("/procedures/{id:guid}", Correct).RequirePermission(ClinicalPermissions.EmrWrite)
            .WithValidation<CorrectProcedureRequest>();
        group.MapPost("/procedures/{id:guid}/entered-in-error", EnteredInError).RequirePermission(ClinicalPermissions.EmrWrite)
            .WithValidation<EnteredInErrorRequest>();
    }

    private static async Task<IResult> List(Guid patientId, bool? includeEnteredInError, ClinicalAccess access, ProcedureService procedures, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed procedures", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await procedures.ListAsync(patientId, includeEnteredInError == true, ct));
    }

    private static async Task<IResult> Record(Guid patientId, RecordProcedureRequest request, ClinicalAccess access, ProcedureService procedures,
        CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var result = await procedures.RecordAsync(patientId, request, ct);
        return result.IsFailure
            ? result.Error!.ToProblem()
            : TypedResults.Created($"/api/v1/emr/patients/{patientId}/procedures", result.Value);
    }

    private static async Task<IResult> Correct(Guid id, CorrectProcedureRequest request, ClinicalAccess access, ProcedureService procedures, CancellationToken ct)
    {
        var record = await procedures.FindAsync(id, ct);
        if (record is null)
        {
            return Error.NotFound("Procedure", id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(record.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var result = await procedures.CorrectAsync(record, request, ct);
        return result.IsFailure ? result.Error!.ToProblem() : TypedResults.Ok(result.Value);
    }

    private static async Task<IResult> EnteredInError(Guid id, EnteredInErrorRequest request, ClinicalAccess access, ProcedureService procedures,
        CancellationToken ct)
    {
        var record = await procedures.FindAsync(id, ct);
        if (record is null)
        {
            return Error.NotFound("Procedure", id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(record.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var result = await procedures.MarkEnteredInErrorAsync(record, request.Reason, ct);
        return result.IsFailure ? result.Error!.ToProblem() : TypedResults.Ok(result.Value);
    }
}
