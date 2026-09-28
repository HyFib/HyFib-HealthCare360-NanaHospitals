namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

public enum DietCategory
{
    Regular = 0,
    Therapeutic,
    ModifiedConsistency,
    Enteral,

    /// <summary>Nil by mouth (NPO): no meals are planned.</summary>
    NilByMouth,
}

/// <summary>
/// Diet master (Normal, Soft, Diabetic, Renal…), shared by all facilities like the service catalogue. The optional
/// charge service is what the daily diet charge posts when diet billing is enabled for a facility.
/// </summary>
public sealed class DietType : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DietCategory Category { get; set; }
    public Guid? ChargeServiceItemId { get; set; }

    /// <summary>Meal rounds a new order of this diet gets unless the prescriber chooses others.</summary>
    public MealSlot DefaultMealSlots { get; set; } = MealSlots.Standard;

    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsNilByMouth => Category == DietCategory.NilByMouth;
}

/// <summary>Maps a clinical diet order's service code to a diet type ("DIET-DIABETIC" or "DIABETIC" → DIABETIC).</summary>
public static class DietCodes
{
    public const string Normal = "NORMAL";
    private const string ServicePrefix = "DIET-";

    public static IReadOnlyList<string> Candidates(string? serviceCode)
    {
        if (string.IsNullOrWhiteSpace(serviceCode))
        {
            return [];
        }

        var code = serviceCode.Trim().ToUpperInvariant();
        return code.StartsWith(ServicePrefix, StringComparison.Ordinal) && code.Length > ServicePrefix.Length
            ? [code, code[ServicePrefix.Length..]]
            : [code];
    }
}
