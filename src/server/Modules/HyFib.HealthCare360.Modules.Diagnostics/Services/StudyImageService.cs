using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>What the image endpoints need to know about a study before anything is asked of PACS.</summary>
internal sealed record StudyImageContext(Guid Id, Guid FacilityId, Guid PatientId, string AccessionNumber, string StudyInstanceUid, bool Linked);

/// <summary>
/// The images of a study, fetched from PACS through the server (IMG-02): series with thumbnails, the objects of a series,
/// and rendered images for the viewer. PACS credentials never leave the server. The study comes from HC360's database,
/// never from the caller: a series must be one PACS lists for the study's UID, and an object one it lists for that
/// series, so a caller cannot reach another study's images by naming its UIDs. Those lists are kept for
/// <see cref="IndexLifetime"/>, so a viewer moving through a series does not search PACS for every image; a UID missing
/// from the kept list is looked up again once, for images that reached PACS meanwhile.
/// </summary>
internal sealed class StudyImageService(
    HospitalDbContext db,
    IServiceProvider services,
    IMemoryCache cache,
    IAuditLogger audit,
    ICurrentUser currentUser,
    ILogger<StudyImageService> logger)
{
    public const int ThumbnailSize = 160;
    public const int DefaultImageSize = 1024;
    public const int MinImageSize = 64;
    public const int MaxImageSize = 2048;
    public const int MaxFrame = 10_000;

    public static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Viewing a study's images writes one PHI access entry per person and study in this time.</summary>
    public static readonly TimeSpan AuditWindow = TimeSpan.FromMinutes(15);

    private static Error NotLinked => Error.Rule("pacs.not_linked", "The images of this study are not linked yet. Link the study with PACS first.");

    /// <summary>The study, in the signed-in facility; null when it does not exist there.</summary>
    public Task<StudyImageContext?> ContextAsync(Guid id, CancellationToken ct) =>
        db.Set<ImagingStudy>().AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new StudyImageContext(s.Id, s.FacilityId, s.PatientId, s.AccessionNumber, s.StudyInstanceUid, s.PacsLinkedAt != null))
            .FirstOrDefaultAsync(ct);

    /// <summary>The picture series of the study, read from PACS every time (the series list is what a person opens).</summary>
    public async Task<Result<StudyImagesDto>> SeriesAsync(StudyImageContext study, CancellationToken ct)
    {
        if (Unready(study) is { } problem)
        {
            return problem;
        }

        var series = await LoadSeriesAsync(study, ct);
        return series.IsFailure
            ? series.Error!
            : new StudyImagesDto(study.Id, study.AccessionNumber, study.StudyInstanceUid, series.Value.Select(ToDto).ToList());
    }

    /// <summary>The objects of one of the study's series, in instance-number order.</summary>
    public async Task<Result<ImageSeriesDetail>> InstancesAsync(StudyImageContext study, string seriesUid, CancellationToken ct)
    {
        var series = await FindSeriesAsync(study, seriesUid, ct);
        if (series.IsFailure)
        {
            return series.Error!;
        }

        var instances = await LoadInstancesAsync(study, series.Value.SeriesInstanceUid, ct);
        return instances.IsFailure
            ? instances.Error!
            : new ImageSeriesDetail(study.Id, ToDto(series.Value),
                instances.Value.Select(i => new ImageInstanceDto(i.SopInstanceUid, i.InstanceNumber, i.Frames)).ToList());
    }

    /// <summary>
    /// A small picture of the series: the PACS's own thumbnail, or, when it has none (thumbnails are optional in
    /// DICOMweb), the middle image of the series rendered small.
    /// </summary>
    public async Task<Result<PacsImage>> ThumbnailAsync(StudyImageContext study, string seriesUid, CancellationToken ct)
    {
        var series = await FindSeriesAsync(study, seriesUid, ct);
        if (series.IsFailure)
        {
            return series.Error!;
        }

        var uid = series.Value.SeriesInstanceUid;
        var thumbnail = await RetrieveAsync(new PacsImageRequest(study.StudyInstanceUid, uid, null, null, PacsImageKind.Thumbnail, ThumbnailSize, study.FacilityId), ct);
        if (thumbnail.IsFailure || thumbnail.Value.Status == PacsImageStatus.Ok)
        {
            return thumbnail;
        }

        var instances = await CachedInstancesAsync(study, uid, ct);
        if (instances.IsFailure)
        {
            return instances.Error!;
        }

        if (instances.Value.Count == 0)
        {
            return Error.NotFound("Image");
        }

        var middle = instances.Value[instances.Value.Count / 2];
        var rendered = await RetrieveAsync(new PacsImageRequest(
            study.StudyInstanceUid, uid, middle.SopInstanceUid, middle.Frames > 1 ? 1 : null, PacsImageKind.Rendered, ThumbnailSize, study.FacilityId), ct);
        return rendered.IsSuccess && rendered.Value.Status != PacsImageStatus.Ok ? Error.NotFound("Image") : rendered;
    }

    /// <summary>One image (or one frame of a multi-frame object) of the study, rendered by PACS at <paramref name="size"/> pixels.</summary>
    public async Task<Result<PacsImage>> RenderedAsync(StudyImageContext study, string seriesUid, string instanceUid, int? frame, int size, CancellationToken ct)
    {
        var series = await FindSeriesAsync(study, seriesUid, ct);
        if (series.IsFailure)
        {
            return series.Error!;
        }

        var uid = series.Value.SeriesInstanceUid;
        var instance = await FindInstanceAsync(study, uid, instanceUid, ct);
        if (instance.IsFailure)
        {
            return instance.Error!;
        }

        if (frame > instance.Value.Frames)
        {
            return Error.Field("frame", $"This image has {instance.Value.Frames} frame{(instance.Value.Frames == 1 ? string.Empty : "s")}.");
        }

        var image = await RetrieveAsync(new PacsImageRequest(
            study.StudyInstanceUid, uid, instance.Value.SopInstanceUid, instance.Value.Frames > 1 ? frame ?? 1 : null, PacsImageKind.Rendered,
            Math.Clamp(size, MinImageSize, MaxImageSize), study.FacilityId), ct);
        return image.IsSuccess && image.Value.Status != PacsImageStatus.Ok ? Error.NotFound("Image") : image;
    }

    /// <summary>
    /// Records that the signed-in person looked at the study's images. Opening the series list always writes an entry;
    /// thumbnails and images write one only when the person has no entry for the study in the last
    /// <see cref="AuditWindow"/>, because a viewer fetches hundreds of images.
    /// </summary>
    public async Task AuditAsync(StudyImageContext study, bool always, CancellationToken ct)
    {
        var key = $"radiology-images-audit:{currentUser.UserId}:{study.Id}";
        if (!always && cache.TryGetValue(key, out _))
        {
            return;
        }

        cache.Set(key, true, AuditWindow);
        await audit.PatientRecordAccessedAsync(study.PatientId, $"Viewed the images of {study.AccessionNumber}", ct);
    }

    private static ImageSeriesDto ToDto(ImagingSeriesInfo series) =>
        new(series.SeriesInstanceUid, series.SeriesNumber, series.Modality, series.Description, series.BodyPart, series.InstanceCount);

    /// <summary>PACS is an optional integration: resolved per call so that an unconfigured PACS is reported, not fatal.</summary>
    private IPacsClient? Pacs => services.GetService(typeof(IPacsClient)) as IPacsClient;

    private Error? Unready(StudyImageContext study) =>
        Pacs is null ? ImagingService.PacsNotConfigured
        : !study.Linked ? NotLinked
        : !DicomConventions.IsValidUid(study.StudyInstanceUid) ? Error.Rule("pacs.invalid_uid", "The study's instance UID is not valid, so its images cannot be found.")
        : null;

    private static string SeriesKey(StudyImageContext study) => $"pacs-series:{study.FacilityId}:{study.StudyInstanceUid}";

    private static string InstancesKey(StudyImageContext study, string seriesUid) => $"pacs-instances:{study.FacilityId}:{study.StudyInstanceUid}:{seriesUid}";

    private async Task<Result<ImagingSeriesInfo>> FindSeriesAsync(StudyImageContext study, string seriesUid, CancellationToken ct)
    {
        if (Unready(study) is { } problem)
        {
            return problem;
        }

        if (!DicomConventions.IsValidUid(seriesUid))
        {
            return Error.NotFound("Image series");
        }

        if (cache.TryGetValue(SeriesKey(study), out IReadOnlyList<ImagingSeriesInfo>? kept) && kept!.FirstOrDefault(s => s.SeriesInstanceUid == seriesUid) is { } cached)
        {
            return cached;
        }

        var series = await LoadSeriesAsync(study, ct);
        if (series.IsFailure)
        {
            return series.Error!;
        }

        return series.Value.FirstOrDefault(s => s.SeriesInstanceUid == seriesUid) is { } found ? found : Error.NotFound("Image series");
    }

    private async Task<Result<ImagingInstanceInfo>> FindInstanceAsync(StudyImageContext study, string seriesUid, string instanceUid, CancellationToken ct)
    {
        if (!DicomConventions.IsValidUid(instanceUid))
        {
            return Error.NotFound("Image");
        }

        if (cache.TryGetValue(InstancesKey(study, seriesUid), out IReadOnlyList<ImagingInstanceInfo>? kept)
            && kept!.FirstOrDefault(i => i.SopInstanceUid == instanceUid) is { } cached)
        {
            return cached;
        }

        var instances = await LoadInstancesAsync(study, seriesUid, ct);
        if (instances.IsFailure)
        {
            return instances.Error!;
        }

        return instances.Value.FirstOrDefault(i => i.SopInstanceUid == instanceUid) is { } found ? found : Error.NotFound("Image");
    }

    private async Task<Result<IReadOnlyList<ImagingInstanceInfo>>> CachedInstancesAsync(StudyImageContext study, string seriesUid, CancellationToken ct) =>
        cache.TryGetValue(InstancesKey(study, seriesUid), out IReadOnlyList<ImagingInstanceInfo>? kept) && kept is not null
            ? Result<IReadOnlyList<ImagingInstanceInfo>>.Ok(kept)
            : await LoadInstancesAsync(study, seriesUid, ct);

    private Task<Result<IReadOnlyList<ImagingSeriesInfo>>> LoadSeriesAsync(StudyImageContext study, CancellationToken ct) =>
        CallAsync(SeriesKey(study), pacs => pacs.FindSeriesAsync(study.StudyInstanceUid, study.FacilityId, ct), study, ct);

    private Task<Result<IReadOnlyList<ImagingInstanceInfo>>> LoadInstancesAsync(StudyImageContext study, string seriesUid, CancellationToken ct) =>
        CallAsync(InstancesKey(study, seriesUid), pacs => pacs.FindInstancesAsync(study.StudyInstanceUid, seriesUid, study.FacilityId, ct), study, ct);

    private async Task<Result<IReadOnlyList<T>>> CallAsync<T>(string key, Func<IPacsClient, Task<IReadOnlyList<T>>> call, StudyImageContext study, CancellationToken ct)
    {
        try
        {
            var found = await call(Pacs!);
            cache.Set(key, found, IndexLifetime);
            return Result<IReadOnlyList<T>>.Ok(found);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("PACS search for the images of {Accession} failed: {Reason}", study.AccessionNumber, ex.Message);
            return Unavailable(ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error.Rule("pacs.unreachable", "PACS did not respond in time. Try again shortly.");
        }
    }

    private async Task<Result<PacsImage>> RetrieveAsync(PacsImageRequest request, CancellationToken ct)
    {
        try
        {
            return await Pacs!.RetrieveImageAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("PACS image for series {Series} failed: {Reason}", request.SeriesInstanceUid, ex.Message);
            return Unavailable(ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error.Rule("pacs.unreachable", "PACS did not respond in time. Try again shortly.");
        }
    }

    /// <summary>The PACS router's messages are written for staff (no addresses, credentials or payloads).</summary>
    private static Error Unavailable(HttpRequestException ex) =>
        Error.Rule("pacs.unreachable", string.IsNullOrWhiteSpace(ex.Message) ? "PACS could not be reached. Try again shortly." : ex.Message);
}
