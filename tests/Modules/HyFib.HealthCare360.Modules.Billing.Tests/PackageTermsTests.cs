using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A package's price and what it covers change only when two people agree: the rule's parts, row by row. The terms compare
/// by what they decide (covers in any order, whatever their notes say); a new package waits out of use at ₹0 covering
/// nothing; only taking it out of use is made at once; the approver reads every change in words. The integration tests
/// drive them over the API; the rows they leave out are here.
/// </summary>
public sealed class PackageTermsTests
{
    private static readonly Guid Delivery = Guid.Parse("0a000000-0000-0000-0000-000000000001");
    private static readonly Guid Caesarean = Guid.Parse("0a000000-0000-0000-0000-000000000002");
    private static readonly Guid Oxytocin = Guid.Parse("0a000000-0000-0000-0000-000000000003");
    private static readonly Guid Cghs = Guid.Parse("0a000000-0000-0000-0000-000000000004");

    private static readonly PackageCover BedDays = new(null, ServiceCategory.BedCharge, 2m, null, "Two bed days");
    private static readonly PackageCover Drug = new(Oxytocin, null, null, 500m, null);

    private static readonly PackageTerms InUse = PackageTerms.Create(Delivery, 25_000m, null, PackageUse.InPatient, isActive: true, [BedDays, Drug]);

    private static readonly PackageApprovals.Names Names = new(
        new Dictionary<Guid, (string, decimal)>
        {
            [Delivery] = ("PKG-NDEL Normal delivery", 0m),
            [Caesarean] = ("PKG-LSCS Caesarean section", 5m),
            [Oxytocin] = ("Oxytocin injection", 12m),
        },
        new Dictionary<Guid, string> { [Cghs] = "CGHS rates" });

    [Fact]
    public void Terms_compare_by_what_they_decide()
    {
        PackageTerms.Create(Delivery, 25_000m, null, PackageUse.InPatient, true, [Drug, BedDays with { Notes = "Bed days, ward" }])
            .ShouldBe(InUse, "the same covers in another order, with other notes, are no change");
        (InUse with { Price = 26_000m }).ShouldNotBe(InUse);
        (InUse with { ServiceItemId = Caesarean }).ShouldNotBe(InUse, "the service it is billed as carries its GST rate");
        (InUse with { TariffPlanId = Cghs }).ShouldNotBe(InUse);
        (InUse with { UsedFor = PackageUse.Both }).ShouldNotBe(InUse);
        PackageTerms.Create(Delivery, 25_000m, null, PackageUse.InPatient, true, [BedDays with { MaxQuantity = 3m }, Drug]).ShouldNotBe(InUse, "a cap is a change");
        PackageTerms.Create(Delivery, 25_000m, null, PackageUse.InPatient, true, [BedDays]).ShouldNotBe(InUse, "a cover taken away is a change");
        InUse.NotesDiffer(PackageTerms.Create(Delivery, 25_000m, null, PackageUse.InPatient, true, [BedDays with { Notes = "Two days" }, Drug]))
            .ShouldBeTrue("notes saved at once, as they decide nothing");
    }

    [Fact]
    public void A_new_package_waits_out_of_use_at_nothing_covering_nothing()
    {
        var now = InUse.OutOfUse();

        (now.IsActive, now.Price, now.Covers.Count).ShouldBe((false, 0m, 0));
        (now.ServiceItemId, now.TariffPlanId, now.UsedFor).ShouldBe((Delivery, (Guid?)null, PackageUse.InPatient), "they price nobody while it is out of use");
        now.ShouldNotBe(InUse);
    }

    [Fact]
    public void Only_taking_a_package_out_of_use_is_made_at_once()
    {
        InUse.Narrowed(InUse with { IsActive = false }).ShouldBe(InUse with { IsActive = false });
        InUse.Narrowed(InUse with { Price = 20_000m }).ShouldBe(InUse, "a cheaper package waits too: it is a concession to every stay on it");
        InUse.Narrowed(PackageTerms.Create(Delivery, 25_000m, null, PackageUse.InPatient, true, [BedDays])).ShouldBe(InUse, "covering less waits");
        (InUse with { IsActive = false }).Narrowed(InUse).IsActive.ShouldBeFalse("putting it in use waits");
    }

    [Fact]
    public void The_approver_reads_every_change_to_a_package_in_words()
    {
        var asked = PackageTerms.Create(Caesarean, 30_000m, Cghs, PackageUse.Both, isActive: true, [BedDays with { MaxQuantity = 3m }, Drug]);

        PackageApprovals.Changes(InUse.OutOfUse(), asked, Names).Select(c => c.Words).ShouldBe(
        [
            "put in use",
            "price ₹0.00 → ₹30,000.00",
            "billed as PKG-LSCS Caesarean section (GST 5%)",
            "for CGHS rates only",
            "prices stays and out-patient bills",
            "covers every bed charge service, up to 3",
            "covers Oxytocin injection, up to ₹500.00",
        ]);
        PackageApprovals.Changes(InUse, InUse with { Price = 27_500m }, Names).Single().Value.ShouldBe("₹25,000.00 → ₹27,500.00 (+10%)");
        PackageApprovals.Changes(InUse, PackageTerms.Create(Delivery, 25_000m, null, PackageUse.InPatient, true, [BedDays]), Names)
            .Single().Words.ShouldBe("no longer covers Oxytocin injection, up to ₹500.00");
        PackageApprovals.Changes(InUse, InUse with { IsActive = false }, Names).Single().Value
            .ShouldBe("Taken out of use: no stay or bill may choose it; those on it keep it");
        PackageApprovals.Changes(InUse, InUse, Names).ShouldBeEmpty();
    }
}
