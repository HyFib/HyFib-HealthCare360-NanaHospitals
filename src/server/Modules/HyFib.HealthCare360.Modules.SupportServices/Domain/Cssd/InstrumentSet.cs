namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

public enum SterilizationMethod
{
    /// <summary>Autoclave (moist heat).</summary>
    Steam = 0,

    /// <summary>Ethylene oxide, for heat-sensitive items.</summary>
    EthyleneOxide,

    /// <summary>Low-temperature hydrogen peroxide plasma.</summary>
    Plasma,
}

/// <summary>
/// A standard instrument tray or pack (Major tray, LSCS set…) with its count sheet (CSSD-01). The count sheet is what
/// soiled trays are checked against when they come back, and the method and shelf life decide how packs of the set are
/// processed and dated.
/// </summary>
public sealed class InstrumentSet : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public Guid? OwnerDepartmentId { get; set; }
    public SterilizationMethod Method { get; set; }

    /// <summary>Days a sterile pack of this set stays usable after release.</summary>
    public int ShelfLifeDays { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public List<InstrumentSetItem> Items { get; set; } = [];

    public int InstrumentCount => Items.Sum(i => i.Quantity);

    public IReadOnlyList<CountSheetLine> CountSheet => Items.OrderBy(i => i.SortOrder).Select(i => new CountSheetLine(i.Name, i.Quantity)).ToList();

    public void ReplaceItems(IEnumerable<CountSheetLine> items)
    {
        Items.Clear();
        var order = 0;
        foreach (var item in items)
        {
            order += 10;
            Items.Add(new InstrumentSetItem { InstrumentSetId = Id, Name = item.ItemName.Trim(), Quantity = item.Quantity, SortOrder = order });
        }
    }
}

/// <summary>
/// One line of a set's count sheet ("Artery forceps × 6"). Editing the set replaces the count sheet, so the set's audit
/// event lists it as it was and as it is (<c>items</c>).
/// </summary>
[AuditedWithParent]
public sealed class InstrumentSetItem : Entity
{
    public Guid InstrumentSetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int SortOrder { get; set; }
}
