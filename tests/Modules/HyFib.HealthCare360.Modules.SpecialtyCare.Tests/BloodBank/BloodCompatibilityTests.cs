using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.BloodBank;

/// <summary>ABO/Rh(D) compatibility table (BB-06), checked cell by cell.</summary>
public sealed class BloodCompatibilityTests
{
    private static readonly BloodGroup[] Standard =
    [
        BloodGroup.APositive, BloodGroup.ANegative, BloodGroup.BPositive, BloodGroup.BNegative,
        BloodGroup.ABPositive, BloodGroup.ABNegative, BloodGroup.OPositive, BloodGroup.ONegative,
    ];

    /// <summary>Recipient → red-cell donors it can receive (standard ABO/Rh table).</summary>
    private static readonly Dictionary<BloodGroup, BloodGroup[]> RedCellDonors = new()
    {
        [BloodGroup.ONegative] = [BloodGroup.ONegative],
        [BloodGroup.OPositive] = [BloodGroup.ONegative, BloodGroup.OPositive],
        [BloodGroup.ANegative] = [BloodGroup.ONegative, BloodGroup.ANegative],
        [BloodGroup.APositive] = [BloodGroup.ONegative, BloodGroup.OPositive, BloodGroup.ANegative, BloodGroup.APositive],
        [BloodGroup.BNegative] = [BloodGroup.ONegative, BloodGroup.BNegative],
        [BloodGroup.BPositive] = [BloodGroup.ONegative, BloodGroup.OPositive, BloodGroup.BNegative, BloodGroup.BPositive],
        [BloodGroup.ABNegative] = [BloodGroup.ONegative, BloodGroup.ANegative, BloodGroup.BNegative, BloodGroup.ABNegative],
        [BloodGroup.ABPositive] = Standard,
    };

    /// <summary>Recipient → plasma donors it can receive (reverse of the red-cell table; Rh irrelevant).</summary>
    private static readonly Dictionary<BloodGroup, string[]> PlasmaDonorAbo = new()
    {
        [BloodGroup.OPositive] = ["O", "A", "B", "AB"],
        [BloodGroup.ONegative] = ["O", "A", "B", "AB"],
        [BloodGroup.APositive] = ["A", "AB"],
        [BloodGroup.ANegative] = ["A", "AB"],
        [BloodGroup.BPositive] = ["B", "AB"],
        [BloodGroup.BNegative] = ["B", "AB"],
        [BloodGroup.ABPositive] = ["AB"],
        [BloodGroup.ABNegative] = ["AB"],
    };

    [Fact]
    public void Packed_red_cells_follow_the_abo_rh_table()
    {
        foreach (var recipient in Standard)
        {
            foreach (var donor in Standard)
            {
                BloodCompatibility.IsCompatible(BloodComponentType.PRBC, donor, recipient)
                    .ShouldBe(RedCellDonors[recipient].Contains(donor), $"PRBC {donor} → {recipient}");
            }
        }
    }

    [Fact]
    public void Plasma_components_follow_the_reverse_abo_table_and_ignore_rh()
    {
        foreach (var component in new[] { BloodComponentType.FFP, BloodComponentType.PlateletConcentrate, BloodComponentType.Cryoprecipitate })
        {
            foreach (var recipient in Standard)
            {
                foreach (var donor in Standard)
                {
                    var expected = PlasmaDonorAbo[recipient].Contains(BloodCompatibility.Abo(donor).ToString());
                    BloodCompatibility.IsCompatible(component, donor, recipient).ShouldBe(expected, $"{component} {donor} → {recipient}");
                }
            }
        }
    }

    [Fact]
    public void Whole_blood_must_be_abo_identical_and_rh_compatible()
    {
        BloodCompatibility.IsCompatible(BloodComponentType.WholeBlood, BloodGroup.ONegative, BloodGroup.APositive).ShouldBeFalse("O whole blood carries anti-A");
        BloodCompatibility.IsCompatible(BloodComponentType.WholeBlood, BloodGroup.ANegative, BloodGroup.APositive).ShouldBeTrue();
        BloodCompatibility.IsCompatible(BloodComponentType.WholeBlood, BloodGroup.APositive, BloodGroup.ANegative).ShouldBeFalse();
        BloodCompatibility.IsCompatible(BloodComponentType.WholeBlood, BloodGroup.BPositive, BloodGroup.BPositive).ShouldBeTrue();
    }

    [Fact]
    public void Rh_negative_recipient_never_receives_rh_positive_red_cells()
    {
        foreach (var donor in Standard.Where(BloodCompatibility.IsRhDPositive))
        {
            foreach (var recipient in Standard.Where(g => !BloodCompatibility.IsRhDPositive(g)))
            {
                BloodCompatibility.RedCellsCompatible(donor, recipient).ShouldBeFalse($"{donor} → {recipient}");
            }
        }
    }

    [Fact]
    public void Bombay_recipients_receive_only_bombay_red_cells()
    {
        foreach (var donor in Standard)
        {
            BloodCompatibility.RedCellsCompatible(donor, BloodGroup.BombayPositive).ShouldBeFalse($"{donor} → Bombay");
        }

        BloodCompatibility.RedCellsCompatible(BloodGroup.BombayNegative, BloodGroup.BombayPositive).ShouldBeTrue();
        BloodCompatibility.RedCellsCompatible(BloodGroup.BombayPositive, BloodGroup.BombayNegative).ShouldBeFalse("Rh(D) still applies");
    }

    [Fact]
    public void Bombay_plasma_suits_only_bombay_recipients_but_bombay_recipients_accept_any_plasma()
    {
        BloodCompatibility.PlasmaCompatible(BloodGroup.BombayPositive, BloodGroup.OPositive).ShouldBeFalse("anti-H reacts with O cells");
        BloodCompatibility.PlasmaCompatible(BloodGroup.BombayPositive, BloodGroup.BombayNegative).ShouldBeTrue();
        foreach (var donor in Standard)
        {
            BloodCompatibility.PlasmaCompatible(donor, BloodGroup.BombayPositive).ShouldBeTrue($"{donor} plasma → Bombay");
        }
    }

    [Fact]
    public void Unknown_group_is_never_compatible()
    {
        foreach (var component in Enum.GetValues<BloodComponentType>())
        {
            BloodCompatibility.IsCompatible(component, BloodGroup.Unknown, BloodGroup.ABPositive).ShouldBeFalse();
            BloodCompatibility.IsCompatible(component, BloodGroup.ONegative, BloodGroup.Unknown).ShouldBeFalse();
        }
    }
}
