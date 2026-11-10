using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;

/// <summary>
/// /api/v1/blood-bank on the ward: a patient's units, the bedside check that starts one, observations while it
/// runs, and its end.
/// </summary>
internal static class BedsideEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/blood-bank").WithTags("Blood Bank");

        group.MapGet("/patients/{patientId:guid}/units", PatientUnits)
            .RequireAnyPermission([SpecialtyCarePermissions.BloodBankTransfuse, SpecialtyCarePermissions.BloodBankRead]);
        group.MapPost("/issues/{id:guid}/transfusion", async (Guid id, StartTransfusionRequest r, BedsideTransfusionService s, CancellationToken ct) =>
                (await s.StartAsync(id, r, ct)).ToHttpResult(t => TypedResults.Created($"/api/v1/blood-bank/transfusions/{t.Id}", t)))
            .RequirePermission(SpecialtyCarePermissions.BloodBankTransfuse).WithValidation<StartTransfusionRequest>();
        group.MapPost("/transfusions/{id:guid}/observations",
                async (Guid id, RecordTransfusionObservationRequest r, BedsideTransfusionService s, CancellationToken ct) =>
                    (await s.ObserveAsync(id, r, ct)).ToHttpResult(t => TypedResults.Ok(t)))
            .RequirePermission(SpecialtyCarePermissions.BloodBankTransfuse).WithValidation<RecordTransfusionObservationRequest>();
        group.MapPost("/transfusions/{id:guid}/end", async (Guid id, EndTransfusionRequest r, BedsideTransfusionService s, CancellationToken ct) =>
                (await s.EndAsync(id, r, ct)).ToHttpResult(t => TypedResults.Ok(t)))
            .RequirePermission(SpecialtyCarePermissions.BloodBankTransfuse).WithValidation<EndTransfusionRequest>();
    }

    private static async Task<IResult> PatientUnits(Guid patientId, BedsideTransfusionService service, PatientAccessGuard guard, IAuditLogger audit,
        CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenChartAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var units = await service.ForPatientAsync(patientId, ct);
        if (units.IsFailure)
        {
            return units.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, "Viewed the patient's blood units on the ward", ct);
        return TypedResults.Ok(units.Value);
    }
}
