namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// An IRN recorded by hand, checked with the connected portal (GST rule 48(4)): the portal is asked only within the time it
/// still gives an IRN's details (<see cref="IrpIrn.DetailsWindow"/>), and the IRN is recorded only when the portal holds it,
/// active, under the acknowledgement given, with a signed QR code naming the document.
/// </summary>
public static class IrnVerification
{
    /// <summary>Whether the portal is asked about an IRN generated at <paramref name="generatedAt"/>: it no longer answers for older ones.</summary>
    public static bool AsksPortal(DateTimeOffset generatedAt, DateTimeOffset now) => now - generatedAt <= IrpIrn.DetailsWindow;

    /// <summary>What in the portal's answer (<paramref name="held"/>) contradicts the IRN being recorded; empty when the portal confirms it.</summary>
    public static IReadOnlyList<string> Contradictions(IrpIrn held, string irn, string ackNo, EInvoiceDocument document, string? sellerGstin)
    {
        if (!string.Equals(held.Irn, irn, StringComparison.OrdinalIgnoreCase))
        {
            return [$"The portal answered for IRN {held.Irn}."];
        }

        if (held.IsCancelled)
        {
            return ["The portal holds it as cancelled."];
        }

        // An answer that does not say the IRN is active does not confirm it: it may be cancelled there.
        if (!held.IsActive)
        {
            return [held.Status is { } status ? $"The portal holds it as {status}, not active." : "The portal's answer does not say whether it is active."];
        }

        var problems = new List<string>();
        if (!string.Equals(held.AckNo.Trim(), ackNo.Trim(), StringComparison.Ordinal))
        {
            problems.Add($"The portal's acknowledgement number for it is {held.AckNo}, not {ackNo.Trim()}.");
        }

        var qr = SignedQrCode.Read(held.SignedQrCode);
        if (qr.IsFailure)
        {
            problems.Add("The portal's signed QR code for it cannot be read.");
        }
        else if (SignedQrCode.Mismatches(qr.Value, document, sellerGstin) is { Count: > 0 } mismatches)
        {
            problems.Add($"The portal's signed QR code for it is not this {InvoiceRegistrationPayload.Noun(document)}'s: {string.Join(" ", mismatches)}");
        }

        return problems;
    }

    /// <summary>
    /// The signed QR code printed for an IRN recorded by hand, and the time the IRN was generated. Confirmed by the portal
    /// (<paramref name="held"/>), they are the portal's own, as for an IRN HC360 asks the portal for: HC360 does not hold the
    /// portal's key, so the pasted code's signature is never checked, and a code damaged or altered there, or in what the
    /// check does not compare, would otherwise be printed. The portal not asked, they are the pasted code and its time.
    /// </summary>
    public static (string SignedQrCode, DateTimeOffset GeneratedAt) Recorded(IrpIrn? held, string pastedQrCode, DateTimeOffset pastedGeneratedAt) =>
        held is { SignedQrCode.Length: > 0 } ? (held.SignedQrCode, held.AckDate) : (pastedQrCode.Trim(), pastedGeneratedAt);
}
