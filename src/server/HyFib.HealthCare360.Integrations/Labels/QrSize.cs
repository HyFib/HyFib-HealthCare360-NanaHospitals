using System.Text;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// How many modules a side a QR code (model 2, error correction level Q) needs for some data. The printer picks the
/// version itself; this assumes byte mode, which needs the most room, so the printed code is never larger than planned.
/// </summary>
internal static class QrSize
{
    public const int MaxBytes = 400;

    /// <summary>Byte capacity of QR versions 1–20 at error-correction level Q (ISO/IEC 18004, byte mode).</summary>
    private static readonly int[] CapacityQ = [11, 20, 32, 46, 60, 74, 86, 108, 130, 151, 177, 203, 241, 258, 292, 322, 364, 394, 442, 482];

    /// <summary>Modules per side of the smallest version that holds the value.</summary>
    /// <exception cref="LabelLayoutException">More than <see cref="MaxBytes"/> bytes.</exception>
    public static int Modules(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes is 0 or > MaxBytes)
        {
            throw new LabelLayoutException($"A QR code on a label holds 1 to {MaxBytes} bytes.");
        }

        var version = Array.FindIndex(CapacityQ, capacity => capacity >= bytes) + 1;
        return 17 + (4 * version);
    }
}
