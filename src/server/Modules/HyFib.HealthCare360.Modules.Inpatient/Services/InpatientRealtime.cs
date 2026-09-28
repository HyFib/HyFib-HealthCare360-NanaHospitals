using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Real-time pushes for the bed board (PERF-04: visible on every screen within two seconds). Payloads carry ids and
/// status only; clients refetch details with their own authorisation, so no PHI travels over SignalR.
/// </summary>
internal static class InpatientRealtime
{
    public const string BedsChanged = "beds.changed";
    public const string AdmissionsChanged = "admissions.changed";
    public const string DischargeInitiated = "inpatient.discharge_initiated";

    public static async Task BedsChangedAsync(IRealtimePublisher realtime, IEnumerable<Bed> beds, CancellationToken ct)
    {
        foreach (var bed in beds.DistinctBy(b => b.Id))
        {
            var payload = new { bedId = bed.Id, wardId = bed.WardId, status = bed.Status };
            await realtime.PublishAsync(Topics.Beds(bed.FacilityId), BedsChanged, payload, ct);
            await realtime.PublishAsync(Topics.Ward(bed.WardId), BedsChanged, payload, ct);
        }
    }

    public static Task BedChangedAsync(IRealtimePublisher realtime, Bed bed, CancellationToken ct) => BedsChangedAsync(realtime, [bed], ct);

    public static Task AdmissionChangedAsync(IRealtimePublisher realtime, Admission admission, CancellationToken ct) =>
        realtime.ToFacilityAsync(admission.FacilityId, AdmissionsChanged, new { admissionId = admission.Id, status = admission.Status }, ct);
}
