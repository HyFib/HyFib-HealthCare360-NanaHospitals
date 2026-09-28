using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Real-time refresh signals for the OT board, dialysis board, physiotherapy worklist and blood bank screens.
/// Payloads carry ids only (ARCHITECTURE §6); screens refetch with their own authorisation.
/// </summary>
internal static class SpecialtyRealtime
{
    public const string OtBookingChanged = "ot.booking.changed";
    public const string DialysisChanged = "dialysis.board.changed";
    public const string PhysioChanged = "physio.worklist.changed";
    public const string BloodBankChanged = "bloodbank.changed";

    /// <summary>OT screens subscribe to <c>ot:{facilityId}</c> (a topic prefix accepted by the events hub).</summary>
    public static string OtTopic(Guid facilityId) => $"ot:{facilityId}";

    public static Task OtChangedAsync(this IRealtimePublisher realtime, Guid facilityId, Guid bookingId, CancellationToken ct) =>
        realtime.PublishAsync(OtTopic(facilityId), OtBookingChanged, new { bookingId }, ct);

    public static Task FacilityChangedAsync(this IRealtimePublisher realtime, Guid facilityId, string eventName, object payload, CancellationToken ct) =>
        realtime.ToFacilityAsync(facilityId, eventName, payload, ct);
}
