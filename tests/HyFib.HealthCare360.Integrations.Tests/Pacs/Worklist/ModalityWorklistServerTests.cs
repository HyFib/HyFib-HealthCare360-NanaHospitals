using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Pacs.Worklist;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs.Worklist;

/// <summary>
/// The modality worklist server running in this process on a free port, answering fo-dicom's own client (as a modality
/// would) from a fake worklist source: echoes, worklist queries, refusals, the audit entry and the masked log line.
/// </summary>
public sealed class ModalityWorklistServerTests : IAsyncDisposable
{
    private static readonly Guid Facility = Guid.NewGuid();
    private static readonly Guid OtherFacility = Guid.NewGuid();

    private readonly Tracing _tracing = new();
    private readonly FakeWorklistSource _source = new();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IWorklistFacilities _facilities = Substitute.For<IWorklistFacilities>();
    private readonly ServiceProvider _services;
    private readonly int _port = FreePort();
    private IDicomServer? _server;

    public ModalityWorklistServerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<PlatformOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_tracing.Logger);
        services.AddScoped<IntegrationTrace>();
        services.AddScoped<IntegrationSettingsReader>();
        services.AddScoped(_ => Substitute.For<ICurrentUser>());
        services.AddScoped<IModalityWorklistSource>(_ => _source);
        services.AddScoped(_ => _audit);
        services.AddScoped(_ => _settings);
        services.AddModalityWorklist();

        // Registered after the module, so these win: no database in a unit test.
        services.AddScoped(_ => _facilities);
        _services = services.BuildServiceProvider();
        _facilities.ActiveAsync(default).ReturnsForAnyArgs([(Facility, "NH"), (OtherFacility, "NHBR")]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ModalityWorklistGateway Gateway => _services.GetRequiredService<ModalityWorklistGateway>();

    private static DateOnly Today => ModalityWorklistGateway.Today(TimeProvider.System);

    public async ValueTask DisposeAsync()
    {
        _server?.Dispose();
        await _services.DisposeAsync();
    }

    private WorklistSettings Settings(string[]? ips = null, int maxAssociations = 4) => new()
    {
        Enabled = true,
        BindAddress = "127.0.0.1",
        Port = _port,
        MaxAssociations = maxAssociations,
        Modalities =
        [
            new WorklistModalitySettings { AeTitle = "CT1", Name = "CT scanner", AllowedIps = ips ?? ["127.0.0.1"], Facility = "NH", Modalities = ["CT"] },
        ],
    };

    private void Start(WorklistSettings settings)
    {
        Gateway.Apply(WorklistConfig.From(settings, [(Facility, "NH"), (OtherFacility, "NHBR")]));
        _server = _services.GetRequiredService<IDicomServerFactory>().Create<ModalityWorklistService>("127.0.0.1", _port);
        Gateway.State.Listening("HC360MWL", _port);
    }

    private IDicomClient Client(string callingAe = "CT1", string calledAe = "HC360MWL")
    {
        var client = _services.GetRequiredService<IDicomClientFactory>().Create("127.0.0.1", _port, false, callingAe, calledAe);
        client.ClientOptions.AssociationRequestTimeoutInMs = 10_000;
        return client;
    }

    private async Task<(List<DicomDataset> Answers, DicomStatus? Status)> FindAsync(DicomCFindRequest request, string callingAe = "CT1")
    {
        var answers = new ConcurrentQueue<DicomDataset>();
        DicomStatus? final = null;
        request.OnResponseReceived = (_, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                answers.Enqueue(response.Dataset);
            }
            else
            {
                final = response.Status;
            }
        };
        var client = Client(callingAe);
        await client.AddRequestAsync(request);
        await client.SendAsync(Ct);
        return ([.. answers], final);
    }

    private static DicomCFindRequest TodaysCtWorklist(string? patientName = null) =>
        DicomCFindRequest.CreateWorklistQuery(patientName: patientName, modality: "CT",
            scheduledDateTime: new DicomDateRange(Today.ToDateTime(TimeOnly.MinValue), Today.ToDateTime(new TimeOnly(23, 59, 59))));

    private void TwoStudiesToday()
    {
        var date = Today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        _source.Items.Add(WorklistMatchingTests.Item(patientName: "SHARMA^RAVI", patientId: "NH26000001", date: date, facility: Facility));
        _source.Items.Add(WorklistMatchingTests.Item(patientName: "VERMA^ANITA", patientId: "NH26000002", accession: "NHRD260000043", date: date, facility: Facility));
        _source.Items.Add(WorklistMatchingTests.Item(patientName: "OTHER^BRANCH", date: date, facility: OtherFacility));
    }

    [Fact]
    public async Task Answers_an_echo_from_an_allowed_modality()
    {
        Start(Settings());
        DicomStatus? status = null;
        var client = Client();

        await client.AddRequestAsync(new DicomCEchoRequest { OnResponseReceived = (_, response) => status = response.Status });
        await client.SendAsync(Ct);

        status.ShouldBe(DicomStatus.Success);
    }

    [Fact]
    public async Task Sends_a_modality_the_worklist_of_its_own_facility_and_kind_of_study()
    {
        Start(Settings());
        TwoStudiesToday();

        var (answers, status) = await FindAsync(TodaysCtWorklist());

        status.ShouldBe(DicomStatus.Success);
        answers.Select(a => a.GetString(DicomTag.PatientName)).ShouldBe(["SHARMA^RAVI", "VERMA^ANITA"], ignoreOrder: true);
        answers.ShouldAllBe(a => a.GetString(DicomTag.StudyInstanceUID) == "2.25.123456789");
        var query = _source.Queries.ShouldHaveSingleItem();
        query.FacilityId.ShouldBe(Facility);
        query.Modalities.ShouldBe(["CT"]);
        query.From.ShouldBe(Today);
        query.To.ShouldBe(Today);
    }

    [Fact]
    public async Task Matches_patient_names_with_wildcards()
    {
        Start(Settings());
        TwoStudiesToday();

        var (answers, _) = await FindAsync(TodaysCtWorklist(patientName: "ver*"));

        answers.ShouldHaveSingleItem().GetString(DicomTag.PatientID).ShouldBe("NH26000002");
    }

    [Fact]
    public async Task Passes_the_name_start_the_id_pattern_and_the_times_to_the_database()
    {
        Start(Settings());
        TwoStudiesToday();
        var request = DicomCFindRequest.CreateWorklistQuery(patientId: "NH26*", patientName: "ver*^an*", modality: "CT",
            scheduledDateTime: new DicomDateRange(
                Today.ToDateTime(new TimeOnly(9, 0)), Today.ToDateTime(new TimeOnly(11, 30))));

        var (answers, _) = await FindAsync(request);

        answers.ShouldHaveSingleItem().GetString(DicomTag.PatientID).ShouldBe("NH26000002");
        var query = _source.Queries.ShouldHaveSingleItem();
        query.PatientNamePrefix.ShouldBe("ver");
        query.PatientId.ShouldBe("NH26*");
        query.FromTime.ShouldBe(new TimeOnly(9, 0));
        query.ToTime.ShouldBe(new TimeOnly(11, 30));
        query.Around.ShouldNotBeNull();
        query.Offset.ShouldBe(0);
    }

    [Fact]
    public async Task Reads_on_until_enough_entries_match_so_none_is_dropped()
    {
        Start(Settings() with { MaxResults = 3 });
        var date = Today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 20; i++)
        {
            _source.Items.Add(WorklistMatchingTests.Item(patientName: "SHARMA^RAVI", date: date, facility: Facility));
        }

        _source.Items.Add(WorklistMatchingTests.Item(
            patientName: "VERMA^ANITA", patientId: "NH26000002", date: date, facility: Facility));
        _source.Items.Add(WorklistMatchingTests.Item(
            patientName: "KUVERMA^ANU", patientId: "NH26000003", date: date, facility: Facility));

        var (answers, _) = await FindAsync(TodaysCtWorklist(patientName: "*VERMA*"));

        answers.Select(a => a.GetString(DicomTag.PatientID)).ShouldBe(["NH26000002", "NH26000003"]);
        _source.Queries.Select(q => q.Offset).ShouldBe([0, 4, 8, 12, 16, 20]);
        _source.Queries.ShouldAllBe(q => q.Limit == 4);
        _tracing.Entries.ShouldHaveSingleItem().Response.ShouldBe("2 entries");
    }

    [Fact]
    public async Task Cuts_the_answer_at_the_limit_and_says_so()
    {
        Start(Settings() with { MaxResults = 3 });
        var date = Today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 10; i++)
        {
            _source.Items.Add(WorklistMatchingTests.Item(date: date, facility: Facility));
        }

        var (answers, _) = await FindAsync(TodaysCtWorklist());

        answers.Count.ShouldBe(3);
        _source.Queries.ShouldHaveSingleItem();
        _tracing.Entries.ShouldHaveSingleItem().Response
            .ShouldBe("3 entries (more entries match; the answer was cut short)");
    }

    [Fact]
    public async Task Reads_past_the_largest_page_when_the_limit_is_the_largest_allowed()
    {
        Start(Settings() with { MaxResults = 1000 });
        var date = Today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 1500; i++)
        {
            _source.Items.Add(WorklistMatchingTests.Item(date: date, facility: Facility));
        }

        var (answers, _) = await FindAsync(TodaysCtWorklist(patientName: "NOBODY"));

        answers.ShouldBeEmpty();
        _source.Queries.Select(q => (q.Offset, q.Limit)).ShouldBe([(0, 1000), (1000, 1000)]);
    }

    [Fact]
    public async Task Writes_one_audit_entry_and_one_masked_log_line_per_query()
    {
        Start(Settings());
        TwoStudiesToday();

        await FindAsync(TodaysCtWorklist(patientName: "SHARMA*"));

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Category == AuditCategories.PhiAccess && e.Action == "worklist.queried" && e.PatientId == null && e.EntityId == "CT1"),
            Arg.Any<CancellationToken>());
        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Operation.ShouldBe("c-find");
        entry.Provider.ShouldBe("dicom:CT1");
        entry.Success.ShouldBeTrue();
        entry.Response.ShouldBe("1 entry");
        entry.Request!.ShouldContain("\"patientName\":\"***\"");
        _tracing.AllText.ShouldNotContain("SHARMA");
        _tracing.AllText.ShouldNotContain("NH26000001");
    }

    [Fact]
    public async Task Sends_nothing_when_the_audit_entry_cannot_be_written()
    {
        Start(Settings());
        TwoStudiesToday();
        _audit.WriteAsync(default!, Ct).ThrowsAsyncForAnyArgs(new InvalidOperationException("database down"));

        var (answers, status) = await FindAsync(TodaysCtWorklist());

        answers.ShouldBeEmpty();
        status!.Code.ShouldBe(DicomStatus.QueryRetrieveUnableToProcess.Code);
    }

    [Fact]
    public async Task A_worklist_that_cannot_be_read_is_reported_without_its_details()
    {
        Start(Settings());
        _source.Failure = new InvalidOperationException("Patient SHARMA^RAVI could not be read");

        var (answers, status) = await FindAsync(TodaysCtWorklist());

        answers.ShouldBeEmpty();
        status!.Code.ShouldBe(DicomStatus.QueryRetrieveUnableToProcess.Code);
        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Success.ShouldBeFalse();
        entry.Error.ShouldBe("The worklist could not be read (InvalidOperationException).");
        _tracing.AllText.ShouldNotContain("SHARMA");
    }

    [Fact]
    public async Task Refuses_a_modality_that_is_not_on_the_list()
    {
        Start(Settings());

        var refused = await Should.ThrowAsync<DicomAssociationRejectedException>(() => FindAsync(TodaysCtWorklist(), callingAe: "XRAY9"));

        refused.RejectReason.ShouldBe(DicomRejectReason.CallingAENotRecognized);
        _source.Queries.ShouldBeEmpty();
        var status = Gateway.State.Snapshot().ShouldHaveSingleItem();
        status.MessagesRefused.ShouldBe(1);
        status.LastError!.ShouldContain("XRAY9");
        _tracing.Entries.ShouldHaveSingleItem().Operation.ShouldBe("associate");
    }

    [Fact]
    public async Task Refuses_calls_to_another_ae_title()
    {
        Start(Settings());
        var client = Client(calledAe: "MAIN_PACS");
        await client.AddRequestAsync(new DicomCEchoRequest());

        var refused = await Should.ThrowAsync<DicomAssociationRejectedException>(() => client.SendAsync(Ct));

        refused.RejectReason.ShouldBe(DicomRejectReason.CalledAENotRecognized);
    }

    [Fact]
    public async Task Refuses_an_allowed_ae_title_calling_from_another_address()
    {
        // This test connects from 127.0.0.1, which CT1 is not allowed to use.
        Start(Settings(ips: ["10.0.5.31"]));

        var refused = await Should.ThrowAsync<DicomAssociationRejectedException>(() => FindAsync(TodaysCtWorklist()));

        refused.RejectReason.ShouldBe(DicomRejectReason.CallingAENotRecognized);
        Gateway.State.Snapshot().ShouldHaveSingleItem().LastError!.ShouldContain("not one of its allowed addresses");
    }

    [Fact]
    public async Task The_servers_own_test_identity_may_echo_but_not_read_the_worklist()
    {
        Start(Settings());
        TwoStudiesToday();
        DicomStatus? echo = null;
        var client = Client(callingAe: "HC360MWL");
        await client.AddRequestAsync(new DicomCEchoRequest { OnResponseReceived = (_, response) => echo = response.Status });
        await client.SendAsync(Ct);

        echo.ShouldBe(DicomStatus.Success);
        try
        {
            await FindAsync(TodaysCtWorklist(), callingAe: "HC360MWL");
        }
        catch (DicomNetworkException)
        {
            // fo-dicom may report that no presentation context was accepted for the worklist.
        }

        _source.Queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_query_reads_the_worklist_of_the_facility_the_modality_belongs_to_now()
    {
        Start(Settings());
        TwoStudiesToday();
        var moved = Settings() with
        {
            Modalities = [Settings().Modalities![0] with { Facility = "NHBR" }],
        };
        var client = Client();
        await client.AddRequestAsync(new DicomCEchoRequest
        {
            OnResponseReceived = (_, _) =>
                Gateway.Apply(WorklistConfig.From(moved, [(Facility, "NH"), (OtherFacility, "NHBR")])),
        });
        var answers = new ConcurrentQueue<DicomDataset>();
        var find = TodaysCtWorklist();
        find.OnResponseReceived = (_, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                answers.Enqueue(response.Dataset);
            }
        };
        await client.AddRequestAsync(find);

        await client.SendAsync(Ct);

        _source.Queries.ShouldHaveSingleItem().FacilityId.ShouldBe(OtherFacility);
        answers.ShouldHaveSingleItem().GetString(DicomTag.PatientName).ShouldBe("OTHER^BRANCH");
    }

    [Fact]
    public async Task A_modality_removed_while_its_association_is_open_reads_nothing_more()
    {
        Start(Settings());
        TwoStudiesToday();
        var removed = Settings() with { Modalities = [] };
        var client = Client();
        await client.AddRequestAsync(new DicomCEchoRequest
        {
            OnResponseReceived = (_, _) => Gateway.Apply(WorklistConfig.From(removed, [(Facility, "NH")])),
        });
        DicomStatus? final = null;
        var answered = 0;
        var find = TodaysCtWorklist();
        find.OnResponseReceived = (_, response) =>
        {
            if (response.Status == DicomStatus.Pending)
            {
                answered++;
            }
            else
            {
                final = response.Status;
            }
        };
        await client.AddRequestAsync(find);

        try
        {
            await client.SendAsync(Ct);
        }
        catch (DicomNetworkException)
        {
            // The server closes the connection after refusing the query.
        }

        answered.ShouldBe(0);
        final.ShouldNotBeNull().Code.ShouldBe(DicomStatus.QueryRetrieveUnableToProcess.Code);
        _source.Queries.ShouldBeEmpty();
        Gateway.State.Snapshot().ShouldHaveSingleItem().LastError!.ShouldContain("no longer allowed");
    }

    [Fact]
    public async Task Logs_refusals_once_per_kind_and_network_and_counts_every_one()
    {
        Start(Settings());

        foreach (var address in new[] { "10.9.8.1", "10.9.8.2", "10.9.8.200" })
        {
            await Gateway.RefuseAsync(WorklistRefusals.UnknownAddress, null, IPAddress.Parse(address),
                $"A connection from {address} was closed.");
        }

        await Gateway.RefuseAsync(WorklistRefusals.UnknownAddress, null, IPAddress.Parse("10.9.9.1"),
            "A connection from 10.9.9.1 was closed.");
        await Gateway.RefuseAsync(WorklistRefusals.CallingAe, "XRAY9", IPAddress.Parse("10.9.8.3"),
            "XRAY9 is not on the list.");

        _tracing.Entries.Select(e => e.Error).ShouldBe([
            "A connection from 10.9.8.1 was closed.",
            "A connection from 10.9.9.1 was closed.",
            "XRAY9 is not on the list.",
        ]);
        Gateway.State.Snapshot().ShouldHaveSingleItem().MessagesRefused.ShouldBe(5);
    }

    [Fact]
    public void Throttles_refusals_for_the_log_interval_and_counts_those_left_out()
    {
        var gateway = Gateway;
        var at = DateTimeOffset.UtcNow;
        var ipv6 = IPAddress.Parse("2001:db8:1:2::10");

        gateway.FirstRefusalInInterval("k", ipv6, at, out _).ShouldBeTrue();
        var sameNetwork = IPAddress.Parse("2001:db8:1:2::99");
        var otherNetwork = IPAddress.Parse("2001:db8:1:3::10");
        var later = at + ModalityWorklistGateway.RefusalLogInterval;

        gateway.FirstRefusalInInterval("k", sameNetwork, at.AddMinutes(1), out _).ShouldBeFalse();
        gateway.FirstRefusalInInterval("k", otherNetwork, at.AddMinutes(1), out _).ShouldBeTrue();
        gateway.FirstRefusalInInterval("k", ipv6, at.AddMinutes(2), out _).ShouldBeFalse();
        gateway.FirstRefusalInInterval("k", ipv6, later, out var suppressed).ShouldBeTrue();
        suppressed.ShouldBe(2);
        ModalityWorklistGateway.Network(IPAddress.Parse("::ffff:192.168.4.77")).ShouldBe("192.168.4.0/24");
        ModalityWorklistGateway.Network(ipv6).ShouldBe("2001:db8:1:2::/64");
    }

    [Fact]
    public void Lets_only_allowed_addresses_and_this_machine_connect()
    {
        Gateway.Apply(WorklistConfig.From(Settings(ips: ["10.0.5.31"]), [(Facility, "NH")]));

        Gateway.Admits(IPAddress.Parse("10.0.5.31"), IPAddress.Parse("10.0.5.2")).ShouldBeTrue();
        Gateway.Admits(IPAddress.Parse("::ffff:10.0.5.31"), IPAddress.Parse("10.0.5.2")).ShouldBeTrue();
        Gateway.Admits(IPAddress.Parse("10.0.5.99"), IPAddress.Parse("10.0.5.2")).ShouldBeFalse();
        Gateway.Admits(IPAddress.Loopback, IPAddress.Loopback).ShouldBeTrue();
        Gateway.Admits(IPAddress.Parse("10.0.5.2"), IPAddress.Parse("10.0.5.2")).ShouldBeTrue();
        Gateway.Admits(null, IPAddress.Loopback).ShouldBeFalse();
    }

    [Fact]
    public void Counts_associations_up_to_the_limit()
    {
        Gateway.Apply(WorklistConfig.From(Settings(maxAssociations: 2), [(Facility, "NH")]));

        Gateway.TryOpen().ShouldBeTrue();
        Gateway.TryOpen().ShouldBeTrue();
        Gateway.TryOpen().ShouldBeFalse();
        Gateway.Close();
        Gateway.TryOpen().ShouldBeTrue();
    }

    [Fact]
    public async Task Serves_from_its_settings_and_passes_its_own_echo_test_until_turned_off()
    {
        var settings = Settings();
        _settings.GetAsync<WorklistSettings>("integrations.mwl", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);
        var hosted = _services.GetServices<IHostedService>().OfType<ModalityWorklistHostedService>().Single();

        await hosted.ReconcileAsync(Ct);
        using var scope = _services.CreateScope();
        var test = await scope.ServiceProvider.GetRequiredService<ModalityWorklistSelfTest>().RunAsync(Ct);

        test.Success.ShouldBeTrue(test.Message);
        test.Message.ShouldContain($"port {_port}");
        Gateway.State.Snapshot().ShouldHaveSingleItem().State.ShouldBe("listening");

        _settings.GetAsync<WorklistSettings>("integrations.mwl", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings with { Enabled = false });
        await hosted.ReconcileAsync(Ct);
        var off = await scope.ServiceProvider.GetRequiredService<ModalityWorklistSelfTest>().RunAsync(Ct);

        Gateway.State.Snapshot().ShouldBeEmpty();
        off.Success.ShouldBeFalse();
        off.Message.ShouldBe("The modality worklist is turned off.");
    }

    [Fact]
    public async Task Reports_a_port_it_cannot_open()
    {
        using var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var settings = Settings() with { Port = ((IPEndPoint)busy.LocalEndpoint).Port };
        _settings.GetAsync<WorklistSettings>("integrations.mwl", Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(settings);
        var hosted = _services.GetServices<IHostedService>().OfType<ModalityWorklistHostedService>().Single();

        await hosted.ReconcileAsync(Ct);

        var status = Gateway.State.Snapshot().ShouldHaveSingleItem();
        status.State.ShouldBe("failed");
        status.LastError!.ShouldContain("could not be opened");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FakeWorklistSource : IModalityWorklistSource
    {
        public List<ModalityWorklistItem> Items { get; } = [];

        public ConcurrentQueue<ModalityWorklistQuery> Queries { get; } = new();

        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<ModalityWorklistItem>> FindAsync(ModalityWorklistQuery query, CancellationToken cancellationToken)
        {
            Queries.Enqueue(query);
            return Failure is { } failure
                ? Task.FromException<IReadOnlyList<ModalityWorklistItem>>(failure)
                : Task.FromResult<IReadOnlyList<ModalityWorklistItem>>(Items
                    .Where(i => i.FacilityId == query.FacilityId)
                    .Skip(query.Offset)
                    .Take(Math.Min(query.Limit, ModalityWorklistQuery.MaxLimit))
                    .ToList());
        }
    }
}
