using System.Buffers.Binary;
using HyFib.HealthCare360.Platform.Files;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>The file store knows a file by its content, never by its name: here, ECG machines' SCP-ECG (CAR-01..04).</summary>
public sealed class StoredFileTypeTests
{
    [Fact]
    public void Takes_an_scp_ecg_record() =>
        EncryptedFileStore.SniffContentType(ScpEcg(marked: true)).ShouldBe(EncryptedFileStore.ScpEcgContentType);

    [Fact]
    public void Takes_an_scp_ecg_record_without_the_text_in_section_0_when_its_length_and_crc_agree() =>
        EncryptedFileStore.SniffContentType(ScpEcg(marked: false)).ShouldBe(EncryptedFileStore.ScpEcgContentType);

    [Fact]
    public void Refuses_a_record_whose_crc_or_length_does_not_agree()
    {
        var damaged = ScpEcg(marked: false);
        damaged[^1] ^= 0xFF;
        EncryptedFileStore.SniffContentType(damaged).ShouldBeNull();

        EncryptedFileStore.SniffContentType(ScpEcg(marked: true)[..^4]).ShouldBeNull();
    }

    [Fact]
    public void Computes_the_crc_ccitt_that_scp_ecg_uses() =>
        EncryptedFileStore.Crc16Ccitt("123456789"u8).ShouldBe((ushort)0x29B1);

    /// <summary>
    /// The start of an SCP-ECG record as the store reads it: the record's CRC and length, then section 0's header with
    /// its section number, length, versions and, when <paramref name="marked"/>, "SCPECG" in its reserved bytes.
    /// </summary>
    private static byte[] ScpEcg(bool marked)
    {
        var record = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(2), (uint)record.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(10), (uint)record.Length - 6);
        record[14] = 20;
        record[15] = 20;
        if (marked)
        {
            "SCPECG"u8.CopyTo(record.AsSpan(16));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(record, EncryptedFileStore.Crc16Ccitt(record.AsSpan(2)));
        return record;
    }
}
