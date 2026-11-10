using System.Globalization;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>What a correction of an implant or consumable line may change: its batch, serial number, expiry and quantity.</summary>
public sealed record MaterialFacts(string? LotNumber, string? SerialNumber, DateOnly? ExpiryDate, decimal Quantity)
{
    public static MaterialFacts Of(SurgeryMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return new(material.LotNumber, material.SerialNumber, material.ExpiryDate, material.Quantity);
    }

    /// <summary>"lot A12 → A21; quantity 2 → 1", or "not used (quantity 2 → 0)".</summary>
    public static string Change(MaterialFacts before, MaterialFacts after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (after.Quantity == 0)
        {
            return $"not used (quantity {Number(before.Quantity)} → 0)";
        }

        var changes = new List<string>();
        Add(changes, "lot", before.LotNumber, after.LotNumber);
        Add(changes, "serial", before.SerialNumber, after.SerialNumber);
        Add(changes, "expiry", before.ExpiryDate?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture), after.ExpiryDate?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture));
        if (before.Quantity != after.Quantity)
        {
            changes.Add($"quantity {Number(before.Quantity)} → {Number(after.Quantity)}");
        }

        return changes.Count == 0 ? "no change" : string.Join("; ", changes);
    }

    private static void Add(List<string> changes, string what, string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add($"{what} {before ?? "none"} → {after ?? "none"}");
        }
    }

    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>The line to correct, the booking it belongs to, its facts as they were when asked and as they should be.</summary>
public sealed record MaterialCorrectionPayload(Guid MaterialId, Guid BookingId, MaterialFacts Before, MaterialFacts Asked);

/// <summary>
/// An implant or consumable line corrected after the operation started (maker-checker slice 10): decided by the OT in-charge
/// (<see cref="SpecialtyCarePermissions.OtMaterialsApprove"/>), never the one who asked.
/// </summary>
internal sealed class MaterialCorrectionApproval(OtClinicalService clinical) : IApprovalAction
{
    public string Kind => OtClinicalService.CorrectionKind;

    public string Title => "Implant or consumable corrected";

    public string ApprovePermission => SpecialtyCarePermissions.OtMaterialsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => clinical.ApplyCorrectionAsync(request, ct);
}
