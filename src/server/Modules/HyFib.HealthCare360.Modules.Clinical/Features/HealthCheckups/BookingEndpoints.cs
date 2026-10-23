using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;

/// <summary>
/// /api/v1/health-checkups — bookings, the day's itinerary across departments, the reviewing doctor's summary,
/// the consolidated report and corporate batches (MHC-02..05).
/// </summary>
internal static class BookingEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/health-checkups").WithTags("Health Checkups");
        string[] stations = [ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrWrite, ClinicalPermissions.VitalsRecord];

        group.MapGet("/bookings", List).RequireAnyPermission(ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrRead);
        group.MapPost("/bookings", Book).RequirePermission(ClinicalPermissions.HealthCheckManage).WithValidation<BookCheckupRequest>();
        group.MapGet("/bookings/{id:guid}", Get).RequireAnyPermission(ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrRead);
        group.MapPost("/bookings/{id:guid}/check-in", CheckIn).RequirePermission(ClinicalPermissions.HealthCheckManage);
        group.MapPost("/bookings/{id:guid}/itinerary/{itemId:guid}/complete", CompleteStop).RequireAnyPermission(stations).WithValidation<CompleteStopRequest>();
        group.MapPost("/bookings/{id:guid}/itinerary/{itemId:guid}/skip", SkipStop).RequireAnyPermission(stations).WithValidation<SkipStopRequest>();
        group.MapPut("/bookings/{id:guid}/summary", Summary).RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<CheckupSummaryRequest>();
        group.MapPost("/bookings/{id:guid}/complete", Complete).RequireAnyPermission(ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrWrite);
        group.MapPost("/bookings/{id:guid}/cancel", Cancel).RequirePermission(ClinicalPermissions.HealthCheckManage).WithValidation<CancelBookingRequest>();
        group.MapPost("/bookings/{id:guid}/no-show", NoShow).RequirePermission(ClinicalPermissions.HealthCheckManage);
        group.MapGet("/bookings/{id:guid}/report", Report).RequireAnyPermission(ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrRead);

        group.MapGet("/corporate-batches", ListBatches).RequirePermission(ClinicalPermissions.HealthCheckManage);
        group.MapPost("/corporate-batches", BookBatch).RequirePermission(ClinicalPermissions.HealthCheckManage).WithValidation<CorporateBatchRequest>();
        group.MapGet("/corporate-batches/{id:guid}", GetBatch).RequirePermission(ClinicalPermissions.HealthCheckManage);
    }

    /// <summary>Bookings of a day (default today, IST) — the checkup desk's list.</summary>
    private static async Task<IResult> List([AsParameters] PageQuery query, DateOnly? date, BookingStatus? status, Guid? corporateBatchId, HospitalDbContext db,
        IFieldProtector protector, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var bookings = db.Set<PackageBooking>().AsNoTracking().Include(b => b.Itinerary).AsQueryable();
        bookings = corporateBatchId is not null
            ? bookings.Where(b => b.CorporateBatchId == corporateBatchId)
            : bookings.Where(b => b.ScheduledDate == (date ?? time.Today()));
        if (status is not null)
        {
            bookings = bookings.Where(b => b.Status == status);
        }

        if (page.SearchTerm is not null)
        {
            var matching = db.Set<Patient>().Search(page.SearchTerm, protector).Select(p => p.Id);
            bookings = bookings.Where(b => matching.Contains(b.PatientId) || b.BookingNumber == page.SearchTerm.ToUpper());
        }

        var result = await (
                from b in bookings
                join p in db.Set<Patient>() on b.PatientId equals p.Id
                orderby b.ScheduledDate, b.BookingNumber
                select new { Booking = b, p.Uhid, p.FullName })
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(x => HealthCheckupService.ToDto(x.Booking, x.Uhid, x.FullName)));
    }

    private static async Task<IResult> Book(BookCheckupRequest request, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.BookAsync(request, ct)).ToCreated(b => $"/api/v1/health-checkups/bookings/{b.Id}");

    private static async Task<IResult> Get(Guid id, HealthCheckupService checkups, ClinicalAccess access, CancellationToken ct)
    {
        var booking = await checkups.Bookings().AsNoTracking().Include(b => b.Itinerary).FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null)
        {
            return Error.NotFound("Health checkup booking", id).ToProblem();
        }

        var opened = await access.OpenAsync(booking.PatientId, "Viewed health checkup", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await checkups.ToDtoAsync(booking, ct));
    }

    private static async Task<IResult> CheckIn(Guid id, CheckInCheckupRequest request, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.CheckInAsync(id, request.VisitId, ct)).ToOk();

    private static async Task<IResult> CompleteStop(Guid id, Guid itemId, CompleteStopRequest request, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.CompleteStopAsync(id, itemId, request.Notes, ct)).ToOk();

    private static async Task<IResult> SkipStop(Guid id, Guid itemId, SkipStopRequest request, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.SkipStopAsync(id, itemId, request.Reason, ct)).ToOk();

    private static async Task<IResult> Summary(Guid id, CheckupSummaryRequest request, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.RecordSummaryAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Complete(Guid id, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.CompleteAsync(id, ct)).ToOk();

    private static async Task<IResult> Cancel(Guid id, CancelBookingRequest request, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.CancelAsync(id, request.Reason, ct)).ToOk();

    private static async Task<IResult> NoShow(Guid id, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.NoShowAsync(id, ct)).ToOk();

    private static async Task<IResult> Report(Guid id, HealthCheckupService checkups, ClinicalAccess access, CancellationToken ct)
    {
        var report = await checkups.ReportAsync(id, ct);
        if (report.IsFailure)
        {
            return report.Error!.ToProblem();
        }

        var opened = await access.OpenAsync(report.Value.Booking.PatientId, "Viewed health checkup report", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(report.Value);
    }

    private static async Task<IResult> ListBatches([AsParameters] PageQuery query, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var batches = db.Set<CorporateCheckupBatch>().AsNoTracking();
        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            batches = batches.Where(b => EF.Functions.ILike(b.CorporateName, like, QueryExtensions.LikeEscape) || b.BatchNumber == term.ToUpper());
        }

        return TypedResults.Ok(await batches.OrderByDescending(b => b.ScheduledDate)
            .Select(b => new CorporateBatchDto(b.Id, b.BatchNumber, b.PayerId, b.CorporateName, b.ContactPerson, b.ContactPhone, b.Reference, b.PackageId, b.ScheduledDate,
                b.BookedCount, b.SkippedCount, b.Notes, b.CreatedAt))
            .ToPagedResultAsync(page, ct));
    }

    private static async Task<IResult> BookBatch(CorporateBatchRequest request, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.BookCorporateAsync(request, ct)).ToCreated(r => $"/api/v1/health-checkups/corporate-batches/{r.BatchId}");

    private static async Task<IResult> GetBatch(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var batch = await db.Set<CorporateCheckupBatch>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
        {
            return Error.NotFound("Corporate batch", id).ToProblem();
        }

        var bookings = await (
                from b in db.Set<PackageBooking>().AsNoTracking().Include(b => b.Itinerary)
                join p in db.Set<Patient>() on b.PatientId equals p.Id
                where b.CorporateBatchId == id
                orderby p.FullName
                select new { Booking = b, p.Uhid, p.FullName })
            .ToListAsync(ct);
        return TypedResults.Ok(new
        {
            Batch = new CorporateBatchDto(batch.Id, batch.BatchNumber, batch.PayerId, batch.CorporateName, batch.ContactPerson, batch.ContactPhone, batch.Reference, batch.PackageId,
                batch.ScheduledDate, batch.BookedCount, batch.SkippedCount, batch.Notes, batch.CreatedAt),
            Bookings = bookings.Select(x => HealthCheckupService.ToDto(x.Booking, x.Uhid, x.FullName)).ToList(),
        });
    }
}
