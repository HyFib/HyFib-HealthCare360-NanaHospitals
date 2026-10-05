using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using HyFib.HealthCare360.Integrations.Laboratory;

namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>
/// A DICOMweb PACS (INT-PACS): QIDO-RS study, series and instance searches, and WADO-RS rendered images and thumbnails,
/// with none/basic/bearer authentication. Results contain patient names, so only the query shape and counts are logged.
/// A viewer fetches hundreds of images, so image retrievals are logged only when they fail.
/// </summary>
internal sealed class DicomWebPacsProvider(HttpClient http, IntegrationTrace trace) : IPacsProvider
{
    public string Key => ProviderKeys.DicomWeb;

    public async Task<bool> PingAsync(PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        try
        {
            var (status, _) = await GetTextAsync(settings.QidoUrl, "studies?limit=1", settings, secrets, cancellationToken);
            var ok = status is HttpStatusCode.OK or HttpStatusCode.NoContent;
            await trace.LogAsync(IntegrationNames.Pacs, Key, "ping", ok, started, null, "studies?limit=1",
                string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)status}"), ok ? null : $"The PACS returned HTTP {(int)status}.");
            return ok;
        }
        catch (Exception ex) when (ex is IntegrationException || IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            await trace.LogAsync(IntegrationNames.Pacs, Key, "ping", false, started, error: IntegrationErrors.Describe(ex, "The PACS"));
            return false;
        }
    }

    public Task<IReadOnlyList<ImagingStudyInfo>> FindStudiesAsync(PacsStudyQuery query, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var summary = $"{{\"accession\":\"{query.AccessionNumber}\",\"studyUid\":\"{query.StudyInstanceUid}\",\"patientId\":\"{Masking.MaskIdentifier(query.PatientId)}\"," +
                      $"\"from\":\"{query.From}\",\"to\":\"{query.To}\",\"modality\":\"{query.Modality}\"}}";
        return SearchAsync("qido.studies", QidoQuery.Build(query, settings.Limit), query.AccessionNumber ?? query.StudyInstanceUid, summary,
            body => DicomJson.ParseStudies(body, LabTime.ResolveZone(settings.TimeZone)), "stud", settings, secrets, cancellationToken);
    }

    public Task<IReadOnlyList<ImagingSeriesInfo>> FindSeriesAsync(string studyInstanceUid, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken) =>
        SearchAsync("qido.series", QidoQuery.Series(studyInstanceUid), studyInstanceUid, "series of one study", DicomJson.ParseSeries, "series", settings,
            secrets, cancellationToken);

    public async Task<IReadOnlyList<ImagingInstanceInfo>> FindInstancesAsync(
        string studyInstanceUid, string seriesInstanceUid, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        // A thin-slice CT series can hold thousands of objects: read them in pages, up to a limit.
        var instances = new List<ImagingInstanceInfo>();
        for (var offset = 0; offset < QidoQuery.MaxInstances; offset += QidoQuery.InstancePageSize)
        {
            var page = await SearchAsync("qido.instances", QidoQuery.Instances(studyInstanceUid, seriesInstanceUid, offset), seriesInstanceUid,
                string.Create(CultureInfo.InvariantCulture, $"objects of one series from {offset}"), DicomJson.ParseInstances, "object", settings, secrets,
                cancellationToken);
            instances.AddRange(page);
            if (page.Count < QidoQuery.InstancePageSize)
            {
                break;
            }
        }

        return instances
            .DistinctBy(i => i.SopInstanceUid, StringComparer.Ordinal)
            .OrderBy(i => i.InstanceNumber ?? int.MaxValue)
            .ThenBy(i => i.SopInstanceUid, StringComparer.Ordinal)
            .Take(QidoQuery.MaxInstances)
            .ToList();
    }

    public async Task<PacsImage> RetrieveImageAsync(PacsImageRequest request, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var operation = request.Kind == PacsImageKind.Thumbnail ? "wado.thumbnail" : "wado.rendered";
        var path = QidoQuery.Image(request);
        try
        {
            if (!IntegrationHttp.TryCreateEndpoint(settings.WadoRoot, requireHttps: false, out var baseUri))
            {
                throw new IntegrationException("The PACS DICOMweb URL is not configured.");
            }

            using var message = CreateRequest(IntegrationHttp.Combine(baseUri, path), settings, secrets);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(PacsImageContent.Jpeg));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(PacsImageContent.Png, 0.9));
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var image = await PacsImageContent.ReadAsync(response, PacsImageContent.LimitFor(request.Kind), cancellationToken);
                    return new PacsImage(PacsImageStatus.Ok, image.Content, image.ContentType);
                case HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.Gone:
                    return new PacsImage(PacsImageStatus.NotFound);
                case HttpStatusCode.BadRequest or HttpStatusCode.NotAcceptable or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.NotImplemented:
                    // The PACS has the object but does not offer this representation (thumbnails are optional in DICOMweb).
                    return new PacsImage(PacsImageStatus.NotAvailable);
                default:
                    throw new IntegrationException(string.Create(CultureInfo.InvariantCulture, $"The PACS returned HTTP {(int)response.StatusCode} for an image."));
            }
        }
        catch (IntegrationException ex)
        {
            await trace.LogAsync(IntegrationNames.Pacs, Key, operation, false, started, request.SeriesInstanceUid, Shape(request), error: ex.Message);
            throw;
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "The PACS");
            await trace.LogAsync(IntegrationNames.Pacs, Key, operation, false, started, request.SeriesInstanceUid, Shape(request), error: error);
            throw new IntegrationException(error, ex);
        }
    }

    /// <summary>What an image request asked for, without UIDs beyond the series reference.</summary>
    private static string Shape(PacsImageRequest request) =>
        string.Create(CultureInfo.InvariantCulture, $"{request.Kind}{(request.SopInstanceUid is null ? " of the series" : " of one object")}{(request.Frame is { } frame ? $", frame {frame}" : string.Empty)}, {request.Size} px");

    private async Task<IReadOnlyList<T>> SearchAsync<T>(
        string operation, string path, string? reference, string summary, Func<string, IReadOnlyList<T>> parse, string noun,
        PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        try
        {
            var (status, body) = await GetTextAsync(settings.QidoUrl, path, settings, secrets, cancellationToken);
            if (status is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
            {
                var error = $"The PACS returned HTTP {(int)status}.";
                await trace.LogAsync(IntegrationNames.Pacs, Key, operation, false, started, reference, summary, error: error);
                throw new IntegrationException(error);
            }

            var found = status == HttpStatusCode.NoContent ? [] : parse(body);
            await trace.LogAsync(IntegrationNames.Pacs, Key, operation, true, started, reference, summary, Count(found.Count, noun));
            return found;
        }
        catch (Exception ex) when (ex is not IntegrationException && IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "The PACS");
            await trace.LogAsync(IntegrationNames.Pacs, Key, operation, false, started, reference, summary, error: error);
            throw new IntegrationException(error, ex);
        }
    }

    private static string Count(int count, string noun) => noun switch
    {
        "stud" => string.Create(CultureInfo.InvariantCulture, $"{count} stud{(count == 1 ? "y" : "ies")}"),
        "series" => string.Create(CultureInfo.InvariantCulture, $"{count} series"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}"),
    };

    private async Task<(HttpStatusCode Status, string Body)> GetTextAsync(
        string? root, string relative, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        if (!IntegrationHttp.TryCreateEndpoint(root, requireHttps: false, out var baseUri))
        {
            throw new IntegrationException("The PACS DICOMweb URL is not configured.");
        }

        using var request = CreateRequest(IntegrationHttp.Combine(baseUri, relative), settings, secrets);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dicom+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
        using var response = await http.SendAsync(request, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static HttpRequestMessage CreateRequest(Uri uri, PacsSettings settings, PacsSecrets secrets)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        switch (settings.AuthType.Trim().ToLowerInvariant())
        {
            case "basic" when !string.IsNullOrWhiteSpace(settings.UserName):
                IntegrationHttp.SetBasicAuth(request, settings.UserName, secrets.Password ?? string.Empty);
                break;
            case "bearer" when !string.IsNullOrWhiteSpace(secrets.BearerToken):
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.BearerToken);
                break;
        }

        return request;
    }
}
