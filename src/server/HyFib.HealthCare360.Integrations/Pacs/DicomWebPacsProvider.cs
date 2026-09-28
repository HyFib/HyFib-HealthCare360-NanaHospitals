using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using HyFib.HealthCare360.Integrations.Laboratory;

namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>
/// QIDO-RS study search against a DICOMweb PACS (INT-PACS: study-to-order reconciliation by accession number), with
/// none/basic/bearer authentication. Results contain patient names, so only the query shape and counts are logged.
/// </summary>
internal sealed class DicomWebPacsProvider(HttpClient http, IntegrationTrace trace) : IPacsProvider
{
    public string Key => ProviderKeys.DicomWeb;

    public async Task<bool> PingAsync(PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        try
        {
            var (status, _) = await GetAsync("studies?limit=1", settings, secrets, cancellationToken);
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

    public async Task<IReadOnlyList<ImagingStudyInfo>> FindStudiesAsync(PacsStudyQuery query, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var path = QidoQuery.Build(query, settings.Limit);
        var summary = $"{{\"accession\":\"{query.AccessionNumber}\",\"patientId\":\"{Masking.MaskIdentifier(query.PatientId)}\",\"from\":\"{query.From}\",\"to\":\"{query.To}\",\"modality\":\"{query.Modality}\"}}";
        try
        {
            var (status, body) = await GetAsync(path, settings, secrets, cancellationToken);
            if (status is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
            {
                var error = $"The PACS returned HTTP {(int)status}.";
                await trace.LogAsync(IntegrationNames.Pacs, Key, "qido.studies", false, started, query.AccessionNumber, summary, error: error);
                throw new IntegrationException(error);
            }

            var studies = status == HttpStatusCode.NoContent ? [] : DicomJson.ParseStudies(body, LabTime.ResolveZone(settings.TimeZone));
            await trace.LogAsync(IntegrationNames.Pacs, Key, "qido.studies", true, started, query.AccessionNumber, summary,
                string.Create(CultureInfo.InvariantCulture, $"{studies.Count} stud{(studies.Count == 1 ? "y" : "ies")}"));
            return studies;
        }
        catch (Exception ex) when (ex is not IntegrationException && IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "The PACS");
            await trace.LogAsync(IntegrationNames.Pacs, Key, "qido.studies", false, started, query.AccessionNumber, summary, error: error);
            throw new IntegrationException(error, ex);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string relative, PacsSettings settings, PacsSecrets secrets, CancellationToken cancellationToken)
    {
        if (!IntegrationHttp.TryCreateEndpoint(settings.QidoUrl, requireHttps: false, out var baseUri))
        {
            throw new IntegrationException("The PACS DICOMweb URL is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, IntegrationHttp.Combine(baseUri, relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dicom+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
        switch (settings.AuthType.Trim().ToLowerInvariant())
        {
            case "basic" when !string.IsNullOrWhiteSpace(settings.UserName):
                IntegrationHttp.SetBasicAuth(request, settings.UserName, secrets.Password ?? string.Empty);
                break;
            case "bearer" when !string.IsNullOrWhiteSpace(secrets.BearerToken):
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.BearerToken);
                break;
        }

        using var response = await http.SendAsync(request, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
