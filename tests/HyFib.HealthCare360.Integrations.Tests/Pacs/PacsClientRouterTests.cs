using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Pacs;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs;

/// <summary>
/// The router is what Diagnostics resolves as <see cref="IPacsClient"/>. Diagnostics treats
/// <see cref="HttpRequestException"/> as "PACS unavailable", so every failure must arrive as one, never as a server error.
/// </summary>
public sealed class PacsClientRouterTests
{
    private static readonly ImagingStudyInfo Study = new("1.2.840.113619.2.55.3.1", "ACC-1", "UHID00012345", "SHARMA RAVI", null, "CT", "CT BRAIN", 3, 245);

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IPacsProvider _dicomWeb = Substitute.For<IPacsProvider>();

    public PacsClientRouterTests()
    {
        _dicomWeb.Key.Returns("dicomweb");
        _settings.GetAsync<PacsSecrets>("integrations.pacs.secret", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new PacsSecrets());
        Configure(new PacsSettings { Enabled = true, Provider = "dicomweb", QidoUrl = "http://orthanc:8042/dicom-web" });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PacsClientRouter Router =>
        new([_dicomWeb], new IntegrationSettingsReader(_settings, Substitute.For<ICurrentUser>(), NullLogger<IntegrationSettingsReader>.Instance), TimeProvider.System);

    private void Configure(PacsSettings? settings) =>
        _settings.GetAsync<PacsSettings>("integrations.pacs", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);

    [Fact]
    public async Task Delegates_study_searches_to_the_configured_provider()
    {
        _dicomWeb.FindStudiesAsync(default!, default!, default!, Ct).ReturnsForAnyArgs([Study]);

        var studies = await Router.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "ACC-1"), Ct);

