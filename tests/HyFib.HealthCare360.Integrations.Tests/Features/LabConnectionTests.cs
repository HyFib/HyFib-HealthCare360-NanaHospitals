using HyFib.HealthCare360.Integrations.Features.LabConnections;
using HyFib.HealthCare360.Integrations.Laboratory;

namespace HyFib.HealthCare360.Integrations.Tests.Features;

/// <summary>The analyser connection form's checks, and how stored connections are read and written.</summary>
public sealed class LabConnectionTests
{
    private static readonly SaveLabConnectionRequestValidator Validator = new();

    private static readonly LabSerialPortDto Com3 = new("COM3", 9600, 8, "None", "One", "None", true, true);

    private static SaveLabConnectionRequest Tcp(int? port = 5100, params string[] ips) =>
        new("Tcp", true, port, ips, null, null, null, null, null, 0);

    private static SaveLabConnectionRequest Serial(Guid? agent, LabSerialPortDto? port) =>
        new("Serial", true, null, null, null, null, null, agent, port, 0);

    private static IEnumerable<string> Errors(SaveLabConnectionRequest request) => Validator.Validate(request).Errors.Select(e => e.ErrorMessage);

    [Fact]
    public void Accepts_a_network_and_a_serial_analyser()
    {
        Validator.Validate(Tcp(5100, "10.0.5.21", "fe80::1")).IsValid.ShouldBeTrue();
        Validator.Validate(Serial(Guid.CreateVersion7(), Com3)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_network_analyser_needs_a_port_and_real_addresses()
    {
        Errors(Tcp(port: null)).ShouldContain("Enter the port the analyser connects to.");
        Errors(Tcp(port: 70000)).ShouldContain("A port is a number from 1 to 65535.");
        Errors(Tcp(5100, "10.0.5.300")).ShouldContain("'10.0.5.300' is not an IP address.");
        Errors(Tcp(5100, [.. Enumerable.Range(1, 17).Select(i => $"10.0.5.{i}")])).ShouldContain("List at most 16 addresses.");
    }

    [Fact]
    public void A_serial_analyser_needs_its_agent_and_valid_port_settings()
    {
        Errors(Serial(null, Com3)).ShouldContain("Choose the lab agent on the PC the analyser is cabled to.");
        Errors(Serial(Guid.CreateVersion7(), null)).ShouldContain("Enter the COM port settings.");
        Errors(Serial(Guid.CreateVersion7(), Com3 with { PortName = "/dev/ttyS0" })).ShouldContain("Enter a COM port, for example COM3.");
        Errors(Serial(Guid.CreateVersion7(), Com3 with { BaudRate = 9601 })).ShouldContain(e => e.StartsWith("Choose a baud rate", StringComparison.Ordinal));
        Errors(Serial(Guid.CreateVersion7(), Com3 with { DataBits = 5 })).ShouldContain("Data bits are 7 or 8.");
        Errors(Serial(Guid.CreateVersion7(), Com3 with { Parity = "none" })).ShouldContain("Parity is None, Odd, Even, Mark or Space.");
        Errors(Serial(Guid.CreateVersion7(), Com3 with { StopBits = "None" })).ShouldContain("Stop bits are One, OnePointFive or Two.");
        Errors(Serial(Guid.CreateVersion7(), Com3 with { Handshake = "Hardware" })).ShouldContain(e => e.StartsWith("Handshake is", StringComparison.Ordinal));
    }

    [Fact]
    public void Checks_the_transport_encoding_and_hl7_options()
    {
        Errors(Tcp() with { Transport = "Usb" }).ShouldContain("Choose network (Tcp) or serial (Serial).");
        Errors(Tcp() with { Encoding = "ascii" }).ShouldContain("Encoding is latin1 or utf-8.");
        Errors(Tcp() with { SampleIdFields = ["OBX-3"] }).ShouldContain("A field is written like SPM-2, SAC-3, OBR-3 or OBR-2.");
        Errors(Tcp() with { OrderMessage = "ORU^R01" }).ShouldContain("The order message is OML^O21 or ORM^O01.");
        Validator.Validate(Tcp() with { SampleIdFields = ["SAC-3", "OBR-2"], OrderMessage = "ORM^O01", Encoding = "utf-8" }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Reads_the_stored_list_and_flags_one_that_cannot_be_read()
    {
        LabConnectionService.Parse(null).Instruments.ShouldBeEmpty();
        LabConnectionService.Parse(null).Invalid.ShouldBeFalse();
        LabConnectionService.Parse("null").Instruments.ShouldBeEmpty();
        LabConnectionService.Parse("[ broken").Invalid.ShouldBeTrue();

        var (instruments, invalid) = LabConnectionService.Parse("""[{"code":"C311","protocol":"Astm","port":5200}]""");

        invalid.ShouldBeFalse();
        instruments.ShouldHaveSingleItem().Port.ShouldBe(5200);
    }

    [Fact]
    public void Port_settings_round_trip_as_windows_names_them()
    {
        var dto = Com3 with { PortName = " com4 ", Parity = "Even", StopBits = "OnePointFive", Handshake = "RequestToSend", DataBits = 7, RtsEnable = false };

        var settings = LabConnectionService.FromDto(dto);

        settings.PortName.ShouldBe("COM4");
        settings.Parity.ShouldBe(LabParity.Even);
        settings.StopBits.ShouldBe(LabStopBits.OnePointFive);
        settings.Handshake.ShouldBe(LabHandshake.RequestToSend);
        settings.Validate().ShouldBeNull();
        LabConnectionService.ToDto(settings).ShouldBe(dto with { PortName = "COM4" });
    }
}
