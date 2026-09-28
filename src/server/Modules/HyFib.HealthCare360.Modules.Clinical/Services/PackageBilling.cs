using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The health-checkup package charge (MHC ↔ Billing). A booking is billed as one line — the package's HC-* service
/// item, quantity 1, no practitioner — posted through <see cref="IChargePoster"/> at check-in, which is when the front
/// desk collects the package fee (a walk-in is checked in, and so charged, as it is booked). The component tests carry
/// the booking id on <c>ServiceOrderPlaced</c> and are never priced on their own. Cancelling the booking or marking it a
/// no-show cancels the charge.
/// <para>
/// Billing is always asked before the booking changes, so any refusal leaves the booking exactly as it was: a charge
/// Billing cannot post blocks the check-in (tests would otherwise run unbilled, and Diagnostics gates on this charge),
/// and a charge already on a finalised bill blocks cancellation until billing cancels that bill or issues a credit note.
/// Nothing here saves: the charge is staged in the caller's unit of work and committed with the booking, or not at all.
/// </para>
/// </summary>
internal sealed class PackageBilling(IChargePoster charges)
{
    /// <summary>Billing source module of package charges; Diagnostics reads their state through IBillingStatusProvider.</summary>
    public const string Source = "healthcheck.package";

    /// <summary>Billing's refusal code for a charge that is already on a finalised bill.</summary>
    internal const string ChargeOnFinalisedBill = "billing.charge_billed";

    /// <summary>Billing keeps descriptions and cancel reasons to 300 characters.</summary>
    private const int MaxBillingText = 300;

    /// <summary>Billing key of a booking's charge. Posting is idempotent on it, so a retried check-in never double-charges.</summary>
    public static string Reference(Guid bookingId) => bookingId.ToString();

    /// <summary>Charges the package, then checks the patient in.</summary>
    public async Task<Result> CheckInAsync(PackageBooking booking, Guid? packageServiceItemId, Guid? visitId, DateTimeOffset now, CancellationToken ct)
    {
        var ready = booking.EnsureCanCheckIn();
        if (ready.IsFailure)
        {
            return ready;
        }

        if (packageServiceItemId is not { } serviceItemId)
        {
            return Error.Rule("package.not_billable",
                $"Package {booking.PackageCode} has no billable service item. Open the package and save it to register one, then check in again.");
        }

        var posted = await charges.PostAsync(new ChargeRequest
        {
            PatientId = booking.PatientId,
            VisitId = visitId ?? booking.VisitId,
            ServiceItemId = serviceItemId,
            Quantity = 1,
            Description = Clip($"{booking.PackageName} ({booking.BookingNumber})"),
            SourceModule = Source,
            SourceReference = Reference(booking.Id),
            PractitionerId = null,
            ServiceDate = now,
        }, ct);

        return posted.IsFailure ? posted.Error! : booking.CheckIn(visitId, now);
    }

    /// <summary>Cancels the package charge, then the booking.</summary>
    public Task<Result> CancelAsync(PackageBooking booking, string reason, DateTimeOffset now, CancellationToken ct) =>
        CloseAsync(booking, booking.EnsureCanCancel(), $"Health checkup cancelled: {reason.Trim()}", "cancelled", () => booking.Cancel(reason, now), ct);

    /// <summary>Cancels any package charge, then records the no-show (usually nothing was charged: a no-show never checked in).</summary>
    public Task<Result> NoShowAsync(PackageBooking booking, DateTimeOffset now, CancellationToken ct) =>
        CloseAsync(booking, booking.EnsureCanMarkNoShow(), "Health checkup no-show", "marked as a no-show", () => booking.MarkNoShow(now), ct);

    private async Task<Result> CloseAsync(PackageBooking booking, Result allowed, string chargeReason, string action, Func<Result> close, CancellationToken ct)
    {
        if (allowed.IsFailure)
        {
            return allowed;
        }

        // Unknown or already-cancelled charges succeed, so a booking that never checked in closes without fuss.
        var released = await charges.CancelAsync(Source, Reference(booking.Id), Clip(chargeReason), ct);
        if (released.IsFailure)
        {
            return released.Error!.Code == ChargeOnFinalisedBill
                ? Error.Rule("booking.package_billed",
                    $"The package charge for booking {booking.BookingNumber} is already on a finalised bill, so the checkup cannot be {action}. "
                    + "Ask billing to cancel that bill or issue a credit note first.")
                : released;
        }

        return close();
    }

    private static string Clip(string text) => text.Length <= MaxBillingText ? text : text[..MaxBillingText];
}
