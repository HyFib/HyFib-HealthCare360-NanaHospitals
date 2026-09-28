using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class AstmFramesTests
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    [Fact]
    public void Checksum_is_the_byte_sum_from_frame_number_through_etx_modulo_256()
    {
        // '1' + "H|\^&" + CR + ETX = 485 = 0x1E5 -> E5
        byte[] content = [(byte)'1', .. Encoding.ASCII.GetBytes(@"H|\^&"), AstmControl.Cr, AstmControl.Etx];

        AstmFrames.Checksum(content).ShouldBe("E5");
    }

    [Fact]
    public void Builds_a_complete_frame()
    {
        var frame = AstmFrames.Build(1, "H|\\^&\r", isFinal: true, Latin1);

        frame.ShouldBe(new byte[] { AstmControl.Stx, (byte)'1', (byte)'H', (byte)'|', (byte)'\\', (byte)'^', (byte)'&', AstmControl.Cr, AstmControl.Etx, (byte)'E', (byte)'5', AstmControl.Cr, AstmControl.Lf });
    }

    [Fact]
    public void Intermediate_frames_end_with_etb()
    {
        var frame = AstmFrames.Build(3, "R|1|^^^GLU", isFinal: false, Latin1);

        frame[^5].ShouldBe(AstmControl.Etb);
        AstmFrames.TryParse(frame, Latin1, out var parsed, out _).ShouldBeTrue();
        parsed.ShouldBe(new AstmFrame(3, "R|1|^^^GLU", false));
    }

    [Fact]
    public void Parses_a_valid_frame_and_accepts_lowercase_checksums()
    {
        var frame = AstmFrames.Build(1, "H|\\^&\r", true, Latin1);
        frame[^4] = (byte)'e';

        AstmFrames.TryParse(frame, Latin1, out var parsed, out var error).ShouldBeTrue(error);

        parsed.ShouldBe(new AstmFrame(1, "H|\\^&\r", true));
    }

    [Fact]
    public void Rejects_a_checksum_mismatch()
    {
        var frame = AstmFrames.Build(1, "H|\\^&\r", true, Latin1);
        frame[3] = (byte)'X';

        AstmFrames.TryParse(frame, Latin1, out _, out var error).ShouldBeFalse();

        error!.ShouldStartWith("checksum mismatch");
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("no-terminator")]
    [InlineData("bad-number")]
    public void Rejects_malformed_frames(string defect)
    {
        var frame = AstmFrames.Build(1, "L|1|N\r", true, Latin1);
        switch (defect)
        {
            case "truncated":
                frame = frame[..^1];
                break;
            case "no-terminator":
                frame[^5] = (byte)'X';
                break;
            case "bad-number":
                frame[1] = (byte)'9';
                break;
        }

        AstmFrames.TryParse(frame, Latin1, out _, out var error).ShouldBeFalse();

        error.ShouldBe(defect switch
        {
            "truncated" => "malformed frame",
            "no-terminator" => "missing ETX/ETB",
            _ => "invalid frame number",
        });
    }

    [Fact]
    public void Builds_one_frame_per_record_with_rolling_frame_numbers()
    {
        var records = Enumerable.Range(1, 9).Select(i => $"R|{i}|^^^T{i}|1").ToList();

        var frames = AstmFrames.BuildMessage(records, Latin1);

        frames.Count.ShouldBe(9);
        frames.Select(f => f[1] - '0').ShouldBe([1, 2, 3, 4, 5, 6, 7, 0, 1]);
        frames.ShouldAllBe(f => f[f.Length - 5] == AstmControl.Etx);
    }

    [Fact]
    public void Splits_long_records_into_etb_frames_of_at_most_240_characters()
    {
        var record = "C|1|I|" + new string('x', 594);

        var frames = AstmFrames.BuildMessage([record], Latin1);

        frames.Count.ShouldBe(3);
        var parsed = frames.Select(f =>
        {
            AstmFrames.TryParse(f, Latin1, out var frame, out _).ShouldBeTrue();
            return frame;
        }).ToList();
        parsed.Select(p => p.Text.Length).ShouldBe([240, 240, 121]);
        parsed.Select(p => p.IsFinal).ShouldBe([false, false, true]);
        string.Concat(parsed.Select(p => p.Text)).ShouldBe(record + "\r");
    }
}
