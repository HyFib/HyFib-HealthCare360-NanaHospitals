using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Mortuary;
using HyFib.HealthCare360.Modules.SupportServices.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Mortuary;

/// <summary>
/// /api/v1/mortuary — the cold-storage slots, the body register, receiving and releasing bodies, and the release slip.
/// A body linked to a patient is opened through the patient access guard, and the release slip is audited.
/// </summary>
internal static class MortuaryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/mortuary").WithTags("Mortuary");

        group.MapGet("/slots", async (MortuaryService mortuary, CancellationToken ct) => TypedResults.Ok(await mortuary.SlotsAsync(ct)))
            .RequireAnyPermission(SupportServicesPermissions.MortuaryManage, SupportServicesPermissions.MortuaryConfigure);
        group.MapPut("/slots", async (SaveSlotsRequest request, MortuaryService mortuary, CancellationToken ct) =>
                (await mortuary.SaveSlotsAsync(request, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.MortuaryConfigure).WithValidation<SaveSlotsRequest>();
        group.MapGet("/bodies", Register).RequirePermission(SupportServicesPermissions.MortuaryManage);
        group.MapGet("/bodies/{id:guid}", Get).RequirePermission(SupportServicesPermissions.MortuaryManage);
        group.MapPost("/bodies", Receive).RequirePermission(SupportServicesPermissions.MortuaryManage).WithValidation<ReceiveBodyRequest>();
        group.MapPost("/bodies/{id:guid}/release", Release).RequirePermission(SupportServicesPermissions.MortuaryManage).WithValidation<ReleaseBodyRequest>();
        group.MapGet("/bodies/{id:guid}/slip", Slip).RequirePermission(SupportServicesPermissions.MortuaryManage);
    }

    private static async Task<IResult> Register(BodyStatus? status, DateOnly? from, DateOnly? to, MortuaryService mortuary, TimeProvider time, CancellationToken ct)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-30);
        if (end < start || end.DayNumber - start.DayNumber > 366)
        {
            return Error.Field("to", "Choose up to a year, ending on or after the start.").ToProblem();
        }

        return TypedResults.Ok(await mortuary.RegisterAsync(status, start, end, ct));
    }

    private static async Task<IResult> Get(Guid id, MortuaryService mortuary, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, mortuary, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await mortuary.DtoAsync(opened.Value, ct));
    }

    private static async Task<IResult> Receive(ReceiveBodyRequest request, MortuaryService mortuary, PatientAccessGuard guard, CancellationToken ct)
    {
        if (request.PatientId is { } patientId && await guard.EnsureCanOpenAsync(patientId, ct) is { IsFailure: true } refused)
        {
            return refused.Error!.ToProblem();
        }

        return (await mortuary.ReceiveAsync(request, ct)).ToOk();
    }

    private static async Task<IResult> Release(Guid id, ReleaseBodyRequest request, MortuaryService mortuary, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, mortuary, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await mortuary.ReleaseAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> Slip(
        Guid id, MortuaryService mortuary, PatientAccessGuard guard, IAuditLogger audit, DocumentAssets assets, CancellationToken ct)
    {
        var opened = await OpenAsync(id, mortuary, guard, ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var body = opened.Value;
        if (body.Status != BodyStatus.Released)
        {
            return Error.Rule("mortuary.not_released", "The slip is printed when the body is released.").ToProblem();
        }

        if (body.PatientId is { } patientId)
        {
            await audit.PatientRecordAccessedAsync(patientId, $"Printed the body release slip {body.Number}", ct);
        }

        return TypedResults.Ok(BodyReleaseSlip.Build(await mortuary.DtoAsync(body, ct), await assets.LetterheadAsync(body.FacilityId, ct)).ToDto());
    }

    private static async Task<Result<BodyRecord>> OpenAsync(Guid id, MortuaryService mortuary, PatientAccessGuard guard, CancellationToken ct)
    {
        if (await mortuary.FindAsync(id, ct) is not { } body)
        {
            return Error.NotFound("Body", id);
        }

        if (body.PatientId is { } patientId && await guard.EnsureCanOpenAsync(patientId, ct) is { IsFailure: true } refused)
        {
            return refused.Error!;
        }

        return body;
    }
}

/// <summary>The slip signed when a body leaves the mortuary: who took it, on what authority, with what belongings.</summary>
internal static class BodyReleaseSlip
{
    public static PrintableDocument Build(BodyDto b, DocumentLetterhead letterhead) => new()
    {
        Title = $"Body release {b.Number}",
        FileName = $"body-release-{b.Number}",
        Styles = DocumentParts.Styles,
        Template = letterhead.Template,
        Footer = $"{b.Number} · Mortuary register",
        Body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("Release of a body from the mortuary")}
            {DocumentParts.Fields([
                ("Register no.", b.Number),
                ("Name", b.Name),
                ("UHID", b.Uhid),
                ("Sex, age", string.Join(", ", new[] { b.Sex, b.AgeYears is { } age ? $"{age} years" : null }.Where(s => s is not null))),
                ("Died", b.DiedAt is { } died ? DocumentFormat.DateTime(died) : null),
                ("Received", $"{DocumentFormat.DateTime(b.ReceivedAt)} by {b.ReceivedByName}"),
                ("Medico-legal", b.IsMlc ? $"Yes, {b.MlcNumber} ({b.PoliceStation})" : "No"),
                ("Released", b.ReleasedAt is { } at ? DocumentFormat.DateTime(at) : null),
                ("Released to", b.ReleasedTo?.ToString()),
                ("Received by", b.Relationship is null ? b.ReceivedBy : $"{b.ReceivedBy} ({b.Relationship})"),
                ("Identity proof", b.IdProof),
                ("Police clearance", b.PoliceClearance),
                ("Belongings", b.Belongings is null ? null : $"{b.Belongings} — {(b.BelongingsHandedOver ? "handed over" : "kept")}"),
                ("Released by", b.ReleasedByName),
            ])}
            <div class="sign"><div>Received by</div><div>Mortuary in charge</div></div>
            """),
    };
}
