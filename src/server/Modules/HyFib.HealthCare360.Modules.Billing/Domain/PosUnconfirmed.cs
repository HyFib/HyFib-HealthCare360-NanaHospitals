using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// What HC360 does with a card/UPI sale whose upload answer was lost (INT-POS). The terminal may have the sale, and the
/// customer may still tap, for the facility's auto-cancel window; Pine Labs' reference to it was in the lost answer, so
/// it cannot be asked about. Until the window passes (plus <see cref="Margin"/>, for clock and network slack) no second
/// sale goes to that terminal, which could charge the customer twice. The window is worked out from the sale's creation
/// time and the auto-cancel setting, so no column holds it.
/// </summary>
public static class PosUnconfirmed
{
    /// <summary>The start of the placeholder transaction id of a sale saved before the terminal was called.</summary>
    public const string PendingPrefix = "pending-";

    /// <summary>The start of the generated transaction id of a sale whose upload answer was lost (the provider's own prefix).</summary>
    public const string IdPrefix = "unconfirmed-";

    /// <summary>Slack added to the terminal's auto-cancel window: the terminal's clock starts when it receives the sale, not when HC360 sent it.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A sale saved before the terminal was called, still without its answer after this long, was dropped (the server
    /// stopped, say): longer than any call to the terminal waits, so a call still in flight is not mistaken for it.
    /// </summary>
    public static readonly TimeSpan PendingStale = TimeSpan.FromMinutes(5);

    /// <summary>How long a sale may still be on the terminal: its auto-cancel minutes (1 to 30, as the terminal is told) plus the margin.</summary>
    public static TimeSpan Window(int autoCancelMinutes) => TimeSpan.FromMinutes(Math.Clamp(autoCancelMinutes, 1, 30)) + Margin;

    public static string Clip(string? text) => text is { Length: > 300 } ? text[..300] : text ?? string.Empty;

    /// <summary>The refusal of a new sale on a terminal that an earlier sale may still be on: what to check and until when.</summary>
    public static Error Refusal(PosTransaction last, TimeSpan window) => Error.Rule(
        "pos.terminal_unconfirmed",
        string.Create(CultureInfo.InvariantCulture,
            $"The last sale of ₹{Money.FormatInr(last.Amount)} at {Clock(last.CreatedAt)} may still be on the terminal. Check the terminal's screen. If the customer paid, record the payment from the slip; otherwise wait until {Clock(last.CreatedAt + window)}, or confirm that the terminal shows no sale."));

    /// <summary>The time of day in hospital time, as the cashier reads it on the wall clock.</summary>
    public static string Clock(DateTimeOffset instant) => BillingTime.ToLocal(instant).ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string UnconfirmedMessage(string? reason) =>
        Clip($"The terminal did not answer, so HC360 cannot tell whether the sale reached it. It may still be on the terminal screen.{(string.IsNullOrWhiteSpace(reason) ? string.Empty : " " + reason)}");

    public static string LapsedMessage(DateTimeOffset since) =>
        Clip($"The terminal never confirmed this sale (sent at {Clock(since)}); the outcome is unknown. The accountants will check it against the settlement. Check the slip before taking the money again.");
}
