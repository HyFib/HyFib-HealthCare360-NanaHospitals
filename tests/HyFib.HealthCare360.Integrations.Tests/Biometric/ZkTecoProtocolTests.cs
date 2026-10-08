using System.Buffers.Binary;
using System.Text;
using HyFib.HealthCare360.Integrations.Biometric;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Biometric;

/// <summary>
/// The expected bytes were worked out with the open-source Python client of these terminals (pyzk), which is in daily
/// use with real ZKTeco and eSSL terminals.
/// </summary>
public sealed class ZkTecoProtocolTests
{
    [Fact]
    public void The_first_packet_carries_reply_number_zero_and_the_SDK_checksum()
    {
        var packet = ZkTecoProtocol.Packet(ZkTecoProtocol.CmdConnect, 0, ushort.MaxValue - 1, []);

        Convert.ToHexStringLower(packet).ShouldBe("e80317fc00000000");
    }

    [Fact]
    public void Packets_with_data_get_the_SDK_checksum_whether_their_length_is_even_or_odd()
    {
        var even = ZkTecoProtocol.Packet(ZkTecoProtocol.CmdOptionsRead, 0x1234, 7, "~SerialNumber\0"u8);
        var odd = ZkTecoProtocol.Packet(ZkTecoProtocol.CmdPrepareBuffer, 0x1234, 9, ZkTecoProtocol.PrepareBufferRequest(ZkTecoProtocol.CmdAttLogRead));

        Convert.ToHexStringLower(even).ShouldBe("0b00b5a4341208007e53657269616c4e756d62657200");
        Convert.ToHexStringLower(odd).ShouldBe("df05e1da34120a00010d000000000000000000");
    }

    [Fact]
    public void A_frame_announces_its_packet_and_anything_else_is_not_a_frame()
    {
        var frame = ZkTecoProtocol.Frame([1, 2, 3, 4, 5, 6, 7, 8, 9]);

        Convert.ToHexStringLower(frame[..8]).ShouldBe("5050827d09000000");
        ZkTecoProtocol.FrameLength(frame).ShouldBe(9);
        ZkTecoProtocol.FrameLength("SSH-2.0-OpenSSH_9.6\r\n"u8).ShouldBe(-1);
    }

    [Theory]
    [InlineData(123456u, 0x1234, "267f32eb")]
    [InlineData(0u, 0x5A3C, "617d3223")]
    public void The_communication_key_is_scrambled_as_the_SDK_does(uint key, int session, string expected) =>
        Convert.ToHexStringLower(ZkTecoProtocol.CommKey(key, (ushort)session)).ShouldBe(expected);

    [Fact]
    public void Terminal_times_count_31_day_months_from_2000()
    {
        var time = new DateTime(2026, 10, 8, 9, 2, 15);

        ZkTecoProtocol.EncodeTime(time).ShouldBe(860403735u);
        ZkTecoProtocol.DecodeTime(860403735u).ShouldBe(time);
        ZkTecoProtocol.DecodeTime(ZkTecoProtocol.EncodeTime(new DateTime(2099, 12, 31, 23, 59, 59))).ShouldBe(new DateTime(2099, 12, 31, 23, 59, 59));
    }

    [Fact]
    public void A_time_that_is_not_a_date_is_left_unread()
    {
        // 30 February: the encoding has room for 31 days in every month.
        var february30 = (uint)(((26 * 12 * 31) + 31 + 29) * 86_400);

        ZkTecoProtocol.DecodeTime(february30).ShouldBeNull();
    }

    [Fact]
    public void An_option_is_read_up_to_its_terminator()
    {
        ZkTecoProtocol.OptionValue("~SerialNumber=CQZ7232460163\0\0garbage"u8).ShouldBe("CQZ7232460163");
        ZkTecoProtocol.OptionValue("~SerialNumber=\0"u8).ShouldBeNull();
        ZkTecoProtocol.OptionValue([]).ShouldBeNull();
    }

    [Theory]
    [InlineData(400, 10, 40)]
    [InlineData(160, 10, 16)]
    [InlineData(80, 10, 8)]
    [InlineData(490, 10, 49)]
    [InlineData(480, 10, 40)] // two punches were made between counting and reading
    [InlineData(192, 10, 16)] // the same with 16-byte records
    [InlineData(400, 0, 40)]
    [InlineData(333, 10, 0)]
    public void The_record_length_follows_the_count_and_allows_for_new_punches(int total, int count, int expected) =>
        ZkTecoProtocol.RecordLength(total, count).ShouldBe(expected);

    [Fact]
    public void Reads_40_byte_records_and_counts_the_unreadable()
    {
        var records = Concat(
            FakeZkTecoTerminal.LongRecord(1, "1001", new DateTime(2026, 10, 8, 9, 2, 15), punchState: 0, verify: 1),
            FakeZkTecoTerminal.LongRecord(2, "EMP-7", new DateTime(2026, 10, 8, 18, 1, 0), punchState: 1, verify: 15),
            UnreadableRecord());

        var read = ZkTecoProtocol.AttendanceRecords(WithLength(records), 3, out var unreadable);

        read.ShouldBe([
            new TerminalPunchRecord("1001", new DateTime(2026, 10, 8, 9, 2, 15), 0, 1, null),
            new TerminalPunchRecord("EMP-7", new DateTime(2026, 10, 8, 18, 1, 0), 1, 15, null),
        ]);
        unreadable.ShouldBe(1);
    }

    [Fact]
    public void Reads_16_byte_records_of_older_firmware_with_their_work_code()
    {
        var records = Concat(
            FakeZkTecoTerminal.ShortRecord(1001, new DateTime(2026, 10, 8, 9, 0, 0), punchState: 0, workCode: 7),
            FakeZkTecoTerminal.ShortRecord(1002, new DateTime(2026, 10, 8, 9, 5, 0), punchState: 255));

        var read = ZkTecoProtocol.AttendanceRecords(WithLength(records), 2, out _);

        read.ShouldBe([
            new TerminalPunchRecord("1001", new DateTime(2026, 10, 8, 9, 0, 0), 0, 1, "7"),
            new TerminalPunchRecord("1002", new DateTime(2026, 10, 8, 9, 5, 0), 255, 1, null),
        ]);
    }

    [Fact]
    public void Refuses_the_oldest_8_byte_records_with_what_to_do_instead()
    {
        var error = Should.Throw<ZkTecoException>(() => ZkTecoProtocol.AttendanceRecords(WithLength(new byte[80]), 10, out _));

        error.Message.ShouldContain("ADMS");
    }

    [Fact]
    public void An_empty_buffer_holds_no_records()
    {
        ZkTecoProtocol.AttendanceRecords([], 0, out _).ShouldBeEmpty();
        ZkTecoProtocol.AttendanceRecords(WithLength([]), 0, out _).ShouldBeEmpty();
    }

    private static byte[] UnreadableRecord()
    {
        var record = FakeZkTecoTerminal.LongRecord(3, "1003", new DateTime(2026, 10, 8, 10, 0, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(27), (uint)(((26 * 12 * 31) + 31 + 29) * 86_400));
        return record;
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static byte[] WithLength(byte[] records)
    {
        var buffer = new byte[4 + records.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)records.Length);
        records.CopyTo(buffer, 4);
        return buffer;
    }

    [Fact]
    public void The_record_helpers_write_the_pin_as_text()
    {
        var record = FakeZkTecoTerminal.LongRecord(9, "42", DateTime.UnixEpoch);

        Encoding.ASCII.GetString(record, 2, 2).ShouldBe("42");
        record[4].ShouldBe((byte)0);
    }
}
