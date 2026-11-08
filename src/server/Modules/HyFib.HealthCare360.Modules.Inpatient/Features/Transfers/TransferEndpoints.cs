using HyFib.HealthCare360.Modules.Inpatient.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Transfers;

/// <summary>/api/v1/inpatient — transfer requests between wards: asked by the sending ward, accepted or declined by the receiving ward.</summary>
internal static class TransferEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inpatient").WithTags("Inpatient");

        group.MapPost("/admissions/{id:guid}/transfer-requests", async (Guid id, RequestTransferRequest request, TransferService transfers, CancellationToken ct) =>
                (await transfers.RequestAsync(id, request, ct)).ToHttpResult(dto => TypedResults.Created($"/api/v1/inpatient/transfer-requests/{dto.Id}", dto)))
            .RequirePermission(InpatientPermissions.Transfer)
            .WithValidation<RequestTransferRequest>();

        group.MapGet("/admissions/{id:guid}/transfer-requests", async (Guid id, TransferService transfers, CancellationToken ct) =>
                TypedResults.Ok(await transfers.ForAdmissionAsync(id, ct)))
            .RequirePermission(InpatientPermissions.AdmissionsRead);

        group.MapGet("/transfer-requests", async (Guid? wardId, bool? openOnly, TransferService transfers, CancellationToken ct) =>
                TypedResults.Ok(await transfers.ListAsync(wardId, openOnly ?? true, ct)))
            .RequireAnyPermission(InpatientPermissions.AdmissionsRead, InpatientPermissions.NursingInCharge);

        group.MapPost("/transfer-requests/{id:guid}/accept", async (Guid id, AcceptTransferRequest request, TransferService transfers, CancellationToken ct) =>
                (await transfers.AcceptAsync(id, request, ct)).ToOk())
            .RequireAnyPermission(InpatientPermissions.Transfer, InpatientPermissions.NursingInCharge)
            .WithValidation<AcceptTransferRequest>();

        group.MapPost("/transfer-requests/{id:guid}/decline", async (Guid id, DecideTransferRequest request, TransferService transfers, CancellationToken ct) =>
                (await transfers.DeclineAsync(id, request, ct)).ToOk())
            .RequireAnyPermission(InpatientPermissions.Transfer, InpatientPermissions.NursingInCharge)
            .WithValidation<DecideTransferRequest>();

        group.MapPost("/transfer-requests/{id:guid}/cancel", async (Guid id, DecideTransferRequest request, TransferService transfers, CancellationToken ct) =>
                (await transfers.CancelAsync(id, request, ct)).ToOk())
            .RequirePermission(InpatientPermissions.Transfer)
            .WithValidation<DecideTransferRequest>();
    }
}
