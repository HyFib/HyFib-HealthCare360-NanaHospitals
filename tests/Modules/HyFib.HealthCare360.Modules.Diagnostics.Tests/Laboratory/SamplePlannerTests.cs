using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Seed;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

public sealed class SamplePlannerTests
{
    [Fact]
    public void Tests_with_the_same_container_specimen_and_bench_share_a_pending_tube()
    {
        var tube = Tube(Seeded("LAB-CBC"));

        SamplePlanner.FindTube([tube], Key("LAB-ESR")).ShouldBe(tube);
    }

    [Fact]
    public void A_different_bench_gets_its_own_tube_even_with_the_same_container()
    {
        // HbA1c (biochemistry) and CBC (haematology) are both EDTA but processed on different benches.
        SamplePlanner.FindTube([Tube(Seeded("LAB-CBC"))], Key("LAB-HBA1C")).ShouldBeNull();
    }

    [Fact]
    public void Serum_chemistry_panels_share_one_plain_tube()
    {
        var tube = Tube(Seeded("LAB-LFT"));

        SamplePlanner.FindTube([tube], Key("LAB-KFT")).ShouldBe(tube);
        SamplePlanner.FindTube([tube], Key("LAB-LIPID")).ShouldBe(tube);
    }

    [Fact]
    public void Post_prandial_glucose_never_shares_the_fasting_tube()
    {
        var fasting = Tube(Seeded("LAB-FBS"));
        var postPrandial = Seeded("LAB-PPBS");
        var ppTube = Tube(postPrandial);

        SamplePlanner.FindTube([fasting], Key("LAB-PPBS")).ShouldBeNull();
        SamplePlanner.FindTube([ppTube], Key("LAB-FBS")).ShouldBeNull();
        ppTube.DedicatedTestId.ShouldBe(postPrandial.Id);
    }

    [Fact]
    public void A_tube_already_drawn_is_not_reused()
    {
        var tube = Tube(Seeded("LAB-CBC"));
        tube.Status = SampleStatus.Collected;

        SamplePlanner.FindTube([tube], Key("LAB-ESR")).ShouldBeNull();
    }

    [Fact]
    public void Outsourced_tests_travel_in_their_own_tube()
    {
        var test = Seeded("LAB-VITD");
        var inHouse = Tube(test);

        SamplePlanner.FindTube([inHouse], SamplePlanner.KeyFor(test, Guid.CreateVersion7())).ShouldBeNull();
    }

    private static LabTest Seeded(string code)
    {
        var seed = LabCatalog.Orderables.Single(o => o.Code == code);
        return new LabTest
        {
            Code = seed.Code,
            Name = seed.Name,
            Section = seed.Section,
            SpecimenType = seed.Specimen,
            Container = seed.Container,
            DrawSeparately = seed.DrawSeparately,
        };
    }

    private static SampleKey Key(string code) => SamplePlanner.KeyFor(Seeded(code), null);

    private static LabSample Tube(LabTest test)
    {
        var key = SamplePlanner.KeyFor(test, null);
        var tube = Sample(SampleStatus.Pending, key.Section);
        tube.Container = key.Container;
        tube.SpecimenType = key.Specimen;
        tube.DedicatedTestId = key.DedicatedTestId;
        return tube;
    }
}
