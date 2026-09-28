namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>
/// Supplier master shared across facilities (INV vendor master). GSTIN decides whether purchases are intra-state
/// (CGST + SGST) or inter-state (IGST); the drug licence (Form 20B/21B) is mandatory for medicine suppliers.
/// </summary>
public sealed class Vendor : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Gstin { get; set; }

    /// <summary>For proprietor vendors this is a personal PAN, so it is masked in audit diffs.</summary>
    [SensitiveData]
    public string? Pan { get; set; }

    public string? DrugLicenseNumber { get; set; }
    public DateOnly? DrugLicenseValidUntil { get; set; }
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public Address Address { get; set; } = new();

    /// <summary>Credit period agreed with the vendor (days from invoice).</summary>
    public int PaymentTermsDays { get; set; } = 30;

    /// <summary>Purchase team's rating from 0 to 5 (quality, timeliness, pricing).</summary>
    public decimal Rating { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>GST state code: the first two digits of the GSTIN.</summary>
    public string? StateCode => GstStateCode(Gstin);

    public static string? GstStateCode(string? gstin) => gstin is { Length: >= 2 } ? gstin[..2] : null;

    /// <summary>Inter-state supply (IGST) when both GSTINs are known and their state codes differ.</summary>
    public static bool IsInterState(string? vendorGstin, string? facilityGstin) =>
        GstStateCode(vendorGstin) is { } vendorState && GstStateCode(facilityGstin) is { } facilityState && vendorState != facilityState;
}
