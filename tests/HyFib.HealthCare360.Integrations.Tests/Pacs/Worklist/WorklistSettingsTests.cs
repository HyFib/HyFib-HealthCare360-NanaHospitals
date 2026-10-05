using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Pacs.Worklist;

namespace HyFib.HealthCare360.Integrations.Tests.Pacs.Worklist;

/// <summary>The worklist settings as checked: what is served, which modalities may ask, and what is reported as a problem.</summary>
public sealed class WorklistSettingsTests
{
    private static readonly Guid Nana = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly (Guid Id, string Code)[] TwoFacilities = [(Nana, "NH"), (Branch, "NHBR")];

    private static WorklistModalitySettings Ct(string ae = "CT1", string? facility = "NH", string[]? ips = null, string[]? modalities = null) =>
        new() { AeTitle = ae, Name = "CT scanner", Facility = facility, AllowedIps = ips ?? ["10.0.5.31"], Modalities = modalities ?? ["CT"] };

    private static WorklistSettings On(params WorklistModalitySettings[] modalities) => new() { Enabled = true, Modalities = modalities };

    [Fact]
    public void Is_off_until_set_up()
    {
        WorklistConfig.From(null, TwoFacilities).ShouldBe(WorklistConfig.Off);
        WorklistConfig.Off.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Reads_the_settings_as_administrators_write_them()
    {
        const string json = """
            {"enabled":true,"port":4104,"aeTitle":"NANA_MWL","characterSet":"ISO_IR 192","daysBack":2,
             "modalities":[{"aeTitle":"DR_ROOM1","name":"DR room 1","allowedIps":["10.0.5.40"],"facility":"nhbr","modalities":["dx","XR"]}]}
            """;

        var config = WorklistConfig.From(JsonSerializer.Deserialize<WorklistSettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)), TwoFacilities);

        config.Problems.ShouldBeEmpty();
        config.Listener.ShouldBe(new WorklistListener("0.0.0.0", 4104, "NANA_MWL", 10));
        config.CharacterSet.ShouldBe("ISO_IR 192");
        config.DaysBack.ShouldBe(2);
        var peer = config.Peers.ShouldHaveSingleItem();
        peer.FacilityId.ShouldBe(Branch);
        peer.Modalities.ShouldBe(["DX", "CR"]);
        peer.Addresses.ShouldBe([IPAddress.Parse("10.0.5.40")]);
    }

    [Fact]
    public void With_one_facility_a_modality_need_not_name_it()
    {
        var config = WorklistConfig.From(On(Ct(facility: null)), [(Nana, "NH")]);

        config.Peers.ShouldHaveSingleItem().FacilityId.ShouldBe(Nana);
    }

    [Theory]
    [InlineData(null, "say which facility")]
    [InlineData("XYZ", "no active facility XYZ")]
    public void A_modality_must_name_a_facility_that_exists(string? facility, string problem)
    {
        var config = WorklistConfig.From(On(Ct(facility: facility)), TwoFacilities);

        config.Peers.ShouldBeEmpty();
        config.Problems.ShouldHaveSingleItem().ShouldContain(problem);
    }

    [Fact]
    public void A_modality_needs_its_ip_address_and_the_kinds_of_study_it_does()
    {
        var config = WorklistConfig.From(On(Ct(ae: "CT1", ips: ["not-an-ip"]), Ct(ae: "MR1", modalities: ["NM", "xa"])), TwoFacilities);

        config.Peers.ShouldBeEmpty();
        config.Problems.Count.ShouldBe(2);
        config.Problems[0].ShouldContain("allowedIps");
        config.Problems[1].ShouldContain("modalities");
    }

    [Fact]
    public void Duplicate_and_borrowed_ae_titles_are_reported()
    {
        var config = WorklistConfig.From(On(Ct("CT1"), Ct("ct1"), Ct("HC360MWL")), TwoFacilities);

        config.Peers.ShouldHaveSingleItem().AeTitle.ShouldBe("CT1");
        config.Problems.ShouldContain(p => p.Contains("listed twice", StringComparison.Ordinal));
        config.Problems.ShouldContain(p => p.Contains("server's own AE title", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", 11112, "0.0.0.0", "AE title")]
    [InlineData("THIS_TITLE_IS_TOO_LONG", 11112, "0.0.0.0", "AE title")]
    [InlineData("HC360MWL", 0, "0.0.0.0", "port")]
    [InlineData("HC360MWL", 11112, "hospital-server", "listen address")]
    public void A_server_that_cannot_listen_is_not_served(string aeTitle, int port, string bindAddress, string problem)
    {
        var config = WorklistConfig.From(new WorklistSettings { Enabled = true, AeTitle = aeTitle, Port = port, BindAddress = bindAddress }, TwoFacilities);

        config.Listener.ShouldBeNull();
        config.Problems.ShouldHaveSingleItem().ShouldContain(problem);
    }

    [Fact]
    public void Limits_are_kept_within_safe_bounds()
    {
        var config = WorklistConfig.From(new WorklistSettings
        {
            Enabled = true,
            MaxAssociations = 1000,
            IdleTimeoutSeconds = 1,
            DaysBack = 365,
            DaysAhead = -4,
            MaxResults = 1_000_000,
            CharacterSet = "GB18030",
        }, TwoFacilities);

        config.Listener!.MaxAssociations.ShouldBe(64);
        config.IdleTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        config.DaysBack.ShouldBe(30);
        config.DaysAhead.ShouldBe(0);
        config.MaxResults.ShouldBe(1000);
        config.CharacterSet.ShouldBe(WorklistConfig.Latin1);
        config.Problems.ShouldHaveSingleItem().ShouldContain("GB18030");
    }

    [Fact]
    public void Finds_modalities_by_ae_title_and_address()
    {
        var config = WorklistConfig.From(On(Ct("CT1", ips: ["10.0.5.31", "::ffff:10.0.5.32"])), TwoFacilities);

        config.Peer("ct1 ").ShouldNotBeNull();
        config.Peer("CT2").ShouldBeNull();
        config.IsKnownAddress(IPAddress.Parse("10.0.5.32")).ShouldBeTrue();
        config.IsKnownAddress(IPAddress.Parse("10.0.5.99")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("CT1", true)]
    [InlineData("CT_ROOM-1.A", true)]
    [InlineData("CT ROOM", true)]
    [InlineData(" CT", false)]
    [InlineData("CT\\1", false)]
    [InlineData("ABCDEFGHIJKLMNOPQ", false)]
    public void Knows_an_ae_title(string value, bool valid) => WorklistConfig.IsAeTitle(value).ShouldBe(valid);
}
