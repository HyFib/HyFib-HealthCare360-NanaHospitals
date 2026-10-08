namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>
/// The barcode on a pharmacy tag (INT-BAR): the item's code and the batch number, joined by <c>#</c>, as in
/// <c>PCM500#AB1234</c>. Item codes never hold a <c>#</c>, so the value splits unambiguously, and scanning it tells the
/// counter which batch is in hand rather than leaving it to first-expiry-first-out. A tag for an item without a batch
/// carries the item code alone, which every item search already finds.
/// </summary>
public static class BatchTag
{
    public const char Separator = '#';

    /// <summary>The item code and batch number in a scanned tag, or false when the text is not a batch tag.</summary>
    public static bool TryParse(string? text, out string itemCode, out string batchNumber)
    {
        itemCode = string.Empty;
        batchNumber = string.Empty;
        var value = text?.Trim();
        var at = value?.IndexOf(Separator, StringComparison.Ordinal) ?? -1;
        if (value is null || at <= 0 || at == value.Length - 1)
        {
            return false;
        }

        var code = value[..at].Trim().ToUpperInvariant();
        var batch = value[(at + 1)..].Trim();
        if (code.Length > 32 || batch.Length is 0 or > 40 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/'))
        {
            return false;
        }

        itemCode = code;
        batchNumber = batch;
        return true;
    }

    /// <summary>Whether a stored batch number is the one a tag names: the same text, whatever the case.</summary>
    public static bool Matches(string storedBatchNumber, string taggedBatchNumber) =>
        string.Equals(storedBatchNumber.Trim(), taggedBatchNumber.Trim(), StringComparison.OrdinalIgnoreCase);
}