        studies.ShouldHaveSingleItem().ShouldBe(Study);
    }

    [Theory]
    [InlineData(false, "PACS is disabled.")]
    [InlineData(null, "PACS is not configured. Set it up in Administration → Integrations.")]
    public async Task An_unusable_pacs_is_reported_as_unavailable(bool? enabled, string message)
    {
        Configure(enabled is null ? null : new PacsSettings { Enabled = enabled.Value, Provider = "dicomweb" });

        var failure = await Should.ThrowAsync<HttpRequestException>(() => Router.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "ACC-1"), Ct));

        failure.Message.ShouldBe(message);
        (await Router.PingAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Provider_failures_surface_as_http_request_exceptions()
    {
        _dicomWeb.FindStudiesAsync(default!, default!, default!, Ct).ReturnsForAnyArgs<IReadOnlyList<ImagingStudyInfo>>(_ => throw new IntegrationException("The PACS returned HTTP 500."));

        var failure = await Should.ThrowAsync<HttpRequestException>(() => Router.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: "ACC-1"), Ct));

        failure.Message.ShouldBe("The PACS returned HTTP 500.");
        failure.InnerException.ShouldBeOfType<IntegrationException>();
    }

    [Fact]
    public void Viewer_links_work_even_while_the_integration_is_disabled()
    {
        Configure(new PacsSettings { Enabled = false, Provider = "dicomweb", ViewerUrlTemplate = "https://pacs.local/ohif/viewer?StudyInstanceUIDs={studyUid}" });

        Router.BuildViewerUrl("1.2.3", null).ShouldBe("https://pacs.local/ohif/viewer?StudyInstanceUIDs=1.2.3");
    }

    [Fact]
    public void No_viewer_template_means_no_link()
    {
        Configure(null);

        Router.BuildViewerUrl("1.2.3", "ACC-1").ShouldBeNull();
    }

    [Fact]
    public async Task Reads_the_pacs_settings_of_the_facility_it_is_given()
    {
        // Background linking has no signed-in facility: each call names the study's facility.
        var facility = Guid.NewGuid();
        _dicomWeb.FindSeriesAsync(default!, default!, default!, Ct).ReturnsForAnyArgs([new ImagingSeriesInfo("1.2.3.1", "CT", 1, null, null, 2)]);

        var series = await Router.FindSeriesAsync("1.2.3", facility, Ct);

        series.ShouldHaveSingleItem();
        await _settings.Received().GetAsync<PacsSettings>("integrations.pacs", facility, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("1.2.3/../../patients")]
    [InlineData("")]
    public async Task Refuses_values_that_are_not_dicom_uids_before_a_provider_sees_them(string uid)
    {
        await Should.ThrowAsync<HttpRequestException>(() => Router.FindInstancesAsync("1.2.3", uid, null, Ct));
        await Should.ThrowAsync<HttpRequestException>(() =>
            Router.RetrieveImageAsync(new PacsImageRequest("1.2.3", "1.2.3.1", uid, null, PacsImageKind.Rendered, 512), Ct));

        _dicomWeb.ReceivedCalls().Where(c => c.GetMethodInfo().Name != "get_Key").ShouldBeEmpty();
    }

    [Fact]
    public async Task Images_the_pacs_will_not_give_surface_as_http_request_exceptions()
    {
        _dicomWeb.RetrieveImageAsync(default!, default!, default!, Ct)
            .ReturnsForAnyArgs<PacsImage>(_ => throw new IntegrationException("The PACS sent an image larger than 16 MB, which HC360 does not pass on."));

        var failure = await Should.ThrowAsync<HttpRequestException>(() =>
            Router.RetrieveImageAsync(new PacsImageRequest("1.2.3", "1.2.3.1", "1.2.3.1.1", null, PacsImageKind.Rendered, 512), Ct));

        failure.Message.ShouldContain("larger than 16 MB");
    }

    [Fact]
    public async Task Sends_each_dicom_file_filed_under_the_study_and_gives_one_outcome_per_file()
    {
        DicomObjectToStore? sent = null;
        _dicomWeb.StoreAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(call =>
        {
            sent = call.Arg<DicomObjectToStore>();
            return new PacsStoreOutcome(sent.Reference, PacsStoreStatus.Stored, sent.SopInstanceUid, null);
        });

        var outcomes = await Router.StoreAsync(
            new PacsStoreRequest(DicomStowTests.Study, [new PacsStoreFile("echo", DicomStowTests.EchoFile()), new PacsStoreFile("pdf", "%PDF-1.7"u8.ToArray())]), Ct);

        outcomes.Select(o => (o.Reference, o.Status)).ShouldBe([("echo", PacsStoreStatus.Stored), ("pdf", PacsStoreStatus.NotDicom)]);
        outcomes[1].Message.ShouldBe("HC360 could not read this file as DICOM.");
        await _dicomWeb.ReceivedWithAnyArgs(1).StoreAsync(default!, default!, default!, Ct);
        var dataset = FellowOakDicom.DicomFile.Open(new MemoryStream(sent.ShouldNotBeNull().Content)).Dataset;
        dataset.GetString(FellowOakDicom.DicomTag.PatientID).ShouldBe("NH00012345");
        dataset.GetString(FellowOakDicom.DicomTag.StudyInstanceUID).ShouldBe(DicomStowTests.StudyUid);
    }

    [Theory]
    [InlineData("1.2.3/../../patients", "NH00012345")]
    [InlineData("2.25.1", " ")]
    public async Task Sends_nothing_without_a_valid_study_uid_and_patient_id(string studyUid, string patientId)
    {
        var study = DicomStowTests.Study with { StudyInstanceUid = studyUid, PatientId = patientId };

        await Should.ThrowAsync<HttpRequestException>(() => Router.StoreAsync(new PacsStoreRequest(study, [new PacsStoreFile("echo", DicomStowTests.EchoFile())]), Ct));

        await _dicomWeb.DidNotReceiveWithAnyArgs().StoreAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Sending_to_a_disabled_pacs_is_reported_as_unavailable()
    {
        Configure(new PacsSettings { Enabled = false, Provider = "dicomweb" });

        var failure = await Should.ThrowAsync<HttpRequestException>(() =>
            Router.StoreAsync(new PacsStoreRequest(DicomStowTests.Study, [new PacsStoreFile("echo", DicomStowTests.EchoFile())]), Ct));

        failure.Message.ShouldBe("PACS is disabled.");
    }

    [Fact]
    public async Task Names_the_pacs_objects_go_to_by_provider_and_sending_address()
    {
        (await Router.StoreDestinationAsync(null, Ct)).ShouldBe("dicomweb http://orthanc:8042/dicom-web");

        Configure(new PacsSettings { Enabled = true, Provider = "dicomweb", QidoUrl = "http://orthanc:8042/dicom-web", StowUrl = "http://stow.local/rs/ " });
        (await Router.StoreDestinationAsync(null, Ct)).ShouldBe("dicomweb http://stow.local/rs");

        Configure(new PacsSettings { Enabled = false, Provider = "dicomweb" });
        (await Should.ThrowAsync<HttpRequestException>(() => Router.StoreDestinationAsync(null, Ct))).Message.ShouldBe("PACS is disabled.");
    }
}
