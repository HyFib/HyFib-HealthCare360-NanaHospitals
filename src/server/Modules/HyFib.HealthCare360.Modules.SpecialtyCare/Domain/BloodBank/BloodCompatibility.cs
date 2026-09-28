namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

public enum AboGroup
{
    A = 0,
    B,
    AB,
    O,

    /// <summary>Oh phenotype: no A, B or H antigen; plasma has anti-A, anti-B and anti-H.</summary>
    Bombay,
}

/// <summary>
/// ABO/Rh(D) compatibility table (BB-06). Pure and exhaustive so it can be tested cell by cell:
/// <list type="bullet">
/// <item>Red cells (PRBC): the donor's antigens must not meet the recipient's antibodies; Rh(D)-negative recipients get Rh(D)-negative cells.</item>
/// <item>Whole blood: carries donor plasma as well, so it must be ABO-identical (and Rh compatible).</item>
/// <item>Plasma, platelets, cryoprecipitate: the donor's antibodies must not meet the recipient's red cells.</item>
/// </list>
/// An unknown group on either side is never compatible — the blood bank must group the sample first.
/// </summary>
public static class BloodCompatibility
{
    public static AboGroup? Abo(BloodGroup group) => group switch
    {
        BloodGroup.APositive or BloodGroup.ANegative => AboGroup.A,
        BloodGroup.BPositive or BloodGroup.BNegative => AboGroup.B,
        BloodGroup.ABPositive or BloodGroup.ABNegative => AboGroup.AB,
        BloodGroup.OPositive or BloodGroup.ONegative => AboGroup.O,
        BloodGroup.BombayPositive or BloodGroup.BombayNegative => AboGroup.Bombay,
        _ => null,
    };

    public static bool IsKnown(BloodGroup group) => Abo(group) is not null;

    public static bool IsRhDPositive(BloodGroup group) =>
        group is BloodGroup.APositive or BloodGroup.BPositive or BloodGroup.ABPositive or BloodGroup.OPositive or BloodGroup.BombayPositive;

    public static bool ContainsRedCells(BloodComponentType component) => component is BloodComponentType.WholeBlood or BloodComponentType.PRBC;

    public static bool IsCompatible(BloodComponentType component, BloodGroup donor, BloodGroup recipient) => component switch
    {
        BloodComponentType.WholeBlood => WholeBloodCompatible(donor, recipient),
        BloodComponentType.PRBC => RedCellsCompatible(donor, recipient),
        _ => PlasmaCompatible(donor, recipient),
    };

    public static bool RedCellsCompatible(BloodGroup donor, BloodGroup recipient)
    {
        if (Abo(donor) is not { } d || Abo(recipient) is not { } r)
        {
            return false;
        }

        var abo = r switch
        {
            // Bombay recipients have anti-H and react with every red cell except Bombay cells.
            AboGroup.Bombay => d == AboGroup.Bombay,
            AboGroup.AB => true,
            AboGroup.A => d is AboGroup.A or AboGroup.O or AboGroup.Bombay,
            AboGroup.B => d is AboGroup.B or AboGroup.O or AboGroup.Bombay,
            _ => d is AboGroup.O or AboGroup.Bombay,
        };

        return abo && RhCompatible(donor, recipient);
    }

    public static bool WholeBloodCompatible(BloodGroup donor, BloodGroup recipient) =>
        Abo(donor) is { } d && Abo(recipient) is { } r && d == r && RhCompatible(donor, recipient);

    public static bool PlasmaCompatible(BloodGroup donor, BloodGroup recipient)
    {
        if (Abo(donor) is not { } d || Abo(recipient) is not { } r)
        {
            return false;
        }

        return d switch
        {
            AboGroup.AB => true,
            AboGroup.A => r is AboGroup.A or AboGroup.O or AboGroup.Bombay,
            AboGroup.B => r is AboGroup.B or AboGroup.O or AboGroup.Bombay,
            AboGroup.O => r is AboGroup.O or AboGroup.Bombay,
            _ => r == AboGroup.Bombay,
        };
    }

    /// <summary>Rh(D)-negative recipients must not receive Rh(D)-positive red cells (alloimmunisation, HDFN risk).</summary>
    private static bool RhCompatible(BloodGroup donor, BloodGroup recipient) => !IsRhDPositive(donor) || IsRhDPositive(recipient);
}
