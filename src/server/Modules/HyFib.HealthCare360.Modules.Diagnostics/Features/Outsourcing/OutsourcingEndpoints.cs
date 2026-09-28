using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Files;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Outsourcing;

/// <summary>
/// /api/v1/lab/outsourcing: tests sent to reference laboratories (LIS-08). Covers dispatch with a courier/manifest
/// reference, receipt of the external report (stored encrypted and linked to the patient), and overdue tracking
/// against the laboratory's agreed turnaround. Results from the external report are then entered and verified as usual.
/// </summary>
internal static class OutsourcingEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/lab/outsourcing").WithTags("Laboratory");
        group.MapGet("/", List).RequireAnyPermission(DiagnosticsPermissions.LabOrdersRead, DiagnosticsPermissions.LabSamplesReceive);
        group.MapPost("/items/{itemId:guid}/send", Send).RequirePermission(DiagnosticsPermissions.LabSamplesReceive).WithValidation<SendOutRequest>();
        group.MapPost("/items/{itemId:guid}/receive", ReceiveReport).RequirePermission(DiagnosticsPermissions.LabResultsEnter).WithValidation<ReceiveExternalReportRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, bool? pendingOnly, Guid? referralLabId, HospitalDbContext db, DiagnosticsLookups lookups, TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var items = db.Set<LabOrderItem>().AsNoTracking().Where(i => i.ReferralLabId != null && i.Status != LabOrderItemStatus.Cancelled);
        if (pendingOnly != false)
        {
            items = items.Where(i => i.ExternalReportReceivedAt == null);
        }

        if (referralLabId is not null)
        {
            items = items.Where(i => i.ReferralLabId == referralLabId);
        }

        var paged = await (
                from i in items
                join o in db.Set<LabOrder>() on i.LabOrderId equals o.Id
                join s in db.Set<LabSample>() on i.SampleId equals (Guid?)s.Id into tubes
                from s in tubes.DefaultIfEmpty()
                join l in db.Set<ReferralLab>() on i.ReferralLabId equals (Guid?)l.Id into labs
                from l in labs.DefaultIfEmpty()
                orderby i.SentOutAt == null descending, i.SentOutAt
                select new
                {
                    i.Id, i.LabOrderId, o.AccessionNumber, o.PatientId, i.TestName, i.Status, i.SampleId, Barcode = s != null ? s.Barcode : null,
                    i.ReferralLabId, LabName = l != null ? l.Name : null, TatDays = l != null ? l.TatDays : 0,
                    CollectedAt = s != null ? s.CollectedAt : null, i.SentOutAt, i.DispatchReference, i.ExternalReportReceivedAt,
                })
            .ToPagedResultAsync(page, ct);

        var patients = await lookups.PatientsAsync(paged.Items.Select(i => i.PatientId).Distinct().ToList(), ct);
        var now = time.GetUtcNow();
        return TypedResults.Ok(paged.Map(i =>
        {
            DateTimeOffset? due = i.SentOutAt?.AddDays(i.TatDays);
            return new OutsourcedItem(
                i.Id, i.LabOrderId, i.AccessionNumber, i.PatientId, patients.GetValueOrDefault(i.PatientId)?.Uhid ?? string.Empty,
                patients.GetValueOrDefault(i.PatientId)?.FullName ?? string.Empty, i.TestName, i.Status, i.SampleId, i.Barcode, i.ReferralLabId, i.LabName,
                i.CollectedAt, i.SentOutAt, i.DispatchReference, i.ExternalReportReceivedAt, due, i.ExternalReportReceivedAt is null && due < now);
        }));
    }

    private static async Task<IResult> Send(
        Guid itemId, SendOutRequest request, HospitalDbContext db, DiagnosticsRealtime realtime, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var item = await db.Set<LabOrderItem>().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
        {
            return Error.NotFound(nameof(LabOrderItem), itemId).ToProblem();
        }

        if (!await db.Set<ReferralLab>().AnyAsync(l => l.Id == request.ReferralLabId && l.IsActive, ct))
        {
            return Error.Field("referralLabId", "Choose an active reference laboratory.").ToProblem();
        }

        var sent = item.SendOut(request.ReferralLabId, request.DispatchReference, user.UserId, time.GetUtcNow());
        if (sent.IsFailure)
        {
            return sent.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(item.FacilityId, "lab.outsourcing.sent", new { ItemId = item.Id, item.LabOrderId }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ReceiveReport(
        Guid itemId, ReceiveExternalReportRequest request, HospitalDbContext db, DiagnosticsRealtime realtime, TimeProvider time, CancellationToken ct)
    {
        var item = await db.Set<LabOrderItem>().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null)
        {
            return Error.NotFound(nameof(LabOrderItem), itemId).ToProblem();
        }

        if (request.ReportFileId is { } fileId)
        {
            var patientId = await db.Set<LabOrder>().Where(o => o.Id == item.LabOrderId).Select(o => o.PatientId).FirstAsync(ct);
            var file = await db.Set<StoredFile>().FirstOrDefaultAsync(f => f.Id == fileId, ct);
            if (file is null)
            {
                return Error.Field("reportFileId", "Upload the external report first.").ToProblem();
            }

            if (file.PatientId is { } owner && owner != patientId)
            {
                return Error.Rule("lab.file_other_patient", "This file belongs to a different patient.").ToProblem();
            }

            file.PatientId = patientId;
            file.OwnerModule ??= "diagnostics";
        }

        var received = item.ReceiveExternalReport(request.ExternalReference, request.ReportFileId, time.GetUtcNow());
        if (received.IsFailure)
        {
            return received.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(item.FacilityId, "lab.outsourcing.received", new { ItemId = item.Id, item.LabOrderId }, ct);
        return TypedResults.NoContent();
    }
}
