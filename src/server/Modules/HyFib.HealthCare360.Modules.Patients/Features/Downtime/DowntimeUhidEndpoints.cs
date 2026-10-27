using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Downtime;

/// <summary>
/// /api/v1/patients/downtime-blocks — blocks of UHIDs for the registration desks' downtime kits (NABH IMS). A
/// registration from paper is entered through the ordinary registration, with <c>paper</c>.
/// </summary>
internal static class DowntimeUhidEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/patients/downtime-blocks").WithTags("Patients").RequirePermission(PatientsPermissions.Downtime);

        group.MapGet("/", async (DowntimeUhidService downtime, CancellationToken ct) => (await downtime.ListAsync(ct)).ToOk());

        group.MapGet("/{id:guid}", async (Guid id, DowntimeUhidService downtime, CancellationToken ct) => (await downtime.GetAsync(id, ct)).ToOk());

        group.MapPost("/", async (IssueDowntimeUhidsRequest request, DowntimeUhidService downtime, CancellationToken ct) =>
                (await downtime.IssueAsync(request, ct)).ToCreated(d => $"/api/v1/patients/downtime-blocks/{d.Block.Id}"))
            .WithValidation<IssueDowntimeUhidsRequest>();

        group.MapPost("/{id:guid}/void", async (Guid id, VoidDowntimeUhidRequest request, DowntimeUhidService downtime, CancellationToken ct) =>
                (await downtime.VoidAsync(id, request, ct)).ToNoContent())
            .WithValidation<VoidDowntimeUhidRequest>();
    }
}
