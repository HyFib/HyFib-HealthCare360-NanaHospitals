using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>
/// A medicine from the drug master for the prescriber's picker (ERX-01). Fill the prescription line from it:
/// <c>drugItemId = id</c>, <c>drugName = name</c>, <c>genericName</c>, <c>strength</c>, <c>dosageForm</c>,
/// <c>isControlledDrug = isControlled</c> and <c>controlledSchedule</c>.
/// <see cref="IsControlled"/> means Schedule H1/X or an NDPS (narcotic/psychotropic) item — the lines that need the
/// prescriber's explicit confirmation and the controlled-drug register; <see cref="ControlledSchedule"/> is then "X"
/// for Schedule X and "H1" otherwise. <see cref="Available"/> and <see cref="NearestExpiry"/> cover the active
/// pharmacy stores of the signed-in facility: null when the facility has no pharmacy store (unknown), 0 when out of
/// stock.
/// </summary>
public sealed record PrescribableDrug(
    Guid Id,
    string Code,
    string Name,
    string? GenericName,
    string? BrandName,
    string? Strength,
    string? DosageForm,
    DrugSchedule Schedule,
    bool IsControlled,
    string? ControlledSchedule,
    bool RequiresPrescription,
    bool IsColdChain,
    string BaseUnit,
    decimal? Available,
    DateOnly? NearestExpiry);

/// <summary>Sellable stock of one item summed over the facility's pharmacy stores, with the nearest usable expiry.</summary>
public sealed record PharmacyStock(decimal Available, DateOnly? NearestExpiry);
