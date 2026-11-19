using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// What the surviving record takes from the duplicate merged into it: the duplicate's whole record moves onto it, so it
/// takes what protects that record too. A merge needs only <c>patients.merge</c>, while lifting a restriction waits for
/// someone who may open restricted records, so a merge must never lift one.
/// </summary>
public sealed class MergeCarryOverTests
{
    [Fact]
    public void A_restricted_duplicate_leaves_the_record_kept_restricted()
    {
        var kept = new Patient { Uhid = "NH26000101" };
        var duplicate = new Patient { Uhid = "NH26000102", IsVip = true };

        var carried = MergeCarryOver.Apply(duplicate, kept);

        kept.IsVip.ShouldBeTrue();
        carried.Restriction.ShouldBeTrue("the restriction it brought is audited");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Only_a_restriction_the_record_kept_lacked_is_carried(bool duplicateRestricted, bool keptRestricted)
    {
        var kept = new Patient { IsVip = keptRestricted };

        var carried = MergeCarryOver.Apply(new Patient { IsVip = duplicateRestricted }, kept);

        kept.IsVip.ShouldBe(keptRestricted, "a merge neither lifts a restriction nor adds one the duplicate did not have");
        carried.Restriction.ShouldBeFalse();
    }

    [Fact]
    public void The_duplicates_sensitive_categories_are_added_to_the_record_kept()
    {
        var kept = new Patient { SensitiveCategories = SensitiveCategory.Mtp };

        MergeCarryOver.Apply(new Patient { SensitiveCategories = SensitiveCategory.Hiv }, kept);

        kept.SensitiveCategories.ShouldBe(SensitiveCategory.Hiv | SensitiveCategory.Mtp);
    }

    [Fact]
    public void The_duplicates_abha_and_aadhaar_go_to_a_record_kept_without_them()
    {
        var kept = new Patient();
        var duplicate = new Patient
        {
            AbhaNumberCipher = "cipher",
            AbhaNumberIndex = "index",
            AbhaNumberMasked = "XX-XXXX-XXXX-1234",
            AadhaarLast4 = "9012",
            AadhaarIndex = "aadhaar",
        };

        var carried = MergeCarryOver.Apply(duplicate, kept);

        carried.AbhaNumber.ShouldBeTrue("a national health ID linked by the merge is audited");
        (kept.AbhaNumberCipher, kept.AbhaNumberIndex, kept.AbhaNumberMasked).ShouldBe(("cipher", "index", "XX-XXXX-XXXX-1234"));
        (kept.AadhaarLast4, kept.AadhaarIndex).ShouldBe(("9012", "aadhaar"));
    }

    [Fact]
    public void The_record_kept_keeps_its_own_abha_and_aadhaar()
    {
        var kept = new Patient { AbhaNumberCipher = "own", AbhaNumberIndex = "own-index", AbhaNumberMasked = "XX-XXXX-XXXX-5678", AadhaarLast4 = "3456", AadhaarIndex = "own-aadhaar" };

        var carried = MergeCarryOver.Apply(new Patient { AbhaNumberCipher = "cipher", AbhaNumberIndex = "index", AadhaarLast4 = "9012", AadhaarIndex = "aadhaar" }, kept);

        carried.AbhaNumber.ShouldBeFalse();
        (kept.AbhaNumberCipher, kept.AbhaNumberIndex, kept.AbhaNumberMasked).ShouldBe(("own", "own-index", "XX-XXXX-XXXX-5678"));
        (kept.AadhaarLast4, kept.AadhaarIndex).ShouldBe(("3456", "own-aadhaar"));
    }
}
