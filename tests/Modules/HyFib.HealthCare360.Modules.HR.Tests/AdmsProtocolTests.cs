using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class AdmsProtocolTests
{
    [Fact]
    public void Handshake_names_the_device_and_asks_for_attendance_logs_only()
    {
        var reply = AdmsProtocol.HandshakeResponse("CQZ7231460088", null);

        reply.ShouldStartWith("GET OPTION FROM: CQZ7231460088\n");
        reply.ShouldContain("ATTLOGStamp=None");
        reply.ShouldContain("OPERLOGStamp=9999");
        reply.ShouldContain("TransFlag=TransData AttLog\n");
        reply.ShouldEndWith("\n");
    }

    [Fact]
    public void Handshake_never_requests_fingerprint_templates_or_photos()
    {
        var reply = AdmsProtocol.HandshakeResponse("SN1", "123");

        reply.ShouldNotContain("EnrollFP");
        reply.ShouldNotContain("UserPic");
        reply.ShouldNotContain("AttPhoto", Case.Sensitive);
    }

    [Fact]
    public void Handshake_echoes_a_valid_stamp_so_the_terminal_resumes()
    {
        AdmsProtocol.HandshakeResponse("SN1", "9123456").ShouldContain("ATTLOGStamp=9123456");
    }

    [Theory]
    [InlineData("12a")]
    [InlineData("1;2")]
    [InlineData("")]
    [InlineData("123456789012345678901")]
    public void Handshake_ignores_an_invalid_stamp(string stamp) =>
        AdmsProtocol.HandshakeResponse("SN1", stamp).ShouldContain("ATTLOGStamp=None");

    [Theory]
    [InlineData(3, "OK: 3")]
    [InlineData(0, "OK: 0")]
    [InlineData(-5, "OK: 0")]
    public void Upload_reply_reports_the_accepted_count(int count, string expected) =>
        AdmsProtocol.UploadAccepted(count).ShouldBe(expected);

    [Theory]
    [InlineData("CQZ7231460088", true)]
    [InlineData("AB-12_3", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("A B", false)]
    [InlineData("SN1;DROP", false)]
    [InlineData(null, false)]
    public void Validates_serial_numbers(string? serial, bool expected) =>
        AdmsProtocol.IsValidSerial(serial).ShouldBe(expected);

    [Fact]
    public void Rejects_serials_longer_than_the_column() =>
        AdmsProtocol.IsValidSerial(new string('A', AdmsProtocol.MaxSerialLength + 1)).ShouldBeFalse();
}
