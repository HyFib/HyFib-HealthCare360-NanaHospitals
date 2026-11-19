using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// What the record kept takes from the duplicate merged into it (<see cref="PatientRegistrationService"/>). The duplicate's
/// whole record moves onto it, so it takes what protects that record too:
/// <list type="bullet">
/// <item>a restriction (VIP): lifting one waits for someone who may open restricted records
/// (<see cref="PatientRecordApprovals"/>), and a merge needs only <c>patients.merge</c>, so a merge never lifts one;</item>
/// <item>the sensitive categories that keep its clinical record confidential (R-4);</item>
/// <item>its ABHA number and Aadhaar, when the record kept has none.</item>
/// </list>
/// </summary>
internal static class MergeCarryOver
{
    /// <summary>Puts on <paramref name="kept"/> what it takes from <paramref name="duplicate"/>; returns what the caller audits.</summary>
    public static MergeCarried Apply(Patient duplicate, Patient kept)
    {
        var restriction = duplicate.IsVip && !kept.IsVip;
        kept.IsVip |= duplicate.IsVip;
        kept.SensitiveCategories |= duplicate.SensitiveCategories;

        var abhaNumber = kept.AbhaNumberCipher is null && duplicate.AbhaNumberCipher is not null;
        if (abhaNumber)
        {
            (kept.AbhaNumberCipher, kept.AbhaNumberIndex, kept.AbhaNumberMasked) = (duplicate.AbhaNumberCipher, duplicate.AbhaNumberIndex, duplicate.AbhaNumberMasked);
        }

        kept.AadhaarLast4 ??= duplicate.AadhaarLast4;
        kept.AadhaarIndex ??= duplicate.AadhaarIndex;
        return new MergeCarried(restriction, abhaNumber);
    }
}

/// <param name="Restriction">The record kept became restricted because the duplicate was.</param>
/// <param name="AbhaNumber">The record kept took the duplicate's ABHA number.</param>
internal sealed record MergeCarried(bool Restriction, bool AbhaNumber);
