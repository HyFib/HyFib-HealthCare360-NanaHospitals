namespace HyFib.HealthCare360.Modules.Billing.Documents;

/// <summary>
/// Amounts in words on the Indian system (thousand, lakh, crore), as printed on bills: "Rupees One Lakh Twenty Thousand
/// Five Hundred and Fifty Paise Only". The same wording as the staff app's prints (money.ts, amountInWords).
/// </summary>
internal static class AmountInWords
{
    private static readonly string[] Ones =
    [
        "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen",
        "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];

    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    /// <summary>The amount, rounded to the paisa (half away from zero), in words; a negative amount starts with "Minus".</summary>
    public static string Rupees(decimal amount)
    {
        var paise = (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
        var whole = Math.Abs(paise) / 100;
        var part = (int)(Math.Abs(paise) % 100);
        var words = $"Rupees {Words(whole)}{(part > 0 ? $" and {BelowHundred(part)} Paise" : "")} Only";
        return paise < 0 ? $"Minus {words}" : words;
    }

    /// <summary>A whole number in words: "One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight".</summary>
    public static string Words(long value)
    {
        if (value == 0)
        {
            return "Zero";
        }

        var crore = value / 10_000_000;
        var lakh = (int)(value % 10_000_000 / 100_000);
        var thousand = (int)(value % 100_000 / 1_000);
        var rest = (int)(value % 1_000);
        var parts = new List<string>(4);
        if (crore > 0)
        {
            parts.Add($"{Words(crore)} Crore");
        }

        if (lakh > 0)
        {
            parts.Add($"{BelowHundred(lakh)} Lakh");
        }

        if (thousand > 0)
        {
            parts.Add($"{BelowHundred(thousand)} Thousand");
        }

        if (rest > 0)
        {
            parts.Add(BelowThousand(rest));
        }

        return string.Join(" ", parts);
    }

    private static string BelowHundred(int n) => n < 20 ? Ones[n] : n % 10 == 0 ? Tens[n / 10] : $"{Tens[n / 10]} {Ones[n % 10]}";

    private static string BelowThousand(int n)
    {
        var hundreds = n / 100;
        var rest = n % 100;
        if (hundreds == 0)
        {
            return BelowHundred(rest);
        }

        return rest == 0 ? $"{Ones[hundreds]} Hundred" : $"{Ones[hundreds]} Hundred {BelowHundred(rest)}";
    }
}
