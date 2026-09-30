using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

/// <summary>What the dispensing rules need to know about an item being sold.</summary>
public sealed record SaleCandidate(string ItemName, DrugSchedule Schedule, bool IsControlled)
{
    public bool RequiresPrescription => Item.NeedsPrescription(Schedule, IsControlled);

    public bool RequiresRegister => Item.NeedsRegister(Schedule, IsControlled);
}

/// <summary>
/// Drugs &amp; Cosmetics Rules at the counter (PH-02, ERX-06), pure and unit-tested:
/// Schedule H, H1, X and NDPS medicines are never sold without a prescription; H1/X sales also need the buyer's
/// identity for the register; and controlled medicines need an explicit confirmation before they are handed over.
/// </summary>
public static class SchedulePolicy
{
    public static Result CheckCounterSale(IReadOnlyCollection<SaleCandidate> items, bool hasPrescription, bool hasBuyerIdentity)
    {
        var prescriptionOnly = items.Where(i => i.RequiresPrescription).Select(i => i.ItemName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (prescriptionOnly.Count > 0 && !hasPrescription)
        {
            var required = Error.Rule("pharmacy.prescription_required", $"A prescription is required to sell {string.Join(", ", prescriptionOnly)} (Schedule H/H1/X).");
            return required with { Data = new { items = prescriptionOnly } };
        }

        if (items.Any(i => i.RequiresRegister) && !hasBuyerIdentity)
        {
            return Error.Rule("pharmacy.buyer_required", "Enter the patient's name and address: Schedule H1/X medicines are recorded in the register.");
        }

        return Result.Success();
    }

    public static Result CheckControlledConfirmation(IReadOnlyCollection<SaleCandidate> items, bool confirmed)
    {
        var controlled = items.Where(i => i.RequiresRegister).Select(i => i.ItemName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (controlled.Count == 0 || confirmed)
        {
            return Result.Success();
        }

        var unconfirmed = Error.Rule("pharmacy.controlled_confirmation_required", $"Confirm the controlled medicines before dispensing: {string.Join(", ", controlled)}.");
        return unconfirmed with { Data = new { items = controlled } };
    }
}

/// <param name="PrescribedItemId">Item the doctor picked from the drug master (null for a free-text or generic order).</param>
/// <param name="PrescribedGeneric">Generic name on the prescription (or of the prescribed item).</param>
public sealed record SubstitutionCheck(
    Guid? PrescribedItemId,
    string? PrescribedGeneric,
    string? PrescribedStrength,
    Guid DispensedItemId,
    string? DispensedGeneric,
    string? DispensedStrength,
    string? Reason,
    bool PrescriberConsent);

/// <summary>
/// Generic substitution (PH-01 "substitution with doctor consent"), pure and unit-tested. Dispensing the
/// prescribed item — or any brand of a generically prescribed drug in the same strength — is not a substitution.
/// Anything else is, and must be a generic equivalent (same generic and strength when known), with a reason and
/// the prescriber's consent recorded. The result says whether the line is a substitution.
/// </summary>
public static class SubstitutionPolicy
{
    public static Result<bool> Evaluate(SubstitutionCheck check)
    {
        if (check.PrescribedItemId == check.DispensedItemId)
        {
            return false;
        }

        var equivalent = IsEquivalent(check);
        if (check.PrescribedItemId is null && equivalent == true)
        {
            // The doctor prescribed the generic; the pharmacist chose the brand in stock.
            return false;
        }

        if (equivalent == false)
        {
            return Error.Rule("pharmacy.substitution_not_equivalent", "Only a generic equivalent of the same strength can be substituted.");
        }

        if (string.IsNullOrWhiteSpace(check.Reason))
        {
            return Error.Field("substitutionReason", "Give the reason for the substitution.");
        }

        return check.PrescriberConsent
            ? true
            : Error.Rule("pharmacy.substitution_consent", "Record the prescriber's consent before substituting.");
    }

    /// <summary>null when equivalence cannot be judged (generic unknown on either side).</summary>
    internal static bool? IsEquivalent(SubstitutionCheck check)
    {
        if (string.IsNullOrWhiteSpace(check.PrescribedGeneric) || string.IsNullOrWhiteSpace(check.DispensedGeneric))
        {
            return null;
        }

        if (Normalize(check.PrescribedGeneric) != Normalize(check.DispensedGeneric))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(check.PrescribedStrength) || string.IsNullOrWhiteSpace(check.DispensedStrength)
            || Normalize(check.PrescribedStrength) == Normalize(check.DispensedStrength);
    }

    internal static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
