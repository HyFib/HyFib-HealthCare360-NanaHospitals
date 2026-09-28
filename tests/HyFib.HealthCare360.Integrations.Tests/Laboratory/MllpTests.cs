using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory.Hl7;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class MllpTests
{
    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private static IEnumerable<string> Texts(IReadOnlyList<byte[]> messages) => messages.Select(m => Encoding.ASCII.GetString(m));

    [Fact]
    public void Frames_a_payload_with_start_and_end_blocks() =>
        Mllp.Frame(Bytes("MSH")).ShouldBe(new byte[] { 0x0B, (byte)'M', (byte)'S', (byte)'H', 0x1C, 0x0D });

    [Fact]
    public void Decodes_a_complete_frame()
    {
        var reader = new MllpFrameReader(1024);

        Texts(reader.Append(Mllp.Frame(Bytes("MSH|1")))).ShouldBe(["MSH|1"]);
        reader.Overflowed.ShouldBeFalse();
    }

    [Fact]
    public void Reassembles_frames_split_across_reads_including_the_end_marker()
    {
        var reader = new MllpFrameReader(1024);
        var framed = Mllp.Frame(Bytes("MSH|split"));

        reader.Append(framed.AsSpan(0, 4)).ShouldBeEmpty();
        reader.Append(framed.AsSpan(4, framed.Length - 5)).ShouldBeEmpty();
        Texts(reader.Append(framed.AsSpan(framed.Length - 1))).ShouldBe(["MSH|split"]);
    }

    [Fact]
    public void Decodes_several_frames_from_one_read_and_ignores_bytes_between_them()
    {
        var reader = new MllpFrameReader(1024);
        byte[] data = [.. Bytes("noise"), .. Mllp.Frame(Bytes("A")), 0x0D, 0x0A, .. Mllp.Frame(Bytes("B"))];

        Texts(reader.Append(data)).ShouldBe(["A", "B"]);
    }

    [Fact]
    public void Keeps_a_lone_end_block_byte_inside_the_payload()
    {
        var reader = new MllpFrameReader(1024);
        byte[] data = [0x0B, (byte)'A', 0x1C, (byte)'B', 0x1C, 0x0D];

        reader.Append(data).ShouldHaveSingleItem().ShouldBe(new byte[] { (byte)'A', 0x1C, (byte)'B' });
    }

    [Fact]
    public void A_new_start_block_abandons_an_unterminated_frame()
    {
        var reader = new MllpFrameReader(1024);
        byte[] data = [0x0B, .. Bytes("OLD"), 0x0B, .. Bytes("NEW"), 0x1C, 0x0D];

        Texts(reader.Append(data)).ShouldBe(["NEW"]);
    }

    [Fact]
    public void Flags_frames_larger_than_the_limit_instead_of_buffering_them()
    {
        var reader = new MllpFrameReader(10);

        reader.Append(Mllp.Frame(Bytes("0123456789ABC"))).ShouldBeEmpty();

        reader.Overflowed.ShouldBeTrue();
    }

    [Fact]
    public void A_frame_exactly_at_the_limit_is_accepted() =>
        new MllpFrameReader(10).Append(Mllp.Frame(Bytes("0123456789"))).ShouldHaveSingleItem().Length.ShouldBe(10);
}
