using System.Net;
using System.Text;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs;

public sealed class DicomWebPacsProviderTests
{
    private static readonly PacsSettings Settings = new() { Enabled = true, Provider = "dicomweb", QidoUrl = "http://orthanc:8042/dicom-web", TimeZone = "UTC" };

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DicomWebPacsProvider Provider => new(_http.Client(), _tracing.Trace);

    [Fact]
    public async Task Searches_studies_under_the_dicomweb_root()
    {
        _http.Respond(HttpStatusCode.OK, DicomJsonTests.StudiesJson, "application/dicom+json");

        var studies = await Provider.FindStudiesAsync(new PacsStudyQuery("UHID00012345", "ACC-2026-0042"), Settings, new PacsSecrets(), Ct);

        studies.Count.ShouldBe(2);
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        request.Uri.AbsoluteUri.ShouldStartWith("http://orthanc:8042/dicom-web/studies?AccessionNumber=ACC-2026-0042&PatientID=UHID00012345&");
        request.Header("Accept").ShouldStartWith("application/dicom+json");
        request.Header("Authorization").ShouldBeEmpty();
    }

    [Fact]
    public async Task Never_logs_patient_names_or_full_identifiers()
    {
        _http.Respond(HttpStatusCode.OK, DicomJsonTests.StudiesJson, "application/dicom+json");

        await Provider.FindStudiesAsync(new PacsStudyQuery("UHID00012345", "ACC-2026-0042"), Settings, new PacsSecrets(), Ct);

        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Success.ShouldBeTrue();
        entry.Response.ShouldBe("2 studies");
        _tracing.AllText.ShouldNotContain("SHARMA");
        _tracing.AllText.ShouldNotContain("UHID00012345");
    }

    [Fact]
    public async Task Uses_basic_authentication_when_configured()
    {
        _http.Respond(HttpStatusCode.OK, "[]");

        await Provider.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "ACC-1"), Settings with { AuthType = "Basic", UserName = "hc360" },
            new PacsSecrets { Password = "pacs-pass" }, Ct);

        _http.Requests[0].Header("Authorization").ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hc360:pacs-pass")));
    }

    [Fact]
    public async Task Uses_bearer_tokens_when_configured()
    {
        _http.Respond(HttpStatusCode.OK, "[]");

        await Provider.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "ACC-1"), Settings with { AuthType = "bearer" },
            new PacsSecrets { BearerToken = "token-123" }, Ct);

        _http.Requests[0].Header("Authorization").ShouldBe("Bearer token-123");
    }

    [Fact]
    public async Task No_content_means_no_studies()
    {
        _http.Respond(HttpStatusCode.NoContent, string.Empty);

        (await Provider.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "ACC-1"), Settings, new PacsSecrets(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Server_errors_are_reported_to_the_caller()
    {
        _http.Respond(HttpStatusCode.Unauthorized, "{}");

        var failure = await Should.ThrowAsync<IntegrationException>(() =>
            Provider.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "ACC-1"), Settings, new PacsSecrets(), Ct));

        failure.Message.ShouldBe("The PACS returned HTTP 401.");
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }

    [Fact]
    public async Task Refuses_to_query_without_a_dicomweb_url()
    {
        await Should.ThrowAsync<IntegrationException>(() =>
            Provider.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "ACC-1"), Settings with { QidoUrl = "ftp://pacs" }, new PacsSecrets(), Ct));

        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ping_reports_reachability()
    {
        _http.Respond(HttpStatusCode.OK, "[]").Throw(new HttpRequestException("unreachable"));

        (await Provider.PingAsync(Settings, new PacsSecrets(), Ct)).ShouldBeTrue();
        (await Provider.PingAsync(Settings, new PacsSecrets(), Ct)).ShouldBeFalse();

        _http.Requests[0].Uri.ShouldBe(new Uri("http://orthanc:8042/dicom-web/studies?limit=1"));
        _tracing.Entries[1].Error!.ShouldStartWith("Could not reach The PACS");
    }
}
