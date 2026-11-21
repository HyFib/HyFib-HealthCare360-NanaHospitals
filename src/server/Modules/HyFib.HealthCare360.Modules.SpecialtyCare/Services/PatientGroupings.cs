using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// The samples of a patient the blood bank grouped, on every request of the patient in every facility (a patient's group
/// does not change with the branch), for the two-sample rule's check across requests: a request resting on one sample is
/// held while a sample grouped on another request disagrees with it (<see cref="GroupingHistory.Contrary"/>,
/// <see cref="BloodRequest.HeldGiven"/>).
/// </summary>
internal static class PatientGroupings
{
    /// <summary>
    /// The samples grouped on the patient's requests other than <paramref name="request"/>, every one of them (an old sample
    /// that disagrees must not drop out of the comparison), the most recently grouped request first.
    /// </summary>
    public static async Task<List<FoundGrouping>> OtherThanAsync(HospitalDbContext db, BloodRequest request, CancellationToken ct)
    {
        var others = await db.Set<BloodRequest>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.PatientId == request.PatientId && r.Id != request.Id && r.GroupedAt != null)
            .OrderByDescending(r => r.GroupedAt)
            .ToListAsync(ct);
        return [.. others.SelectMany(GroupingHistory.SamplesOf)];
    }

    /// <summary>
    /// A sample grouped on another request of the patient that disagrees with the one sample <paramref name="request"/>
    /// rests on, or null: also for a request that does not rest on one sample, without reading anything.
    /// </summary>
    public static async Task<FoundGrouping?> ContraryAsync(HospitalDbContext db, BloodRequest request, CancellationToken ct) =>
        request.AwaitingSecondSample ? GroupingHistory.Contrary(request.PatientBloodGroup, await OtherThanAsync(db, request, ct)) : null;
}

/// <summary>
/// Keeps a patient's blood groupings one at a time. Each sample the blood bank groups is compared with the patient's other
/// requests, and holds those resting on one sample that it disagrees with; the doctor's override of the two-sample rule
/// reads them too. Two groupings of the patient saved at the same moment would each read the other's request as it was
/// before, and neither would see the disagreement. So grouping a sample and the override each take the patient's
/// transaction-scoped advisory lock before they read the requests, and hold it to their commit.
/// </summary>
internal static class BloodGroupingLocks
{
    /// <summary>Takes the patient's lock, in the transaction that groups the sample or authorises the override: it lasts as long as that transaction.</summary>
    public static async Task LockAsync(HospitalDbContext db, Guid patientId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Lock a patient's blood groupings inside the transaction that groups a sample or authorises the override: the lock lasts as long as that transaction.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Key(patientId)})", ct);
    }

    /// <summary>The lock key of a patient's blood groupings, the same on every server node.</summary>
    internal static long Key(Guid patientId) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"bloodbank.grouping:{patientId:N}")));
}
