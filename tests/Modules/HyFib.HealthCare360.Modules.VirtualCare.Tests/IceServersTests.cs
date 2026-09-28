using System.Text.Json;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class IceServersTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Without_a_setting_the_public_stun_server_is_used()
    {
        var servers = IceServers.Parse(null);

        servers.ShouldHaveSingleItem().Urls.ShouldBe([IceServers.DefaultStun]);
    }

    [Fact]
    public void Turn_servers_keep_their_credentials()
    {
        var servers = IceServers.Parse(Json("""
            [
              { "urls": ["stun:stun.nanahospitals.in:3478"] },
              { "urls": ["turn:turn.nanahospitals.in:3478?transport=udp", "turns:turn.nanahospitals.in:5349"], "username": "hc360", "credential": "s3cret" }
            ]
            """));

        servers.Count.ShouldBe(2);
        servers[1].Urls.Count.ShouldBe(2);
        servers[1].Username.ShouldBe("hc360");
        servers[1].Credential.ShouldBe("s3cret");
    }

    [Fact]
    public void Single_urls_and_plain_strings_are_accepted()
    {
        var servers = IceServers.Parse(Json("""
            [ { "urls": "stun:one.example:3478" }, { "url": "turn:two.example:3478", "Username": "u", "Credential": "p" }, "stun:three.example:3478" ]
            """));

        servers.Select(s => s.Urls[0]).ShouldBe(["stun:one.example:3478", "turn:two.example:3478", "stun:three.example:3478"]);
        servers[1].Username.ShouldBe("u");
    }

    [Fact]
    public void Entries_that_are_not_ice_urls_are_skipped()
    {
        var servers = IceServers.Parse(Json("""
            [ { "urls": ["https://evil.example", "stun:ok.example:3478"] }, "javascript:alert(1)", 42 ]
            """));

        servers.ShouldHaveSingleItem().Urls.ShouldBe(["stun:ok.example:3478"]);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""[ "http://not-ice" ]""")]
    [InlineData("""{ "urls": "stun:x:3478" }""")]
    [InlineData("""  "stun:x:3478"  """)]
    public void Unusable_settings_fall_back_to_the_default(string json) =>
        IceServers.Parse(Json(json)).ShouldBe(IceServers.Default);
}
