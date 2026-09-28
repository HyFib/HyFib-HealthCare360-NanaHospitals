using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;

/// <summary>
/// /api/v1/lab/samples: the phlebotomy worklist, collection with label data, receipt by barcode scan, rejection and
/// recollection (LIS-02, LIS-03).
/// </summary>
internal static class SampleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var samples = app.MapGroup("/api/v1/lab/samples").WithTags("Laboratory");
        samples.MapGet("/pending-collection", PendingCollection).RequireAnyPermission(DiagnosticsPermissions.LabSamplesCollect, DiagnosticsPermissions.LabOrdersRead);
        samples.MapGet("/by-barcode/{barcode}", ByBarcode)
            .RequireAnyPermission(DiagnosticsPermissions.LabSamplesReceive, DiagnosticsPermissions.LabResultsEnter, DiagnosticsPermissions.LabOrdersRead);
        samples.MapPost("/receive", Receive).RequirePermission(DiagnosticsPermissions.LabSamplesReceive).WithValidation<ReceiveSamplesRequest>();
        samples.MapPost("/{id:guid}/reject", Reject).RequirePermission(DiagnosticsPermissions.LabSamplesReceive).WithValidation<RejectSampleRequest>();
        samples.MapPost("/{id:guid}/recollect", Recollect).RequirePermission(DiagnosticsPermissions.LabSamplesReceive);

        var orders = app.MapGroup("/api/v1/lab/orders").WithTags("Laboratory");
        orders.MapPost("/{id:guid}/collect", Collect).RequirePermission(DiagnosticsPermissions.LabSamplesCollect).WithValidation<CollectSamplesRequest>();
        orders.MapGet("/{id:guid}/labels", Labels).RequireAnyPermission(DiagnosticsPermissions.LabSamplesCollect, DiagnosticsPermissions.LabSamplesReceive);
    }

    /// <summary>Tubes waiting to be drawn: STAT first, then oldest first. Recollections are marked.</summary>
    private static async Task<IResult> PendingCollection(
        [AsParameters] PageQuery query,
        LabSection? section,
        OrderPriority? priority,
        PatientClass? patientClass,
        HospitalDbContext db,
        DiagnosticsLookups lookups,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var tubes = db.Set<LabSample>().AsNoTracking().Where(s => s.Status == SampleStatus.Pending);
        if (section is not null)
        {
            tubes = tubes.Where(s => s.Section == section);
        }

        if (priority is not null)
        {
            tubes = tubes.Where(s => s.Priority == priority);
        }

        if (patientClass is not null)
        {
            tubes = tubes.Where(s => db.Set<LabOrder>().Any(o => o.Id == s.LabOrderId && o.PatientClass == patientClass));
        }

        if (page.SearchTerm is { } term)
        {
            var code = term.ToUpperInvariant();
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            tubes = tubes.Where(s => s.Barcode == term || matching.Contains(s.PatientId)
                                     || db.Set<LabOrder>().Any(o => o.Id == s.LabOrderId && o.AccessionNumber == code));
        }

        var paged = await tubes.OrderByUrgency(s => s.Priority).ThenBy(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Barcode, s.LabOrderId, s.PatientId, s.Priority, s.Container, s.SpecimenType, s.Section, s.RecollectionOfSampleId })
            .ToPagedResultAsync(page, ct);

        var orderIds = paged.Items.Select(s => s.LabOrderId).Distinct().ToList();
        var tubeIds = paged.Items.Select(s => s.Id).ToList();
        var orders = await db.Set<LabOrder>().AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.AccessionNumber, o.PatientClass, o.OrderedAt }).ToDictionaryAsync(o => o.Id, ct);
        var items = await (
                from i in db.Set<LabOrderItem>().AsNoTracking()
                join t in db.Set<LabTest>() on i.LabTestId equals t.Id
                where i.SampleId != null && tubeIds.Contains(i.SampleId.Value) && i.Status != LabOrderItemStatus.Cancelled
                select new { SampleId = i.SampleId!.Value, i.TestName, t.Preparation })
            .ToListAsync(ct);
        var patients = await lookups.PatientsAsync(paged.Items.Select(s => s.PatientId).Distinct().ToList(), ct);
        var today = DiagnosticsTime.Today(time);

        return TypedResults.Ok(paged.Map(s =>
        {
            var patient = patients.GetValueOrDefault(s.PatientId);
            var order = orders.GetValueOrDefault(s.LabOrderId);
            var tests = items.Where(i => i.SampleId == s.Id).ToList();
            return new PendingCollectionItem(
                s.Id, s.Barcode, s.LabOrderId, order?.AccessionNumber ?? string.Empty, s.PatientId, patient?.Uhid ?? string.Empty,
                patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown, patient?.AgeOn(today), order?.PatientClass ?? PatientClass.OutPatient,
                s.Priority, s.Container, s.SpecimenType, s.Section, tests.Select(t => t.TestName).ToList(),
                tests.Select(t => t.Preparation).OfType<string>().Distinct().ToList(), order?.OrderedAt ?? default, s.RecollectionOfSampleId is not null);
        }));
    }

    private static async Task<IResult> Collect(Guid id, CollectSamplesRequest request, SampleService samples, CancellationToken ct) =>
        (await samples.CollectAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Labels(Guid id, Guid? sampleId, SampleService samples, CancellationToken ct) =>
        (await samples.LabelsAsync(id, sampleId, ct)).ToOk();

    private static async Task<IResult> Receive(ReceiveSamplesRequest request, SampleService samples, CancellationToken ct) =>
        TypedResults.Ok(await samples.ReceiveAsync(request.Barcodes, ct));

    private static async Task<IResult> Reject(Guid id, RejectSampleRequest request, SampleService samples, CancellationToken ct) =>
        (await samples.RejectAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Recollect(Guid id, SampleService samples, CancellationToken ct) =>
        (await samples.RecollectAsync(id, ct)).ToOk();

    private static async Task<IResult> ByBarcode(string barcode, SampleService samples, CancellationToken ct) =>
        await samples.FindByBarcodeAsync(barcode, ct) is { } sample
            ? TypedResults.Ok(sample)
            : Error.NotFound(nameof(LabSample), barcode).ToProblem();
}
