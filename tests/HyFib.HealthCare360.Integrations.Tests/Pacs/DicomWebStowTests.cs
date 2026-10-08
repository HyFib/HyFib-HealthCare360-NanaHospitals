using System.Net;
using System.Text;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs;

/// <summary>STOW-RS against a fake DICOMweb PACS: where and how an object is sent, and what is logged.</summary>
public sealed class DicomWebStowTests
{
    private static readonly PacsSettings Settings = new() { Enabled = true, Provider = "dicomweb", QidoUrl = "http://orthanc:8042/dicom-web" };

    private readonly FakeHttpHandler _http = new();
    private readonly Tracing _tracing = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DicomWebPacsProvider Provider => new(_http.Client(), new PacsStowHttp(_http.Client()), _tracing.Trace);

    private static DicomObjectToStore Item =>
        DicomStow.Prepare(new PacsStoreFile("file-1", DicomStowTests.EchoFile()), DicomStowTests.Study, DateTimeOffset.UnixEpoch, out _)!;

    [Fact]
    public async Task Posts_the_object_to_the_studies_resource_as_multipart_related()
    {
        var item = Item;
        _http.Respond(HttpStatusCode.OK, "{}", "application/dicom+json");

        var outcome = await Provider.StoreAsync(item, Settings, new PacsSecrets(), Ct);

        outcome.Status.ShouldBe(PacsStoreStatus.Stored);
        var request = _http.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsoluteUri.ShouldBe("http://orthanc:8042/dicom-web/studies");
        request.ContentTypeHeader.ShouldNotBeNull().ShouldStartWith("multipart/related");
        request.ContentTypeHeader.ShouldContain("type=\"application/dicom\"");
        request.Header("Accept").ShouldStartWith("application/dicom+json");
        Contains(request.BodyBytes!, item.Content).ShouldBeTrue("the prepared file goes as it is");
    }

    [Fact]
    public async Task Uses_the_stow_address_when_the_pacs_takes_objects_elsewhere_and_signs_in()
    {
        _http.Respond(HttpStatusCode.OK, string.Empty);

        await Provider.StoreAsync(Item, Settings with { StowUrl = "http://dcm4chee:8080/dcm4chee-arc/aets/DCM4CHEE/rs", AuthType = "basic", UserName = "hc360" },
            new PacsSecrets { Password = "pacs-pass" }, Ct);

        var request = _http.Requests.ShouldHaveSingleItem();
        request.Uri.AbsoluteUri.ShouldBe("http://dcm4chee:8080/dcm4chee-arc/aets/DCM4CHEE/rs/studies");
        request.Header("Authorization").ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hc360:pacs-pass")));
    }

    [Fact]
    public async Task Logs_the_object_by_its_uid_and_size_without_the_patients_details()
    {
        _http.Respond(HttpStatusCode.OK, string.Empty);

        await Provider.StoreAsync(Item, Settings, new PacsSecrets(), Ct);

        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Operation.ShouldBe("stow");
        entry.Success.ShouldBeTrue();
        entry.Reference.ShouldBe(DicomStowTests.SopInstanceUid);
        _tracing.AllText.ShouldNotContain("SHARMA");
        _tracing.AllText.ShouldNotContain("NH00012345");
    }

    [Fact]
    public async Task A_pacs_that_cannot_be_reached_gives_a_failed_outcome_not_an_exception()
    {
        _http.Throw(new HttpRequestException("Connection refused"));

        var outcome = await Provider.StoreAsync(Item, Settings, new PacsSecrets(), Ct);

        outcome.Status.ShouldBe(PacsStoreStatus.Failed);
        outcome.Message.ShouldNotBeNullOrWhiteSpace();
        _tracing.Entries.ShouldHaveSingleItem().Success.ShouldBeFalse();
    }

    [Fact]
    public async Task A_refusal_is_logged_with_the_pacss_reason()
    {
        _http.Respond(HttpStatusCode.Conflict, $$$"""{"00081198":{"vr":"SQ","Value":[{"00081155":{"vr":"UI","Value":["{{{DicomStowTests.SopInstanceUid}}}"]},"00081197":{"vr":"US","Value":[42752]}}]}}""",
            "application/dicom+json");

        var outcome = await Provider.StoreAsync(Item, Settings, new PacsSecrets(), Ct);

        outcome.Status.ShouldBe(PacsStoreStatus.Refused);
        outcome.Message.ShouldBe("The PACS refused the object: it is out of storage space or other resources.");
        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Success.ShouldBeFalse();
        entry.Error.ShouldBe(outcome.Message);
    }

    [Fact]
    public async Task Without_an_address_nothing_is_sent()
    {
        var outcome = await Provider.StoreAsync(Item, Settings with { QidoUrl = null }, new PacsSecrets(), Ct);

        outcome.Status.ShouldBe(PacsStoreStatus.Failed);
        outcome.Message.ShouldBe("The PACS DICOMweb URL is not configured.");
        _http.Requests.ShouldBeEmpty();
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
