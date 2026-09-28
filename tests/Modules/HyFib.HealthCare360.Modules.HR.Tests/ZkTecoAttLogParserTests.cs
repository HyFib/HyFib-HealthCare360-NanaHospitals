using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class ZkTecoAttLogParserTests
{
    [Fact]
    public void Parses_tab_separated_attlog_lines()
    {
        var result = ZkTecoAttLogParser.Parse("1001\t2026-09-25 08:58:12\t0\t1\t0\t0\t0\n1002\t2026-09-25 17:31:05\t1\t15\t\t0\t0\n");

        result.RejectedLines.ShouldBeEmpty();
        result.Records.Count.ShouldBe(2);
        result.Records[0].ShouldBe(new AttLogRecord("1001", new DateTime(2026, 9, 25, 8, 58, 12), 0, 1, null));
        result.Records[0].Direction.ShouldBe(PunchDirection.In);
        result.Records[1].Pin.ShouldBe("1002");
        result.Records[1].VerifyMode.ShouldBe(15);
        result.Records[1].Direction.ShouldBe(PunchDirection.Out);
    }

    [Fact]
    public void Keeps_the_terminal_local_time_without_converting_it()
    {
        var record = ZkTecoAttLogParser.Parse("7\t2026-12-31 23:59:59\t0\t1").Records.Single();

        record.LocalTime.ShouldBe(new DateTime(2026, 12, 31, 23, 59, 59));
        record.LocalTime.Kind.ShouldBe(DateTimeKind.Unspecified);
    }

    [Fact]
    public void Accepts_crlf_line_endings_and_skips_blank_lines()
    {
        var result = ZkTecoAttLogParser.Parse("1001\t2026-09-25 08:58:12\t0\t1\r\n\r\n1001\t2026-09-25 17:02:40\t1\t1\r\n");

        result.Records.Count.ShouldBe(2);
        result.RejectedLines.ShouldBeEmpty();
    }

    [Fact]
    public void Accepts_space_padded_lines_from_older_firmware()
    {
        var record = ZkTecoAttLogParser.Parse("1003   2026-09-25 09:00:00   1   15").Records.Single();

        record.Pin.ShouldBe("1003");
        record.LocalTime.ShouldBe(new DateTime(2026, 9, 25, 9, 0, 0));
        record.StatusCode.ShouldBe(1);
        record.VerifyMode.ShouldBe(15);
    }

    [Fact]
    public void Missing_status_defaults_to_check_in_and_a_zero_work_code_is_dropped()
    {
        var record = ZkTecoAttLogParser.Parse("42\t2026-09-25 09:00:00").Records.Single();

        record.StatusCode.ShouldBe(0);
        record.Direction.ShouldBe(PunchDirection.In);
        record.WorkCode.ShouldBeNull();
    }

    [Fact]
    public void Keeps_an_alphanumeric_work_code()
    {
        ZkTecoAttLogParser.Parse("42\t2026-09-25 09:00:00\t4\t1\tOT7").Records.Single().WorkCode.ShouldBe("OT7");
    }

    [Theory]
    [InlineData("10 01\t2026-09-25 09:00:00\t0\t1")]
    [InlineData("1001;\t2026-09-25 09:00:00\t0\t1")]
    [InlineData("1001\t25-09-2026 09:00:00\t0\t1")]
    [InlineData("1001\t2026-02-30 09:00:00\t0\t1")]
    [InlineData("1001")]
    [InlineData("1234567890123456789012345\t2026-09-25 09:00:00\t0\t1")]
    public void Rejects_malformed_lines_without_throwing(string line)
    {
        var result = ZkTecoAttLogParser.Parse(line + "\n1001\t2026-09-25 09:00:00\t0\t1");

        result.Records.Count.ShouldBe(1);
        result.RejectedLines.Count.ShouldBe(1);
    }

    [Fact]
    public void Rejects_overlong_lines()
    {
        ZkTecoAttLogParser.TryParseLine("1001\t2026-09-25 09:00:00\t0\t1\t" + new string('x', 300), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, PunchDirection.In)]
    [InlineData(1, PunchDirection.Out)]
    [InlineData(2, PunchDirection.Out)]
    [InlineData(3, PunchDirection.In)]
    [InlineData(4, PunchDirection.In)]
    [InlineData(5, PunchDirection.Out)]
    [InlineData(9, PunchDirection.Unknown)]
    public void Maps_terminal_status_codes_to_directions(int status, PunchDirection expected) =>
        ZkTecoAttLogParser.DirectionFor(status).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Empty_bodies_yield_nothing(string? body)
    {
        var result = ZkTecoAttLogParser.Parse(body);

        result.Records.ShouldBeEmpty();
        result.RejectedLines.ShouldBeEmpty();
    }
}
