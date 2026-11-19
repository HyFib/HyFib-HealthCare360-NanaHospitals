namespace HyFib.HealthCare360.Platform.Numbering;

/// <summary>
/// The Damm check digit (H. Michael Damm, 2004): one digit worked out from the digits before it, through a table of
/// order 10 in which no two neighbouring digits can be swapped without changing the result. It catches every single
/// wrong digit and every swap of two neighbouring digits, the mistakes people make when they copy a number by hand. A
/// number that ends in its check digit runs the table back to 0. The format part {CHK} writes it
/// (<see cref="NumberFormatPartKind.CheckDigit"/>).
/// </summary>
public static class DammCheckDigit
{
    // Damm's quasigroup of order 10: row the digit so far, column the next digit.
    private static readonly byte[,] Table =
    {
        { 0, 3, 1, 7, 5, 9, 8, 6, 4, 2 },
        { 7, 0, 9, 2, 1, 5, 4, 8, 6, 3 },
        { 4, 2, 0, 6, 8, 7, 1, 3, 5, 9 },
        { 1, 7, 5, 0, 9, 8, 3, 4, 2, 6 },
        { 6, 1, 2, 3, 0, 4, 5, 9, 7, 8 },
        { 3, 6, 7, 4, 2, 0, 9, 5, 8, 1 },
        { 5, 8, 6, 9, 7, 2, 0, 1, 3, 4 },
        { 8, 9, 4, 5, 3, 6, 2, 0, 1, 7 },
        { 9, 4, 3, 8, 6, 1, 7, 2, 0, 5 },
        { 2, 5, 8, 1, 4, 3, 6, 7, 9, 0 },
    };

    /// <summary>The check digit of the digits in <paramref name="text"/>, in order; anything else in it is passed over.</summary>
    public static char Of(ReadOnlySpan<char> text) => (char)('0' + Run(text));

    /// <summary>
    /// True when the digits of <paramref name="text"/> end in the check digit of the digits before it: at least two
    /// digits, run through the table to 0. Anything else in it is passed over, as <see cref="Of"/> passes it over.
    /// </summary>
    public static bool Verifies(ReadOnlySpan<char> text)
    {
        var digits = 0;
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
        }

        return digits >= 2 && Run(text) == 0;
    }

    private static int Run(ReadOnlySpan<char> text)
    {
        var interim = 0;
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                interim = Table[interim, c - '0'];
            }
        }

        return interim;
    }
}
