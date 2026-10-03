using System.Runtime.InteropServices;
using System.Threading.RateLimiting;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;

/// <summary>
/// /api/v1/radiology/studies/{id}/images and /viewer: a study's images from PACS, fetched through the server (IMG-02),
/// and the PACS viewer link. Images are patient data: every call needs <see cref="DiagnosticsPermissions.RadiologyImages"/>,
/// passes the restricted-record guard and is audited (<see cref="StudyImageService.AuditAsync"/>). Images reach the
/// browser only as JPEG or PNG within a size limit, and API responses are never cached (<c>Cache-Control: no-store</c>).
/// Thumbnails and images are fetched from PACS and held in memory while they are sent, so each person has at most
/// <see cref="RadiologyImageLimits.PermitLimit"/> in flight (<see cref="RadiologyImageLimits"/>).
/// </summary>
internal static class RadiologyImageEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var images = app.MapGroup("/api/v1/radiology/studies/{id:guid}").WithTags("Radiology")
            .RequirePermission(DiagnosticsPermissions.RadiologyImages);
        images.MapGet("/images", Series);
        images.MapGet("/images/series/{seriesUid}", SeriesImages);
        images.MapGet("/images/series/{seriesUid}/thumbnail", Thumbnail)
            .RequireRateLimiting(RadiologyImageLimits.Policy);
        images.MapGet("/images/series/{seriesUid}/instances/{instanceUid}/rendered", Rendered)
            .RequireRateLimiting(RadiologyImageLimits.Policy);
        images.MapGet("/viewer", Viewer);
    }

    private static async Task<IResult> Series(Guid id, StudyImageService images, PatientAccessGuard guard, CancellationToken ct) =>
        await OpenAsync(id, images, guard, ct) is { } opened
            ? opened.Problem ?? await AuditedAsync(opened.Study, images, always: true, await images.SeriesAsync(opened.Study, ct), ct)
            : NotFound(id);

    private static async Task<IResult> SeriesImages(Guid id, string seriesUid, StudyImageService images, PatientAccessGuard guard, CancellationToken ct) =>
        await OpenAsync(id, images, guard, ct) is { } opened
            ? opened.Problem ?? await AuditedAsync(opened.Study, images, always: false, await images.InstancesAsync(opened.Study, seriesUid, ct), ct)
            : NotFound(id);

    private static async Task<IResult> Thumbnail(Guid id, string seriesUid, StudyImageService images, PatientAccessGuard guard, CancellationToken ct) =>
        await OpenAsync(id, images, guard, ct) is { } opened
            ? opened.Problem ?? await ImageAsync(opened.Study, images, await images.ThumbnailAsync(opened.Study, seriesUid, ct), ct)
            : NotFound(id);

    private static async Task<IResult> Rendered(
        Guid id, string seriesUid, string instanceUid, int? frame, int? size, StudyImageService images, PatientAccessGuard guard, CancellationToken ct)
    {
        if (frame is < 1 or > StudyImageService.MaxFrame)
        {
            return Error.Field("frame", $"The frame is a number from 1 to {StudyImageService.MaxFrame}.").ToProblem();
        }

        if (size is < StudyImageService.MinImageSize or > StudyImageService.MaxImageSize)
        {
            return Error.Field("size", $"The size is from {StudyImageService.MinImageSize} to {StudyImageService.MaxImageSize} pixels.").ToProblem();
        }

        return await OpenAsync(id, images, guard, ct) is { } opened
            ? opened.Problem ?? await ImageAsync(opened.Study, images,
                await images.RenderedAsync(opened.Study, seriesUid, instanceUid, frame, size ?? StudyImageService.DefaultImageSize, ct), ct)
            : NotFound(id);
    }

    /// <summary>Opens the PACS viewer for the study (from the patient record); image access is a PHI read and is audited.</summary>
    private static async Task<IResult> Viewer(Guid id, ImagingService service, StudyImageService images, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        if (await OpenAsync(id, images, guard, ct) is not { } opened)
        {
            return NotFound(id);
        }

        if (opened.Problem is { } problem)
        {
            return problem;
        }

        var link = await service.ViewerAsync(id, ct);
        if (link.IsSuccess)
        {
            await audit.PatientRecordAccessedAsync(opened.Study.PatientId, $"Opened images of {opened.Study.AccessionNumber}", ct);
        }

        return link.ToOk();
    }

    /// <summary>The study in the signed-in facility, after the restricted-record guard (null when there is no such study).</summary>
    private static async Task<(StudyImageContext Study, IResult? Problem)?> OpenAsync(Guid id, StudyImageService images, PatientAccessGuard guard, CancellationToken ct)
    {
        var study = await images.ContextAsync(id, ct);
        if (study is null)
        {
            return null;
        }

        var access = await guard.EnsureCanOpenAsync(study.PatientId, ct);
        return (study, access.IsFailure ? access.Error!.ToProblem() : null);
    }

    private static async Task<IResult> AuditedAsync<T>(StudyImageContext study, StudyImageService images, bool always, Result<T> result, CancellationToken ct)
    {
        if (result.IsSuccess)
        {
            await images.AuditAsync(study, always, ct);
        }

        return result.ToOk();
    }

    private static async Task<IResult> ImageAsync(StudyImageContext study, StudyImageService images, Result<PacsImage> result, CancellationToken ct)
    {
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await images.AuditAsync(study, always: false, ct);
        // The image as PACS sent it, from the buffer it was read into (no copy).
        var image = result.Value;
        return MemoryMarshal.TryGetArray(image.Content, out var bytes)
            ? TypedResults.File(
                new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false), image.ContentType)
            : TypedResults.File(image.Content.ToArray(), image.ContentType);
    }

    private static IResult NotFound(Guid id) => Error.NotFound(nameof(ImagingStudy), id).ToProblem();
}

/// <summary>
/// The rate-limiting policy of the image endpoints: per signed-in person, at most <see cref="PermitLimit"/> thumbnails
/// or images fetched from PACS at once; the next <see cref="QueueLimit"/> wait their turn (a viewer opening a study
/// asks for every series' thumbnail together), and beyond that the answer is 429 Too Many Requests.
/// </summary>
internal static class RadiologyImageLimits
{
    public const string Policy = "radiology-images";
    public const int PermitLimit = 6;
    public const int QueueLimit = 32;

    public static RateLimitPartition<string> Partition(HttpContext http) =>
        RateLimitPartition.GetConcurrencyLimiter(
            http.User.FindFirst(HcClaims.UserId)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = PermitLimit,
                QueueLimit = QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
}
