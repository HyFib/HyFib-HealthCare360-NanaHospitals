using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>One billable line, identified either by catalogue code or by service item id.</summary>
public sealed record ChargeLine(
    string SourceModule,
    string SourceReference,
    Guid PatientId,
    Guid? VisitId,
    Guid? AdmissionId,
    string? ServiceCode = null,
    Guid? ServiceItemId = null,
    decimal Quantity = 1,
    decimal? UnitPriceOverride = null,
    string? Description = null,
    Guid? PractitionerId = null,
    DateTimeOffset? ServiceDate = null);

public sealed record ChargeOutcome(ChargeStatus Status, string? Error)
{
    public static ChargeOutcome Posted { get; } = new(ChargeStatus.Posted, null);

    public static ChargeOutcome Nothing { get; } = new(ChargeStatus.NotDue, null);
}

/// <summary>
/// Posts this module's charges through <see cref="IChargePoster"/> (implemented by Billing). Posting is idempotent
/// on SourceModule + SourceReference, so a retry after a WiFi drop or a Billing outage never double-charges.
/// Failures are returned, not thrown: the clinical action has already happened and must be recorded; staff can
/// re-post from the record once Billing is available.
/// </summary>
public sealed class SpecialtyChargeService(HospitalDbContext db, IChargePoster poster, IMemoryCache cache)
{
    private static readonly TimeSpan CatalogueCacheDuration = TimeSpan.FromMinutes(10);

    public Task<Guid?> ServiceItemIdAsync(string code, CancellationToken ct) =>
        cache.GetOrCreateAsync($"specialtycare:service-item:{code}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CatalogueCacheDuration;
            return await db.Set<ServiceItem>().AsNoTracking()
                .Where(s => s.Code == code && s.IsActive)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
        });

    public async Task<ChargeOutcome> PostAsync(IReadOnlyList<ChargeLine> lines, CancellationToken ct)
    {
        if (lines.Count == 0)
        {
            return ChargeOutcome.Nothing;
        }

        var errors = new List<string>();
        foreach (var line in lines)
        {
            var itemId = line.ServiceItemId ?? (line.ServiceCode is { } code ? await ServiceItemIdAsync(code, ct) : null);
            if (itemId is null)
            {
                errors.Add($"Service '{line.ServiceCode}' is not in the service catalogue.");
                continue;
            }

            var result = await poster.PostAsync(new ChargeRequest
            {
                PatientId = line.PatientId,
                VisitId = line.VisitId,
                AdmissionId = line.AdmissionId,
                ServiceItemId = itemId.Value,
                Quantity = line.Quantity,
                UnitPriceOverride = line.UnitPriceOverride,
                Description = line.Description,
                SourceModule = line.SourceModule,
                SourceReference = line.SourceReference,
                PractitionerId = line.PractitionerId,
                ServiceDate = line.ServiceDate,
            }, ct);

            if (result.IsFailure)
            {
                errors.Add(result.Error!.Message);
            }
        }

        return errors.Count == 0 ? ChargeOutcome.Posted : new ChargeOutcome(ChargeStatus.Failed, Truncate(string.Join(" ", errors.Distinct())));
    }

    public async Task<ChargeOutcome> CancelAsync(string sourceModule, string sourceReference, string reason, CancellationToken ct)
    {
        var result = await poster.CancelAsync(sourceModule, sourceReference, reason, ct);
        return result.IsSuccess ? new ChargeOutcome(ChargeStatus.Cancelled, null) : new ChargeOutcome(ChargeStatus.Failed, Truncate(result.Error!.Message));
    }

    private static string Truncate(string message) => message.Length <= 500 ? message : message[..500];
}
